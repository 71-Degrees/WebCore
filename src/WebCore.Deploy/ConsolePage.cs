/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net;
using System.Text;

namespace WebCore.Deploy;

/// <summary>The HTML of the deploy console: one card per group of actions, each action a link, and a pane that shows the running command's output.</summary>
internal static class ConsolePage
{
    public static string Render(IReadOnlyList<ConsoleAction> actions, string token)
    {
        var html = new StringBuilder(Head);
        foreach (var group in actions.GroupBy(a => a.Group))
        {
            html.Append("<section class=\"card\"><h2>").Append(WebUtility.HtmlEncode(group.Key)).Append("</h2><ul>");
            foreach (var action in group)
            {
                var danger = action.Confirm is not null && action.Confirm.Contains("PRODUCTION", StringComparison.Ordinal) ? " live" : string.Empty;
                html.Append("<li><a href=\"#").Append(action.Id).Append("\" class=\"act").Append(danger).Append("\" data-id=\"").Append(action.Id).Append('"');
                if (action.Confirm is not null)
                {
                    html.Append(" data-confirm=\"").Append(WebUtility.HtmlEncode(action.Confirm)).Append('"');
                }

                html.Append('>').Append(WebUtility.HtmlEncode(action.Label)).Append("</a></li>");
            }

            html.Append("</ul></section>");
        }

        html.Append(Tail.Replace("__TOKEN__", token, StringComparison.Ordinal));
        return html.ToString();
    }

    private const string Head = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Deploy Console</title>
<style>
:root { --bg:#f6f7f9; --card:#ffffff; --text:#1b2330; --muted:#4b5565; --line:#cfd5df; --link:#0b57d0; --live:#a4161a; --ok:#0b6b3a; --bad:#a4161a; --out-bg:#10151d; --out-text:#e6eaf0; }
@media (prefers-color-scheme: dark) { :root { --bg:#0e131b; --card:#18202c; --text:#e8ecf2; --muted:#a9b3c3; --line:#34404f; --link:#8ab4ff; --live:#ff9aa0; --ok:#6ee7a0; --bad:#ff9aa0; --out-bg:#080b10; --out-text:#e6eaf0; } }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:16px/1.5 system-ui,Segoe UI,sans-serif; }
main { max-width:960px; margin:0 auto; padding:16px; }
h1 { margin:8px 0 4px; font-size:1.5rem; }
p.lede { margin:0 0 16px; color:var(--muted); }
.grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(280px,1fr)); gap:16px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 16px; }
h2 { margin:0 0 8px; font-size:1.05rem; }
ul { list-style:none; margin:0; padding:0; }
li { margin:2px 0; }
a.act { display:block; padding:8px 4px; color:var(--link); text-decoration:underline; border-radius:6px; }
a.act:hover, a.act:focus-visible { background:var(--bg); }
a.act.live { color:var(--live); font-weight:600; }
a.act.busy { pointer-events:none; opacity:.6; }
#state { margin:16px 0 8px; font-weight:600; }
#state.ok { color:var(--ok); } #state.bad { color:var(--bad); }
pre { margin:0; min-height:12rem; max-height:60vh; overflow:auto; background:var(--out-bg); color:var(--out-text); border-radius:10px; padding:12px; font:13px/1.45 Consolas,monospace; white-space:pre-wrap; word-break:break-word; }
</style>
</head>
<body>
<main>
<h1>Deploy Console</h1>
<p class="lede">Each link runs the same <code>tools/Deploy</code> command you would type. One at a time. Red links change production.</p>
<div class="grid">
""";

    private const string Tail = """
</div>
<div id="state" role="status" aria-live="polite">Ready.</div>
<pre id="out" aria-live="polite" tabindex="0"></pre>
</main>
<script>
const token = "__TOKEN__";
const out = document.getElementById("out");
const state = document.getElementById("state");
let running = false;

function setBusy(on) {
  running = on;
  document.querySelectorAll("a.act").forEach(a => a.classList.toggle("busy", on));
}

async function run(link) {
  const confirmText = link.dataset.confirm;
  if (running || (confirmText && !window.confirm(confirmText))) { return; }
  setBusy(true);
  out.textContent = "";
  state.className = "";
  state.textContent = "Running: " + link.textContent + " ...";
  let exit = null;
  try {
    const response = await fetch("/run?action=" + encodeURIComponent(link.dataset.id), { method: "POST", headers: { "X-Console-Token": token } });
    if (!response.ok || !response.body) {
      out.textContent = await response.text();
      state.className = "bad";
      state.textContent = "Not run (" + response.status + ").";
      return;
    }
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) { break; }
      out.textContent += decoder.decode(value, { stream: true });
      out.scrollTop = out.scrollHeight;
    }
    const match = out.textContent.match(/@@EXIT (-?\d+)\s*$/);
    if (match) { exit = Number(match[1]); out.textContent = out.textContent.replace(/@@EXIT -?\d+\s*$/, ""); }
  } catch (error) {
    out.textContent += "\n" + error;
  } finally {
    setBusy(false);
  }
  state.className = exit === 0 ? "ok" : "bad";
  state.textContent = exit === 0 ? "Done: " + link.textContent : "Finished with a problem (exit code " + (exit ?? "unknown") + "): " + link.textContent;
}

document.querySelectorAll("a.act").forEach(link => link.addEventListener("click", event => { event.preventDefault(); run(link); }));
</script>
</body>
</html>
""";
}
