using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AgentApp.Editor;

public sealed class BrowserBridge : IDisposable
{
    private static readonly int[] Ports = { 47832, 47833, 47834, 47835, 47836, 47837, 47838 };

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<PendingRequest> _queue = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _inflight = new();

    private Task? _acceptLoop;
    private volatile bool _pageEverConnected;
    private DateTime _lastPollAt = DateTime.MinValue;

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/";
    public bool IsPageConnected =>
        _pageEverConnected &&
        (DateTime.UtcNow - _lastPollAt) < TimeSpan.FromSeconds(35);

    public event Action? StateChanged;

    private sealed class PendingRequest
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Method { get; set; } = "POST";
        public string Url { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new();
        public string Body { get; set; } = "";
        public bool Streaming { get; set; }
        public ConcurrentQueue<string> Chunks { get; } = new();
        public SemaphoreSlim ChunkSignal { get; } = new(0, int.MaxValue);
        public volatile bool StreamDone;
        public string? StreamError;
        public TaskCompletionSource<BridgeResponse> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class BridgeResponse
    {
        public int Status { get; set; }
        public string Body { get; set; } = "";
        public string? Error { get; set; }
    }

    public void Start()
    {
        if (IsRunning) return;
        Exception? last = null;
        foreach (var port in Ports)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port; IsRunning = true; break;
            }
            catch (Exception ex) { last = ex; }
        }
        if (!IsRunning)
            throw new Exception("не удалось занять порт 47832-47838: " + (last?.Message ?? "unknown"));
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        IsRunning = false;
        _pageEverConnected = false;
        _lastPollAt = DateTime.MinValue;

        foreach (var kv in _inflight)
        {
            kv.Value.StreamError = "bridge stopped";
            kv.Value.StreamDone = true;
            try { kv.Value.ChunkSignal.Release(); } catch { }
            kv.Value.Tcs.TrySetResult(new BridgeResponse { Error = "bridge stopped" });
        }
        _inflight.Clear();
        StateChanged?.Invoke();
    }

    // ── Non-streaming (оставлен для совместимости) ────────────────────────
    public async Task<BridgeResponse> SendAsync(
        string url, string method,
        Dictionary<string, string> headers, string body,
        TimeSpan? timeout = null)
    {
        if (!IsRunning) return new BridgeResponse { Error = "bridge not running" };
        if (!IsPageConnected) return new BridgeResponse { Error = "bridge page not connected" };

        var pr = new PendingRequest { Method = method, Url = url, Headers = headers, Body = body };
        _queue.Enqueue(pr);

        var to = timeout ?? TimeSpan.FromMinutes(5);
        var done = await Task.WhenAny(pr.Tcs.Task, Task.Delay(to));
        if (done != pr.Tcs.Task)
        {
            _inflight.TryRemove(pr.Id, out _);
            return new BridgeResponse { Error = "bridge timeout" };
        }
        return await pr.Tcs.Task;
    }

    // ── Streaming — Yield чанки SSE по мере поступления ──────────────────
    public async IAsyncEnumerable<string> SendStreamingAsync(
        string url, string method,
        Dictionary<string, string> headers, string body,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsRunning) throw new InvalidOperationException("bridge not running");
        if (!IsPageConnected) throw new InvalidOperationException("bridge page not connected");

        var pr = new PendingRequest
        {
            Method = method, Url = url, Headers = headers, Body = body, Streaming = true
        };
        _queue.Enqueue(pr);

        try
        {
            var lastChunkAt = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool anyChunk = false;
                while (pr.Chunks.TryDequeue(out var chunk))
                {
                    lastChunkAt = DateTime.UtcNow;
                    anyChunk = true;
                    yield return chunk;
                }
                if (anyChunk) continue;

                if (!string.IsNullOrEmpty(pr.StreamError))
                    throw new Exception("bridge stream error: " + pr.StreamError);

                if (pr.StreamDone) break;

                // Stall-детекция: если вкладка молчит >90 сек, стрим
                // завершается — не висим вечно.
                if ((DateTime.UtcNow - lastChunkAt) > TimeSpan.FromSeconds(90))
                    throw new Exception("bridge stream stalled (no data for 90s)");

                try { await pr.ChunkSignal.WaitAsync(250, ct); }
                catch (OperationCanceledException) { throw; }
            }

            while (pr.Chunks.TryDequeue(out var chunk))
                yield return chunk;
        }
        finally
        {
            _inflight.TryRemove(pr.Id, out _);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleContextAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            string path = req.Url?.AbsolutePath ?? "/";

            if (path == "/" || path == "/index.html") { await WriteHtmlAsync(resp, PageHtml); return; }
            if (path == "/ping") { await WriteJsonAsync(resp, new { ok = true, port = Port }); return; }
            if (path == "/poll") { await HandlePollAsync(resp); return; }
            if (path == "/chunk" && req.HttpMethod == "POST") { await HandleChunkAsync(req, resp); return; }
            if (path == "/response" && req.HttpMethod == "POST") { await HandleResponseAsync(req, resp); return; }

            resp.StatusCode = 404;
            resp.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    private async Task HandlePollAsync(HttpListenerResponse resp)
    {
        _lastPollAt = DateTime.UtcNow;
        if (!_pageEverConnected) { _pageEverConnected = true; StateChanged?.Invoke(); }

        PendingRequest? pr = null;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (_queue.TryDequeue(out pr)) break;
            await Task.Delay(150);
        }

        if (pr == null) { resp.StatusCode = 204; resp.Close(); return; }

        _inflight[pr.Id] = pr;
        await WriteJsonAsync(resp, new
        {
            id = pr.Id,
            method = pr.Method,
            url = pr.Url,
            headers = pr.Headers,
            body = pr.Body,
            streaming = pr.Streaming
        });
    }

    private async Task HandleChunkAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
        string raw = await sr.ReadToEndAsync();

        string? id = null; string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            if (root.TryGetProperty("text", out var tEl)) text = tEl.GetString();
        }
        catch { }

        if (id != null && text != null && _inflight.TryGetValue(id, out var pr))
        {
            pr.Chunks.Enqueue(text);
            try { pr.ChunkSignal.Release(); } catch { }
        }
        await WriteJsonAsync(resp, new { ok = true });
    }

    private async Task HandleResponseAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
        string raw = await sr.ReadToEndAsync();

        string? id = null; string? err = null; bool done = false;
        BridgeResponse? parsed = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            if (root.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String)
                err = eEl.GetString();
            if (root.TryGetProperty("done", out var dEl) && dEl.ValueKind == JsonValueKind.True)
                done = true;
            parsed = new BridgeResponse
            {
                Status = root.TryGetProperty("status", out var sEl) && sEl.TryGetInt32(out var sv) ? sv : 0,
                Body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "",
                Error = err
            };
        }
        catch { }

        if (id != null && _inflight.TryGetValue(id, out var pr))
        {
            if (pr.Streaming)
            {
                pr.StreamError = err;
                pr.StreamDone = true;
                try { pr.ChunkSignal.Release(); } catch { }
            }
            else if (done || err != null || parsed != null)
            {
                pr.Tcs.TrySetResult(parsed ?? new BridgeResponse { Error = err ?? "unknown" });
            }
        }
        await WriteJsonAsync(resp, new { ok = true });
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object o)
    {
        byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o));
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = buf.Length;
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        await resp.OutputStream.WriteAsync(buf);
        resp.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, string html)
    {
        byte[] buf = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = buf.Length;
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        await resp.OutputStream.WriteAsync(buf);
        resp.Close();
    }

    public void Dispose() => Stop();

    // ────────────────────────────────────────────────────────────────────
    private const string PageHtml = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
<meta charset=""utf-8"">
<title>MCode Browser Bridge</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:flex-start;justify-content:center;
     background:#0e0e0e;color:#f0f0f0;font-family:system-ui,'Segoe UI',sans-serif;padding:40px 20px;box-sizing:border-box}
.box{text-align:center;max-width:720px;width:100%}
h1{font-size:20px;font-weight:500;margin:0 0 16px}
p{color:#8a8a8a;font-size:14px;line-height:1.65;margin:8px 0}
.dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:#7dc97d;
     margin-right:8px;vertical-align:middle;animation:pulse 1.4s infinite}
.dot.busy{background:#e8a54c}
@keyframes pulse{0%,100%{opacity:.5}50%{opacity:1}}
.stat{margin-top:24px;font-family:Consolas,monospace;font-size:12.5px;color:#6a6a6a}
.stream{margin-top:14px;padding:10px;background:#0a0a0a;border:1px solid #1a1a1a;border-radius:8px;
        text-align:left;font-family:Consolas,monospace;font-size:11.5px;color:#a8a8a8;
        max-height:180px;overflow:auto;white-space:pre-wrap;word-break:break-all}
.sec{margin-top:24px;text-align:left}
.sec-title{font-size:11.5px;color:#6a6a6a;margin-bottom:6px;text-transform:uppercase;letter-spacing:.5px}
pre.pv{background:#0a0a0a;border:1px solid #1a1a1a;border-radius:8px;padding:10px;
       font-size:11.5px;font-family:Consolas,monospace;color:#a8a8a8;
       max-height:240px;overflow:auto;white-space:pre-wrap;word-break:break-word;margin:0}
</style>
</head>
<body>
<div class=""box"">
<h1>MCode Browser Bridge</h1>
<p><span class=""dot"" id=""d""></span><span id=""s"">Готов, жду запросы от приложения...</span></p>
<p>Не закрывай эту вкладку, пока работаешь с ИИ в приложении.<br>
Все AI-запросы идут через сеть этого браузера — можно включить VPN.</p>
<div class=""stat"" id=""stat"">requests: 0 · errors: 0 · streamed: 0</div>
<div class=""stream"" id=""live"">—</div>
<div class=""sec"">
  <div class=""sec-title"">System prompt (последний запрос)</div>
  <pre class=""pv"" id=""sys-prompt-preview"">—</pre>
</div>
</div>
<script>
const d = document.getElementById('d');
const s = document.getElementById('s');
const stat = document.getElementById('stat');
const live = document.getElementById('live');
let req = 0, err = 0, str = 0;
function updateStat(){ stat.textContent = 'requests: ' + req + ' · errors: ' + err + ' · streamed: ' + str; }

async function reportError(id, msg){
  try {
    await fetch('/response', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, error: String(msg) })
    });
  } catch(_) {}
}

async function runStreaming(job){
  str++; updateStat();
  d.classList.add('busy'); s.textContent = 'Стримлю ответ...';
  live.textContent = '';
  try {
    const fr = await fetch(job.url, {
      method: job.method || 'POST',
      headers: job.headers || {},
      body: job.body || undefined,
    });
    if (!fr.ok) {
      const t = await fr.text();
      await reportError(job.id, 'HTTP ' + fr.status + ': ' + t.slice(0, 400));
      return;
    }
    if (!fr.body) {
      const t = await fr.text();
      await fetch('/chunk', { method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ id: job.id, text: t }) });
      await fetch('/response', { method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ id: job.id, done: true }) });
      return;
    }
    const reader = fr.body.getReader();
    const decoder = new TextDecoder();
    let tail = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const text = decoder.decode(value, { stream: true });
      if (!text) continue;
      await fetch('/chunk', { method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ id: job.id, text }) });
      tail = (tail + text).slice(-400);
      live.textContent = tail;
    }
    const rem = decoder.decode();
    if (rem) {
      await fetch('/chunk', { method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ id: job.id, text: rem }) });
    }
    await fetch('/response', { method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ id: job.id, done: true }) });
  } catch (e) {
    err++; updateStat();
    await reportError(job.id, e);
  } finally {
    d.classList.remove('busy'); s.textContent = 'Готов, жду запросы от приложения...';
  }
}

async function runNonStreaming(job){
  d.classList.add('busy'); s.textContent = 'Выполняю запрос...';
  try {
    const fr = await fetch(job.url, {
      method: job.method || 'POST',
      headers: job.headers || {},
      body: job.body || undefined,
    });
    const text = await fr.text();
    await fetch('/response', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: job.id, status: fr.status, body: text }),
    });
  } catch (e) {
    err++; updateStat();
    await reportError(job.id, e);
  } finally {
    d.classList.remove('busy'); s.textContent = 'Готов, жду запросы от приложения...';
  }
}

async function updateSysPreview(job){
  try {
    const parsed = JSON.parse(job.body || '{}');
    const sys = (parsed.messages || []).find(m => m && m.role === 'system');
    if (!sys) return;
    let txt = sys.content;
    if (Array.isArray(txt)) txt = txt.map(b => (b && b.text) ? b.text : JSON.stringify(b)).join('\n');
    txt = String(txt);
    const el = document.getElementById('sys-prompt-preview');
    if (el) el.textContent = txt.length > 30000 ? txt.slice(0, 30000) + '\n…[truncated]' : txt;
  } catch(_) {}
}

async function loop(){
  while (true) {
    try {
      const r = await fetch('/poll', { cache: 'no-store' });
      if (r.status === 204) continue;
      if (!r.ok) { await new Promise(x=>setTimeout(x,500)); continue; }
      const job = await r.json();
      req++; updateStat();
      await updateSysPreview(job);
      if (job.streaming) await runStreaming(job);
      else await runNonStreaming(job);
    } catch (e) {
      await new Promise(x=>setTimeout(x,500));
    }
  }
}
loop();
</script>
</body>
</html>";
}
