/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Timing;

/// <summary>
/// Runs an action once a key has been quiet for a short while: every new <see cref="Trigger"/> for the same key cancels the one still waiting (and signals the token of one already running), so a burst of
/// keystrokes becomes a single save or check shortly after the person pauses. Keys are independent of each other. Time comes from the <see cref="TimeProvider"/>, so tests can drive it.
/// <para>
/// The action continues on the synchronization context that called <see cref="Trigger"/> - deliberately, so a UI component's action can touch its own state and ask to re-render. A failing action does not
/// stop anything: the error goes to <c>onError</c> (this library has no logger of its own) and the next trigger runs as usual.
/// </para>
/// </summary>
public sealed class KeyedDebouncer<TKey> : IDisposable
    where TKey : notnull
{
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly Action<Exception> _onError;
    private readonly Dictionary<TKey, CancellationTokenSource> _pending;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>The run the most recent <see cref="Trigger"/> started. Held rather than discarded so it is observed; it never faults (every failure goes to
    /// <c>onError</c>), and a test can await it.</summary>
    internal Task LastRun { get; private set; } = Task.CompletedTask;

    public KeyedDebouncer(TimeSpan delay, Action<Exception> onError, TimeProvider? time = null, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(onError);
        _delay = delay;
        _onError = onError;
        _time = time ?? TimeProvider.System;
        _pending = new Dictionary<TKey, CancellationTokenSource>(comparer);
    }

    /// <summary>Runs <paramref name="action"/> after the delay unless this key is triggered again first. The token it gets is cancelled by a newer trigger, <see cref="Cancel"/> or <see cref="Dispose"/>.</summary>
    public void Trigger(TKey key, Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var current = new CancellationTokenSource();
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            if (_disposed)
            {
                current.Dispose();
                return;
            }

            _pending.TryGetValue(key, out superseded);
            _pending[key] = current;
        }

        superseded?.Cancel();
        LastRun = RunAsync(key, current, action);
    }

    /// <summary>Stops whatever is waiting or running for the key. Nothing happens when there is nothing.</summary>
    public void Cancel(TKey key)
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            _pending.Remove(key, out pending);
        }

        pending?.Cancel();
    }

    /// <summary>Stops everything that is waiting or running, for every key.</summary>
    public void CancelAll()
    {
        List<CancellationTokenSource> all;
        lock (_gate)
        {
            all = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var pending in all)
        {
            pending.Cancel();
        }
    }

    /// <summary>Cancels everything and refuses further triggers.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        CancelAll();
    }

    private async Task RunAsync(TKey key, CancellationTokenSource source, Func<CancellationToken, Task> action)
    {
        try
        {
            // WhenAny never throws for a cancelled delay, so being superseded is an ordinary outcome and not an exception to catch.
            var wait = Task.Delay(_delay, _time, source.Token);
            await Task.WhenAny(wait);
            if (!wait.IsCanceled && !source.IsCancellationRequested)
            {
                await action(source.Token);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            return;   // the action itself noticed it was superseded and stopped: not an error
        }
        catch (Exception ex)
        {
            LogFailure(ex);
        }
        finally
        {
            Forget(key, source);
        }
    }

    // The caller's onError is where a failed action is logged (this library has no logger of its own).
    private void LogFailure(Exception ex) => _onError(ex);

    private void Forget(TKey key, CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, source))
            {
                _pending.Remove(key);
            }
        }

        source.Dispose();
    }
}
