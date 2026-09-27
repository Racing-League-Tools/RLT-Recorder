using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RltUdpClient.Core;

/// <summary>
/// A very small read-only HTTP server: it lists the recorded dumps, lets them be
/// downloaded, and shows what the recorder is doing right now. On a headless box
/// this is both the only way to get files off the machine and the only way to see
/// that anything is happening at all.
///
/// Built on a raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>,
/// which would need an URL ACL or administrator rights on Windows — not something
/// to ask of someone who just wants to record a race.
/// </summary>
public sealed class StatusServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 8 * 1024;
    private const int FallbackPortsToTry = 20;

    private readonly AppConfig _config;
    private readonly RecorderState _state;
    private readonly string _outputDirectory;

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public StatusServer(AppConfig config, RecorderState state, string outputDirectory)
    {
        _config = config;
        _state = state;
        _outputDirectory = outputDirectory;
    }

    /// <summary>The port actually bound, once <see cref="TryStart"/> has succeeded.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Binds and starts serving. Returns false instead of throwing: losing the
    /// file server must never take the recorder down with it.
    /// </summary>
    public bool TryStart(CancellationToken cancellationToken, out string? error)
    {
        var attempts = _config.HttpPortFallback ? FallbackPortsToTry : 1;

        for (var offset = 0; offset < attempts; offset++)
        {
            var port = _config.HttpPort + offset;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();

                _listener = listener;
                Port = port;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(cancellationToken), CancellationToken.None);

                error = null;
                return true;
            }
            catch (SocketException)
            {
                // Port taken; either try the next one or give up and say so.
            }
        }

        error = _config.HttpPortFallback
            ? $"no free port in {_config.HttpPort}-{_config.HttpPort + attempts - 1}"
            : $"port {_config.HttpPort} is already in use";
        return false;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener!;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await HandleAsync(client, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // A broken connection is not worth taking the server down for.
                    }
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var stream = client.GetStream();

        var request = await ReadRequestLineAsync(stream, cancellationToken);
        if (request is null)
            return;

        var (method, target) = request.Value;

        if (!string.Equals(method, "GET", StringComparison.Ordinal))
        {
            await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain", cancellationToken);
            return;
        }

        var path = Uri.UnescapeDataString(target.Split('?')[0]);

        switch (path)
        {
            case "/" or "/index.html":
                await WriteTextAsync(stream, 200, RenderPage(), "text/html; charset=utf-8", cancellationToken);
                return;

            case "/api/status":
                await WriteTextAsync(stream, 200, RenderStatusJson(), "application/json; charset=utf-8", cancellationToken);
                return;
        }

        if (path.StartsWith("/files/", StringComparison.Ordinal))
        {
            await ServeFileAsync(stream, path["/files/".Length..], cancellationToken);
            return;
        }

        await WriteTextAsync(stream, 404, "Not Found", "text/plain", cancellationToken);
    }

    private static async Task<(string Method, string Target)?> ReadRequestLineAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxRequestBytes);
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, MaxRequestBytes), cancellationToken);
            if (read <= 0)
                return null;

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var lineEnd = text.IndexOf('\r');
            if (lineEnd < 0)
                return null;

            var parts = text[..lineEnd].Split(' ');
            return parts.Length < 2 ? null : (parts[0], parts[1]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Serves one recorded dump. The requested name is matched against the files
    /// actually present rather than joined onto a path, so nothing outside the
    /// output directory can be reached however the request is spelled.
    /// </summary>
    private async Task ServeFileAsync(NetworkStream stream, string requestedName, CancellationToken cancellationToken)
    {
        var match = EnumerateDumps()
            .FirstOrDefault(f => string.Equals(f.Name, requestedName, StringComparison.Ordinal));

        if (match is null)
        {
            await WriteTextAsync(stream, 404, "No such dump", "text/plain", cancellationToken);
            return;
        }

        var header = BuildHeader(200, "OK", "application/octet-stream", match.Length,
            $"attachment; filename=\"{match.Name}\"");

        await stream.WriteAsync(header, cancellationToken);

        await using var file = new FileStream(match.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await file.CopyToAsync(stream, cancellationToken);
    }

    private IEnumerable<FileInfo> EnumerateDumps()
    {
        if (!Directory.Exists(_outputDirectory))
            return Array.Empty<FileInfo>();

        return new DirectoryInfo(_outputDirectory)
            .EnumerateFiles("*.dat", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc);
    }

    private static byte[] BuildHeader(int status, string reason, string contentType, long contentLength,
        string? contentDisposition = null)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");

        if (contentDisposition is not null)
            builder.Append("Content-Disposition: ").Append(contentDisposition).Append("\r\n");

        return Encoding.ASCII.GetBytes(builder.Append("\r\n").ToString());
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string body, string contentType,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };

        await stream.WriteAsync(BuildHeader(status, reason, contentType, payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private string RenderStatusJson()
    {
        var snapshot = _state.Snapshot();

        var payload = new StatusPayload(
            Running: snapshot.IsRunning,
            Receiving: snapshot.IsReceiving,
            UdpPort: _config.Port,
            CurrentFile: snapshot.CurrentFile,
            SessionId: snapshot.Status.SessionId,
            GameInfo: snapshot.Status.GameInfo,
            PacketsReceived: snapshot.Status.PacketsReceived,
            PacketsWritten: snapshot.Status.PacketsWritten,
            PacketsFiltered: snapshot.Status.PacketsFiltered,
            CurrentBytes: snapshot.Status.UncompressedBytes,
            SecondsSinceLastPacket: snapshot.SecondsSinceLastPacket,
            Version: AppVersion.Number,
            Files: EnumerateDumps()
                .Select(f => new DumpFileEntry(f.Name, f.Length, f.LastWriteTimeUtc))
                .ToArray());

        return JsonSerializer.Serialize(payload, AppJsonContext.Default.StatusPayload);
    }

    private string RenderPage() => Html
        .Replace("{{UDP_PORT}}", _config.Port.ToString(CultureInfo.InvariantCulture))
        .Replace("{{VERSION}}", AppVersion.Number)
        .Replace("{{LOGO}}", LogoDataUri);

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }
    }

    /// <summary>
    /// The Racing League Tools logo, 64 px WebP, inlined so the page needs nothing
    /// from the recorder beyond this one response.
    /// </summary>
    private const string LogoDataUri = "data:image/webp;base64,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";

    // Styling follows racingleaguetools.com (tokens.css / components.css): dark
    // canvas only, orange accent, Saira Condensed for titles and Inter for the rest.
    // Only the tokens this page uses are copied. Fonts come from Google Fonts and
    // fall back to system faces when the viewer is offline.
    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="dark">
        <meta name="theme-color" content="#0B0D12">
        <title>RLT Recorder</title>
        <link rel="icon" href="{{LOGO}}">
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Saira+Condensed:wght@800&display=swap" rel="stylesheet">
        <style>
          :root {
            --orange:#F2762E; --orange-hover:#FF9152; --orange-soft:rgba(242,118,46,.14);
            --green:#2ED47A; --green-soft:rgba(46,212,122,.14);
            --danger:#E5484D; --danger-soft:rgba(229,72,77,.14);
            --canvas:#0B0D12; --raised:#14161D; --overlay:#1C1F29;
            --hairline:#262B38; --border-strong:rgba(244,245,247,.14); --hover:rgba(244,245,247,.045);
            --text:#F4F5F7; --text-2:#B9BDC7; --on-accent:#0B0D12;
            --font-display:'Saira Condensed','Arial Narrow',sans-serif;
            --font-body:'Inter',system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;
            color-scheme: dark;
          }
          *, *::before, *::after { box-sizing: border-box; }
          [hidden] { display: none !important; }
          html, body { margin: 0; min-height: 100%; }
          body { display: flex; flex-direction: column; min-height: 100vh; background: var(--canvas); color: var(--text);
                 font: 16px/1.5 var(--font-body); -webkit-font-smoothing: antialiased; font-feature-settings: "cv05" 1, "cv11" 1; }
          a { color: var(--orange); text-decoration: none; transition: color 120ms ease; }
          a:hover { color: var(--orange-hover); }
          :focus-visible { outline: 2px solid var(--orange); outline-offset: 2px; }
          ::selection { background: var(--orange); color: var(--on-accent); }
          h1, h2, p { margin: 0; }
          .nums { font-variant-numeric: tabular-nums; }

          .topbar { position: sticky; top: 0; z-index: 20; height: 56px; display: flex; align-items: center;
                    justify-content: space-between; gap: 16px; padding: 0 24px;
                    background: rgba(20,22,29,.82); backdrop-filter: saturate(140%) blur(12px);
                    -webkit-backdrop-filter: saturate(140%) blur(12px); border-bottom: 1px solid var(--hairline); }
          .brand { display: flex; align-items: center; gap: 12px; min-width: 0; color: var(--text); }
          .brand img { width: 32px; height: 32px; border-radius: 9999px; flex-shrink: 0; }
          .brand-name { font: 800 20px/1.2 var(--font-display); letter-spacing: .01em; white-space: nowrap;
                        overflow: hidden; text-overflow: ellipsis; }
          .ver { color: var(--text-2); font-size: 13px; font-weight: 500; white-space: nowrap; }

          main { flex: 1 0 auto; width: 100%; max-width: 800px; margin: 0 auto; padding: 48px 24px 64px; }
          .pill { display: inline-flex; align-items: center; gap: 8px; height: 26px; margin-bottom: 20px; padding: 0 12px;
                  border: 1px solid var(--border-strong); border-radius: 9999px; background: var(--hover); color: var(--text-2);
                  font-size: 12px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; }
          .pill .dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
          .pill.live { color: var(--green); background: var(--green-soft); border-color: rgba(46,212,122,.28); }
          .pill.live .dot { animation: pulse 1.6s ease-in-out infinite; }
          .pill.wait { color: var(--orange); background: var(--orange-soft); border-color: rgba(242,118,46,.28); }
          .pill.down { color: var(--danger); background: var(--danger-soft); border-color: rgba(229,72,77,.28); }
          @keyframes pulse { 50% { opacity: .35; } }
          h1 { font: 800 32px/1.08 var(--font-display); letter-spacing: .005em; margin-bottom: 12px; }
          .lede { color: var(--text-2); margin-bottom: 32px; max-width: 68ch; }
          .lede strong { color: var(--text); font-weight: 600; }

          .card { background: var(--raised); border: 1px solid var(--hairline); border-radius: 10px; padding: 24px; }
          .card + .card { margin-top: 16px; }
          .card-head { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; margin-bottom: 12px; }
          h2 { font-size: 16px; line-height: 1.25; font-weight: 600; letter-spacing: -.005em; }
          .caption { color: var(--text-2); font-size: 12px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; }

          .meta-list { display: grid; margin: 0; }
          .meta-list > div { display: grid; grid-template-columns: minmax(7rem, 12rem) 1fr; gap: 12px; align-items: baseline;
                             padding: 8px 0; border-bottom: 1px solid var(--hairline); }
          .meta-list > div:last-child { border-bottom: 0; padding-bottom: 0; }
          .meta-list dt { color: var(--text-2); font-size: 14px; }
          .meta-list dd { margin: 0; font-size: 14px; word-break: break-word; }

          .files { width: 100%; border-collapse: collapse; }
          .files th { text-align: left; padding: 0 8px 8px; border-bottom: 1px solid var(--hairline); color: var(--text-2);
                      font-size: 12px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; }
          .files td { padding: 10px 8px; border-bottom: 1px solid var(--hairline); font-size: 14px; vertical-align: middle; }
          .files tr:last-child td { border-bottom: 0; }
          .files th:first-child, .files td:first-child { padding-left: 0; }
          .files th:last-child, .files td:last-child { padding-right: 0; }
          .files .name { word-break: break-all; }
          .files .when { display: block; color: var(--text-2); font-size: 13px; }
          .files .size, .files .act { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
          .files .size { color: var(--text-2); }
          .btn { display: inline-flex; align-items: center; justify-content: center; gap: 8px; height: 30px; padding: 0 12px;
                 border-radius: 10px; border: 1px solid var(--border-strong); background: transparent; color: var(--text);
                 font-size: 13px; font-weight: 600; line-height: 1; white-space: nowrap;
                 transition: background-color 120ms ease, border-color 120ms ease, color 120ms ease; }
          .btn:hover { background: var(--hover); color: var(--text); }
          .btn.primary { background: var(--orange); border-color: transparent; color: var(--on-accent); }
          .btn.primary:hover { background: var(--orange-hover); color: var(--on-accent); }
          .btn svg { width: 14px; height: 14px; }

          .empty-state { display: grid; justify-items: center; gap: 12px; padding: 32px 16px 8px; text-align: center; color: var(--text-2); }
          .empty-icon { display: grid; place-items: center; width: 48px; height: 48px; border-radius: 9999px; background: var(--overlay); }
          .empty-icon svg { width: 22px; height: 22px; }
          .empty-title { color: var(--text); font-weight: 600; }

          .site-footer { flex-shrink: 0; border-top: 1px solid var(--hairline); color: var(--text-2); font-size: 13px;
                         padding: 24px; display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
          .site-footer strong { color: var(--text); font-weight: 600; }

          @media (max-width: 640px) {
            .topbar { padding: 0 16px; }
            main { padding: 32px 16px 48px; }
            .card { padding: 16px; }
            .meta-list > div { grid-template-columns: 1fr; gap: 4px; }
            .files .size { display: none; }
            .site-footer { padding: 20px 16px; }
          }
          @media (prefers-reduced-motion: reduce) { * { animation: none !important; transition: none !important; } }
        </style>
        </head>
        <body>
        <header class="topbar">
          <span class="brand">
            <img src="{{LOGO}}" alt="" width="32" height="32">
            <span class="brand-name">RLT Recorder</span>
          </span>
          <span class="ver">v{{VERSION}}</span>
        </header>

        <main>
          <p id="pill" class="pill"><span class="dot"></span><span id="state">Connecting…</span></p>
          <h1>UDP Recorder</h1>
          <p class="lede">Listening for F1 telemetry on UDP port <strong class="nums">{{UDP_PORT}}</strong>.
            Send the recorded dump to your league manager, who imports it in Racing League Tools.</p>

          <section class="card">
            <div class="card-head"><h2>Current recording</h2><span id="session" class="caption nums"></span></div>
            <dl class="meta-list">
              <div><dt>Recording to</dt><dd id="file">—</dd></div>
              <div><dt>Packets written</dt><dd id="written" class="nums">—</dd></div>
              <div><dt>Packets filtered</dt><dd id="filtered" class="nums">—</dd></div>
              <div><dt>Current size</dt><dd id="size" class="nums">—</dd></div>
            </dl>
          </section>

          <section class="card">
            <div class="card-head"><h2>Recorded dumps</h2><span id="count" class="caption nums"></span></div>
            <table id="table" class="files" hidden>
              <thead><tr><th>File</th><th class="size">Size</th><th class="act"></th></tr></thead>
              <tbody id="files"></tbody>
            </table>
            <div id="none" class="empty-state" hidden>
              <span class="empty-icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 22V3h12l-2 5 2 5H5"/></svg></span>
              <p class="empty-title">Nothing recorded yet</p>
              <p>Start a session in the game with UDP telemetry pointed at this machine.</p>
            </div>
          </section>
        </main>

        <footer class="site-footer">
          <span>Racing League Tools · UDP Recorder</span>
          <span>Import: <strong>Database → Replay session from UDP dump</strong></span>
        </footer>

        <script>
        const $ = id => document.getElementById(id);
        const esc = s => s.replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
        const fmt = b => b < 1024 ? b + " B"
          : b < 1048576 ? (b/1024).toFixed(0) + " KB"
          : (b/1048576).toFixed(1) + " MB";
        const when = d => new Date(d).toLocaleString([], {dateStyle:"medium", timeStyle:"short"});
        const icon = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12m0 0-5-5m5 5 5-5M4 21h16"/></svg>';

        function setState(cls, text) {
          $("pill").className = "pill" + (cls ? " " + cls : "");
          $("state").textContent = text;
        }

        async function tick() {
          try {
            const s = await (await fetch("api/status", {cache:"no-store"})).json();

            if (s.receiving) setState("live", "Receiving telemetry");
            else if (s.running) setState("wait", "Waiting for telemetry");
            else setState("", "Stopped");

            $("session").textContent = s.currentFile && s.sessionId ? "Session " + s.sessionId : "";
            $("file").textContent = s.currentFile || "—";
            $("written").textContent = s.packetsWritten.toLocaleString();
            $("filtered").textContent = s.packetsFiltered.toLocaleString();
            $("size").textContent = fmt(s.currentBytes);

            // The newest dump gets the filled button: it is almost always the one wanted.
            $("files").innerHTML = s.files.map((f, i) => {
              const href = "files/" + encodeURIComponent(f.name);
              return `<tr><td class="name"><a href="${href}">${esc(f.name)}</a><span class="when">${when(f.modified)}</span></td>` +
                `<td class="size">${fmt(f.bytes)}</td>` +
                `<td class="act"><a class="btn${i === 0 ? " primary" : ""}" href="${href}" download>${icon}Download</a></td></tr>`;
            }).join("");
            $("count").textContent = s.files.length ? s.files.length + (s.files.length === 1 ? " file" : " files") : "";
            $("table").hidden = s.files.length === 0;
            $("none").hidden = s.files.length > 0;
          } catch {
            setState("down", "Recorder unreachable");
          }
        }

        tick();
        setInterval(tick, 1000);
        </script>
        </body>
        </html>
        """;
}
