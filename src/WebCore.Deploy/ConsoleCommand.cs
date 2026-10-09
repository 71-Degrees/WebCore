/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WebCore.Deploy;

/// <summary>
/// <c>console</c>: a page on this machine only (http://127.0.0.1:5098) with a link for each deploy command, so the owner can bookmark it and click instead of typing. Each click runs the same
/// <c>tools/Deploy</c> command in a child process and streams its output into the page. The listener answers the loopback address only, runs one command at a time, takes only an action id from
/// <see cref="ConsoleActions"/> (never a command line), and wants a token that exists only in the page it served - so another site open in the same browser cannot press the buttons.
/// </summary>
internal static class ConsoleCommand
{
    private const int DefaultPort = 5098;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static async Task<int> RunAsync(Options options)
    {
        var port = options.IntValue("--port") ?? DefaultPort;
        var origin = $"http://127.0.0.1:{port}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var root = DeployTool.RepoRoot();

        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return DeployTool.Fail($"Could not listen on {origin} ({ex.Message}). Is the console already running? Try --port.");
        }

        Console.WriteLine($"Deploy console: {origin}  (Ctrl+C to stop)");
        if (!options.Flag("--no-open"))
        {
            Process.Start(new ProcessStartInfo(origin) { UseShellExecute = true });
        }

        while (true)
        {
            var context = await listener.GetContextAsync();
            Observe(Task.Run(() => HandleAsync(context, origin, token, root)));
        }
    }

    /// <summary>Lets a request run on its own while the listener takes the next one, and makes sure a failure that escapes the handler is reported instead of lost.</summary>
    private static void Observe(Task request) =>
        request.ContinueWith(failed => Console.Error.WriteLine("Console request failed: " + failed.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);

    // The top of this request's code path: whatever goes wrong is logged here and answered, never allowed to stop the listener.
    private static async Task HandleAsync(HttpListenerContext context, string origin, string token, string root)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                await WriteAsync(context, 200, "text/html; charset=utf-8", ConsolePage.Render(ConsoleActions.All, token));
            }
            else if (context.Request.HttpMethod == "POST" && path == "/run")
            {
                await RunActionAsync(context, origin, token, root);
            }
            else
            {
                await WriteAsync(context, 404, "text/plain; charset=utf-8", "Not found.");
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            Console.Error.WriteLine("Console request failed: " + ex.Message);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static async Task RunActionAsync(HttpListenerContext context, string origin, string token, string root)
    {
        var request = context.Request;
        var sameSite = request.Headers["Origin"] is null or "" || request.Headers["Origin"] == origin;
        if (!sameSite || request.Headers["X-Console-Token"] != token)
        {
            await WriteAsync(context, 403, "text/plain; charset=utf-8", "Refused: this page did not come from the console.");
            return;
        }

        var action = ConsoleActions.Find(request.QueryString["action"]);
        if (action is null)
        {
            await WriteAsync(context, 404, "text/plain; charset=utf-8", "Unknown action.");
            return;
        }

        if (!await OneAtATime.WaitAsync(0))
        {
            await WriteAsync(context, 409, "text/plain; charset=utf-8", "Another command is still running. Wait for it to finish.");
            return;
        }

        try
        {
            await StreamAsync(context, action, root);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task StreamAsync(HttpListenerContext context, ConsoleAction action, string root)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.SendChunked = true;
        var stream = context.Response.OutputStream;
        var writer = new object();
        var clientGone = false;

        void Send(string line)
        {
            lock (writer)
            {
                if (clientGone)
                {
                    return;
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                    // The tab was closed. The command keeps going: abandoning a deploy halfway would be worse than finishing it unwatched.
                    clientGone = true;
                    Console.WriteLine("(the page went away; the command carries on)");
                }
            }
        }

        Console.WriteLine($"[{DateTime.Now:T}] {action.Label}: {string.Join(' ', action.Args)}");
        // `dotnet run` and a built Deploy.exe run the launcher's own apphost; `dotnet Deploy.dll` runs it through the dotnet host, which then needs the dll named.
        var host = Environment.ProcessPath ?? "dotnet";
        var info = new ProcessStartInfo(host)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot find the deploy launcher to run."));
        }

        foreach (var arg in action.Args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the deploy command.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Send(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Send(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        process.WaitForExit(); // lets the last output events arrive before the exit line
        Send($"@@EXIT {process.ExitCode}");
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
