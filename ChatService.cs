using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentApp.Editor;

public sealed class ChatModelConfig
{
    public bool IsBuiltin { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public object Parameters { get; set; } = new { type = "object", properties = new { } };
}

public sealed class ToolImage
{
    public string MimeType { get; set; } = "image/png";
    public string Base64 { get; set; } = "";
    public string? Path { get; set; }
    public string? Note { get; set; }
}

public sealed class ChatTurn
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public List<ToolImage>? Images { get; set; }
}

public sealed class ToolExecResult
{
    public string Content { get; set; } = "";
    public int Added { get; set; } = 0;
    public int Removed { get; set; } = 0;
    public string? Extra { get; set; } = null;
    public List<ToolImage>? Images { get; set; } = null;
}

public sealed class ToolStatus
{
    public string Phase { get; set; } = "";
    public string Tool { get; set; } = "";
    public int Added { get; set; } = 0;
    public int Removed { get; set; } = 0;
    public string? Extra { get; set; } = null;
}

public sealed class ChatService
{
    private static readonly byte[] Secret = Convert.FromHexString(
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    private const string ServerBase = "http://msk-3.veroid.net:30005";
    private const int MaxToolRounds = 10000;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // ResponseDrainTimeout=0 — ключ против "Error while copying
        // content to a stream": не ждём дрейна сокета после конца
        // ответа, просто закрываем.
        ResponseDrainTimeout = TimeSpan.Zero,
        AllowAutoRedirect = true,
    }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Активный browser-bridge, если включён dev.browserBridge.</summary>
    public BrowserBridge? Bridge { get; set; }
    public ChatModelConfig? LastModelConfig { get; private set; }

    private readonly string _statePath;
    public string DeviceId { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string BoundEmail { get; private set; } = "";
    public int UsedToday { get; private set; }
    public int DailyLimit { get; private set; }

    public ChatService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentApp");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "device.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                var r = doc.RootElement;
                DeviceId = r.TryGetProperty("device_id", out var d) ? (d.GetString() ?? "") : "";
                Token = r.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : "";
                BoundEmail = r.TryGetProperty("email", out var e) ? (e.GetString() ?? "") : "";
                UsedToday = r.TryGetProperty("used", out var u) && u.TryGetInt32(out var uv) ? uv : 0;
                DailyLimit = r.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 0;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(DeviceId)) DeviceId = Guid.NewGuid().ToString("N");
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new
            {
                device_id = DeviceId, token = Token, email = BoundEmail,
                used = UsedToday, limit = DailyLimit,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    internal static string Encrypt(string plain)
    {
        var nonce = new byte[12]; RandomNumberGenerator.Fill(nonce);
        var pb = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[pb.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Secret, 16);
        aes.Encrypt(nonce, pb, ct, tag);
        var comb = new byte[ct.Length + tag.Length];
        Buffer.BlockCopy(ct, 0, comb, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, comb, ct.Length, tag.Length);
        return Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(comb);
    }

    internal static string Decrypt(string enc)
    {
        var idx = enc.IndexOf('.');
        if (idx < 0) throw new FormatException("bad enc");
        var nonce = Convert.FromBase64String(enc.Substring(0, idx));
        var comb = Convert.FromBase64String(enc.Substring(idx + 1));
        var ct = new byte[comb.Length - 16]; var tag = new byte[16];
        Buffer.BlockCopy(comb, 0, ct, 0, ct.Length);
        Buffer.BlockCopy(comb, ct.Length, tag, 0, 16);
        var pl = new byte[ct.Length];
        using var aes = new AesGcm(Secret, 16);
        aes.Decrypt(nonce, ct, tag, pl);
        return Encoding.UTF8.GetString(pl);
    }

    public async Task RegisterAsync(string email, CancellationToken ct = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        try
        {
            var payload = JsonSerializer.Serialize(new { device_id = DeviceId, email });
            using var req = new HttpRequestMessage(HttpMethod.Post, ServerBase + "/api/register");
            req.Content = new StringContent(Encrypt(payload), Encoding.UTF8, "text/plain");
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {body}");
            using var doc = JsonDocument.Parse(Decrypt(body));
            var r = doc.RootElement;
            if (r.TryGetProperty("token", out var tk)) Token = tk.GetString() ?? "";
            if (r.TryGetProperty("used_today", out var u) && u.TryGetInt32(out var uv)) UsedToday = uv;
            if (r.TryGetProperty("daily_limit", out var l) && l.TryGetInt32(out var lv)) DailyLimit = lv;
            BoundEmail = email; Save();
        }
        catch (Exception ex) { throw new Exception("Register failed: " + ex.Message); }
    }

    public async Task RefreshQuotaAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Token)) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { token = Token });
            using var req = new HttpRequestMessage(HttpMethod.Post, ServerBase + "/api/quota");
            req.Content = new StringContent(Encrypt(payload), Encoding.UTF8, "text/plain");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(Decrypt(await resp.Content.ReadAsStringAsync(ct)));
            var r = doc.RootElement;
            if (r.TryGetProperty("used", out var u) && u.TryGetInt32(out var uv)) UsedToday = uv;
            if (r.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv)) DailyLimit = lv;
            Save();
        }
        catch { }
    }

    /// <summary>
    /// Под-запрос той же модели, без тулов и без истории.
    /// Для tool ask_ai.
    /// </summary>
    public async Task<string> AskSameAsync(string prompt, CancellationToken ct = default)
    {
        var model = LastModelConfig ?? new ChatModelConfig
        {
            IsBuiltin = true, Provider = "openai", Model = "default"
        };

        var sb = new StringBuilder();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var turns = new List<ChatTurn>
        {
            new ChatTurn { Role = "user", Content = prompt }
        };

        try
        {
            await SendChatAsync(
                turns, model, Array.Empty<ToolDefinition>(),
                toolExecutor: _ => Task.FromResult(new ToolExecResult { Content = "{}" }),
                onDelta: t => sb.Append(t),
                onReasoningDelta: _ => { },
                onStatus: _ => { },
                onDone: (_, _) => done.TrySetResult(sb.ToString()),
                onError: err => done.TrySetResult("(ask_ai error: " + err + ")"),
                ct: ct);
        }
        catch (Exception ex) { return "(ask_ai exception: " + ex.Message + ")"; }

        var fin = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3)));
        if (fin != done.Task) return sb.ToString() + "\n[ask_ai: timed out]";
        return await done.Task;
    }

    public async Task SendChatAsync(
        List<ChatTurn> messages, ChatModelConfig? model, ToolDefinition[] tools,
        Func<ToolCall, Task<ToolExecResult>> toolExecutor,
        Action<string> onDelta, Action<string> onReasoningDelta, Action<ToolStatus> onStatus,
        Action<int, int> onDone, Action<string> onError, CancellationToken ct)
    {
        LastModelConfig = model ?? new ChatModelConfig { IsBuiltin = true, Provider = "openai", Model = "default" };

        try
        {
            if (model is null || model.IsBuiltin)
                await RunOpenAILoopAsync(messages, model, tools, toolExecutor, onDelta, onReasoningDelta, onStatus, onDone, onError, useProxy: true, ct);
            else if ((model.Provider ?? "openai").ToLowerInvariant() == "anthropic")
                await RunAnthropicLoopAsync(messages, model, tools, toolExecutor, onDelta, onReasoningDelta, onStatus, onDone, onError, ct);
            else
                await RunOpenAILoopAsync(messages, model, tools, toolExecutor, onDelta, onReasoningDelta, onStatus, onDone, onError, useProxy: false, ct);
        }
        catch (OperationCanceledException) { onDone(UsedToday, DailyLimit); }
        catch (Exception ex) { onError(ex.Message); }
    }

    private async Task RunOpenAILoopAsync(
        List<ChatTurn> messages, ChatModelConfig? model, ToolDefinition[] tools,
        Func<ToolCall, Task<ToolExecResult>> toolExecutor,
        Action<string> onDelta, Action<string> onReasoningDelta, Action<ToolStatus> onStatus,
        Action<int, int> onDone, Action<string> onError,
        bool useProxy, CancellationToken ct)
    {
        int nudgeCount = 0;
        for (int round = 0; round < MaxToolRounds; round++)
        {
            var msgsArr = new List<object>();
            foreach (var t in messages)
            {
                if (t.Role == "tool")
                {
                    msgsArr.Add(new { role = "tool", tool_call_id = t.ToolCallId ?? "", content = t.Content });
                    // vision: если tool вернул картинки — догоняем их отдельным user-
                    // сообщением с image_url блоками (формат совместим с OpenAI vision
                    // и большинством совместимых бэкендов).
                    if (t.Images is { Count: > 0 })
                    {
                        var parts = new List<object> { new { type = "text", text = $"Изображения из результата {t.ToolName}:" } };
                        foreach (var img in t.Images)
                            parts.Add(new { type = "image_url", image_url = new { url = $"data:{img.MimeType};base64,{img.Base64}" } });
                        msgsArr.Add(new { role = "user", content = parts });
                    }
                }
                else if (t.Role == "assistant" && t.ToolCalls is { Count: > 0 })
                {
                    var tcs = t.ToolCalls.Select(tc => new
                    { id = tc.Id, type = "function",
                      function = new { name = tc.Name, arguments = tc.Arguments } }).ToArray();
                    msgsArr.Add(new
                    { role = "assistant",
                      content = string.IsNullOrEmpty(t.Content) ? null : t.Content,
                      tool_calls = tcs });
                }
                else msgsArr.Add(new { role = t.Role, content = t.Content });
            }

            object toolsPayload = tools.Length > 0
                ? tools.Select(t => new { type = "function",
                    function = new { name = t.Name, description = t.Description, parameters = t.Parameters } }).ToArray()
                : Array.Empty<object>();

            var payloadObj = new Dictionary<string, object?>
            {
                ["model"] = model?.Model ?? "default",
                ["messages"] = msgsArr,
                ["stream"] = true,
            };
            if (tools.Length > 0) payloadObj["tools"] = toolsPayload;

            var payloadJson = JsonSerializer.Serialize(payloadObj);
            var (text, toolCalls) = await StreamOpenAIWithRetryAsync(
                payloadJson, model, useProxy, onDelta, onReasoningDelta,
                onToolStart: name => onStatus(new ToolStatus { Phase = "start", Tool = name }),
                ct);

            if (toolCalls.Count == 0)
            {
                // Модель после инструментов нередко возвращает пустой content
                // и никаких новых tool_calls. Молча завершать стрим нельзя —
                // подталкиваем модель дать ответ пользователю. До 2 раз.
                if (string.IsNullOrEmpty(text) && round > 0 && nudgeCount < 50)
                {
                    nudgeCount++;
                    messages.Add(new ChatTurn
                    {
                        Role = "user",
                        Content = "Пожалуйста, ответь пользователю текстом на основе уже полученных результатов."
                    });
                    continue;
                }
                onDone(UsedToday, DailyLimit);
                return;
            }

            messages.Add(new ChatTurn { Role = "assistant", Content = text, ToolCalls = toolCalls });

            foreach (var tc in toolCalls)
            {
                // onStatus{start} уже был отправлен во время стрима (onToolStart)
                ToolExecResult res;
                try { res = await toolExecutor(tc); }
                catch (Exception ex)
                { res = new ToolExecResult { Content = "{\"error\":\"" + ex.Message.Replace("\"","\\\"") + "\"}" }; }
                onStatus(new ToolStatus { Phase = "done", Tool = tc.Name, Added = res.Added, Removed = res.Removed, Extra = res.Extra });
                messages.Add(new ChatTurn { Role = "tool", Content = res.Content, ToolCallId = tc.Id, ToolName = tc.Name, Images = res.Images });
            }
        }
        onError("Tool rounds limit exceeded");
    }

    /// <summary>
    /// Ошибка типа "соединение оборвалось посреди стрима". Трактуем её
    /// как graceful end-of-stream: используем что успели получить.
    /// </summary>
    private static bool IsStreamEndError(Exception ex)
    {
        if (ex is System.IO.IOException) return true;
        if (ex is System.Net.Sockets.SocketException) return true;
        var m = (ex.Message ?? "").ToLowerInvariant();
        return m.Contains("copying content")
            || m.Contains("premature")
            || m.Contains("eof")
            || m.Contains("connection reset")
            || m.Contains("connection closed")
            || m.Contains("response ended")
            || m.Contains("unexpected end")
            || m.Contains("bytes read")
            || m.Contains("stream stalled")
            || m.Contains("bridge stream");
    }

    /// <summary>
    /// Ошибка, после которой имеет смысл ПЕРЕЗАПРОСИТЬ стрим с нуля.
    /// </summary>
    private static bool IsRetryableStreamError(Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException) return true;
        if (ex is System.IO.IOException) return true;
        if (ex is System.Net.Sockets.SocketException) return true;
        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException) return true;
        var m = (ex.Message ?? "").ToLowerInvariant();
        return m.Contains("copying content")
            || m.Contains("stream timeout")
            || m.Contains("premature")
            || m.Contains("connection")
            || m.Contains("eof")
            || m.Contains("429")
            || m.Contains("502")
            || m.Contains("503")
            || m.Contains("504");
    }

    /// <summary>
    /// Обёртка над StreamOpenAIAsync с автоматическим retry при обрыве
    /// соединения. На повторных попытках дельты копятся локально, а в UI
    /// уходит только новая часть (diff против уже показанного).
    /// </summary>
    private async Task<(string text, List<ToolCall> toolCalls)> StreamOpenAIWithRetryAsync(
        string payloadJson, ChatModelConfig? model, bool useProxy,
        Action<string> onDelta, Action<string> onReasoningDelta,
        Action<string>? onToolStart,
        CancellationToken ct)
    {
        const int maxAttempts = 5;
        Exception? lastEx = null;
        string shownText = "";

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            bool first = attempt == 1;
            var buf = new StringBuilder();

            try
            {
                var (text, calls) = await StreamOpenAIAsync(
                    payloadJson, model, useProxy,
                    t => { buf.Append(t); if (first) onDelta(t); },
                    t => { if (first) onReasoningDelta(t); },
                    first ? onToolStart : null,
                    ct);

                if (!first)
                {
                    if (text.StartsWith(shownText))
                    {
                        string rest = text.Substring(shownText.Length);
                        if (rest.Length > 0) onDelta(rest);
                    }
                    else if (text.Length > 0)
                    {
                        onDelta(text);
                    }
                }
                if (attempt > 1)
                    Console.WriteLine($"[STREAM] OK on attempt {attempt}");
                return (text, calls);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"[STREAM] attempt {attempt}/{maxAttempts} failed: {ex.GetType().Name}: {ex.Message}");

                if (first) shownText = buf.ToString();

                if (!IsRetryableStreamError(ex) || attempt >= maxAttempts)
                    throw new Exception($"stream failed after {attempt} attempts: {ex.Message}", ex);

                await Task.Delay(300 * attempt, ct);
            }
        }
        throw lastEx ?? new Exception("stream failed");
    }

    private async Task<(string text, List<ToolCall> toolCalls)> StreamOpenAIAsync(
        string payloadJson, ChatModelConfig? model, bool useProxy,
        Action<string> onDelta, Action<string> onReasoningDelta,
        Action<string>? onToolStart,
        CancellationToken ct)
    {
        // ── Browser bridge: non-stream, но эмулируем один delta ──
        if (Bridge?.IsRunning == true && Bridge.IsPageConnected && !useProxy && model != null)
        {
            return await StreamViaBridgeOpenAIAsync(
                payloadJson, model, onDelta, onReasoningDelta, onToolStart, ct);
        }

        HttpRequestMessage req; HttpResponseMessage resp;

        if (useProxy)
        {
            if (string.IsNullOrEmpty(Token)) throw new Exception("not registered");
            var innerObj = JsonNode.Parse(payloadJson)!.AsObject();
            innerObj["token"] = Token;
            var encrypted = Encrypt(innerObj.ToJsonString());
            req = new HttpRequestMessage(HttpMethod.Post, ServerBase + "/api/chat");
            req.Content = new StringContent(encrypted, Encoding.UTF8, "text/plain");
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        else
        {
            string url = BuildUrl(model!.BaseUrl, "/chat/completions");
            req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + model.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"HTTP {(int)resp.StatusCode}: {Truncate(raw, 400)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Idle-watchdog: если от сервера 60 сек не приходит ни одной строки —
        // рвём стрим с явной ошибкой. Защищает от подвисания, когда upstream
        // держит соединение открытым, но больше ничего не шлёт.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(TimeSpan.FromSeconds(60));
        var streamCt = idleCts.Token;
        void TouchIdle()
        {
            if (idleCts.IsCancellationRequested) return;
            try { idleCts.CancelAfter(TimeSpan.FromSeconds(60)); } catch { }
        }

        var sb = new StringBuilder();
        var toolBuf = new Dictionary<int, ToolCall>();
        var notifiedTools = new HashSet<int>();
        bool breakOuter = false;

        string? line;
        while (true)
        {
            try
            {
                line = await reader.ReadLineAsync(streamCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("stream timeout: no data from server for 60 seconds");
            }
            catch (Exception ex) when (IsStreamEndError(ex))
            {
                Console.WriteLine("[STREAM] OpenAI read ended early (graceful): " + ex.Message);
                break;
            }
            if (line == null) break;
            TouchIdle();
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:")) continue;

            string piece = line.Substring(5).Trim();
            if (piece.Length == 0) continue;

            if (useProxy)
            {
                string dec;
                try { dec = Decrypt(piece); } catch { continue; }
                try
                {
                    using var ddoc = JsonDocument.Parse(dec);
                    var dr = ddoc.RootElement;
                    var dt = dr.TryGetProperty("type", out var tv) ? (tv.GetString() ?? "") : "";
                    if (dt == "raw")
                    {
                        var rawChunk = dr.TryGetProperty("chunk", out var c) ? (c.GetString() ?? "") : "";
                        ProcessOpenAIChunk(rawChunk, sb, toolBuf, onDelta, onReasoningDelta, onToolStart, notifiedTools);
                    }
                    else if (dt == "done")
                    {
                        int used = dr.TryGetProperty("used", out var u) && u.TryGetInt32(out var uv) ? uv : UsedToday;
                        int limit = dr.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : DailyLimit;
                        UsedToday = used; DailyLimit = limit; Save();
                        breakOuter = true;
                    }
                    else if (dt == "error")
                    {
                        var err = dr.TryGetProperty("error", out var e) ? (e.GetString() ?? "error") : "error";
                        throw new Exception(err);
                    }
                }
                catch (JsonException) { }
                if (breakOuter) break;
                continue;
            }

            if (piece == "[DONE]") break;
            ProcessOpenAIChunk(piece, sb, toolBuf, onDelta, onReasoningDelta, onToolStart, notifiedTools);
        }

        var calls = toolBuf.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        return (sb.ToString(), calls);
    }

    private static void ProcessOpenAIChunk(
        string chunk, StringBuilder sb, Dictionary<int, ToolCall> toolBuf,
        Action<string> onDelta, Action<string> onReasoningDelta,
        Action<string>? onToolStart, HashSet<int> notifiedTools)
    {
        try
        {
            using var doc = JsonDocument.Parse(chunk);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)) return;

            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var rt = rc.GetString();
                if (!string.IsNullOrEmpty(rt)) onReasoningDelta(rt);
            }

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var t = c.GetString();
                if (!string.IsNullOrEmpty(t)) { sb.Append(t); onDelta(t); }
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    int idx = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                    if (!toolBuf.TryGetValue(idx, out var b)) { b = new ToolCall(); toolBuf[idx] = b; }
                    if (tc.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
                        b.Id = idv.GetString() ?? "";
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                            { var __n = nm.GetString(); if (!string.IsNullOrEmpty(__n)) b.Name = __n; }
                        if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
                            b.Arguments += ar.GetString() ?? "";
                    }
                    if (onToolStart != null && !notifiedTools.Contains(idx) && !string.IsNullOrEmpty(b.Name))
                    {
                        notifiedTools.Add(idx);
                        try { onToolStart(b.Name); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private async Task RunAnthropicLoopAsync(
        List<ChatTurn> messages, ChatModelConfig model, ToolDefinition[] tools,
        Func<ToolCall, Task<ToolExecResult>> toolExecutor,
        Action<string> onDelta, Action<string> onReasoningDelta, Action<ToolStatus> onStatus,
        Action<int, int> onDone, Action<string> onError, CancellationToken ct)
    {
        for (int round = 0; round < MaxToolRounds; round++)
        {
            string? system = null;
            var msgsArr = new List<object>();
            foreach (var t in messages)
            {
                if (t.Role == "system") { system = (system is null ? "" : system + "\n\n") + t.Content; continue; }
                if (t.Role == "assistant" && t.ToolCalls is { Count: > 0 })
                {
                    var blocks = new List<object>();
                    if (!string.IsNullOrEmpty(t.Content)) blocks.Add(new { type = "text", text = t.Content });
                    foreach (var tc in t.ToolCalls)
                    {
                        object inputObj;
                        try { inputObj = JsonSerializer.Deserialize<object>(tc.Arguments) ?? new { }; }
                        catch { inputObj = new { }; }
                        blocks.Add(new { type = "tool_use", id = tc.Id, name = tc.Name, input = inputObj });
                    }
                    msgsArr.Add(new { role = "assistant", content = blocks.ToArray() });
                }
                else if (t.Role == "tool")
                {
                    object toolContent;
                    if (t.Images is { Count: > 0 })
                    {
                        var parts = new List<object> { new { type = "text", text = t.Content } };
                        foreach (var img in t.Images)
                            parts.Add(new { type = "image", source = new { type = "base64", media_type = img.MimeType, data = img.Base64 } });
                        toolContent = parts.ToArray();
                    }
                    else toolContent = t.Content;
                    msgsArr.Add(new { role = "user", content = new object[]
                        { new { type = "tool_result", tool_use_id = t.ToolCallId ?? "", content = toolContent } } });
                }
                else msgsArr.Add(new { role = t.Role, content = t.Content });
            }

            var payloadDict = new Dictionary<string, object?>
            {
                ["model"] = model.Model, ["max_tokens"] = 4096,
                ["messages"] = msgsArr.ToArray(), ["stream"] = true,
            };
            if (!string.IsNullOrEmpty(system)) payloadDict["system"] = system;
            if (tools.Length > 0)
                payloadDict["tools"] = tools.Select(t => new
                { name = t.Name, description = t.Description, input_schema = t.Parameters }).ToArray();

            var payloadJson = JsonSerializer.Serialize(payloadDict);

            // ── Browser bridge: non-stream, но с тем же циклом tool-exec ──
            if (Bridge?.IsRunning == true && Bridge.IsPageConnected)
            {
                var bres = await StreamViaBridgeAnthropicAsync(payloadJson, model, onDelta, onStatus, ct);
                if (bres.error != null) { onError("bridge: " + bres.error); return; }

                if (bres.toolCalls.Count == 0) { onDone(-1, -1); return; }

                messages.Add(new ChatTurn { Role = "assistant", Content = bres.text, ToolCalls = bres.toolCalls });
                foreach (var tc in bres.toolCalls)
                {
                    ToolExecResult tres;
                    try { tres = await toolExecutor(tc); }
                    catch (Exception ex)
                    { tres = new ToolExecResult { Content = "{\"error\":\"" + ex.Message.Replace("\"","\\\"") + "\"}" }; }
                    onStatus(new ToolStatus { Phase = "done", Tool = tc.Name, Added = tres.Added, Removed = tres.Removed, Extra = tres.Extra });
                    messages.Add(new ChatTurn { Role = "tool", Content = tres.Content, ToolCallId = tc.Id, ToolName = tc.Name, Images = tres.Images });
                }
                continue;
            }

            string url = BuildUrl(model.BaseUrl, "/messages");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("x-api-key", model.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                onError($"Anthropic HTTP {(int)resp.StatusCode}: {Truncate(raw, 400)}");
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var sb = new StringBuilder();
            var toolBuf = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

            string? line;
            while (true)
            {
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception ex) when (IsStreamEndError(ex))
                {
                    Console.WriteLine("[STREAM] Anthropic read ended early: " + ex.Message);
                    break;
                }
                if (line == null) break;
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:")) continue;
                string piece = line.Substring(5).Trim();
                if (piece.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(piece);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("type", out var tt)) continue;
                    var tp = tt.GetString() ?? "";
                    if (tp == "content_block_start")
                    {
                        int idx = r.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                        if (r.TryGetProperty("content_block", out var cb) &&
                            cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                        {
                            string id = cb.TryGetProperty("id", out var idv) ? (idv.GetString() ?? "") : "";
                            string nm = cb.TryGetProperty("name", out var nmv) ? (nmv.GetString() ?? "") : "";
                            toolBuf[idx] = (id, nm, new StringBuilder());
                            if (!string.IsNullOrEmpty(nm))
                                onStatus(new ToolStatus { Phase = "start", Tool = nm });
                        }
                    }
                    else if (tp == "content_block_delta")
                    {
                        int idx = r.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                        if (r.TryGetProperty("delta", out var delta))
                        {
                            var dt = delta.TryGetProperty("type", out var dtv) ? (dtv.GetString() ?? "") : "";
                            if (dt == "text_delta")
                            {
                                var t = delta.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "") : "";
                                if (!string.IsNullOrEmpty(t)) { sb.Append(t); onDelta(t); }
                            }
                            else if (dt == "input_json_delta")
                            {
                                var pj = delta.TryGetProperty("partial_json", out var pjv) ? (pjv.GetString() ?? "") : "";
                                if (toolBuf.TryGetValue(idx, out var bucket)) bucket.Args.Append(pj);
                            }
                        }
                    }
                    else if (tp == "message_stop") break;
                    else if (tp == "error")
                    {
                        var err = r.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                            ? (em.GetString() ?? "Anthropic error") : "Anthropic error";
                        onError(err); return;
                    }
                }
                catch { }
            }

            var calls = toolBuf.OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall { Id = kv.Value.Id, Name = kv.Value.Name, Arguments = kv.Value.Args.ToString() })
                .ToList();

            if (calls.Count == 0) { onDone(-1, -1); return; }

            messages.Add(new ChatTurn { Role = "assistant", Content = sb.ToString(), ToolCalls = calls });

            foreach (var tc in calls)
            {
                // start уже был отправлен при content_block_start
                ToolExecResult res;
                try { res = await toolExecutor(tc); }
                catch (Exception ex)
                { res = new ToolExecResult { Content = "{\"error\":\"" + ex.Message.Replace("\"","\\\"") + "\"}" }; }
                onStatus(new ToolStatus { Phase = "done", Tool = tc.Name, Added = res.Added, Removed = res.Removed, Extra = res.Extra });
                messages.Add(new ChatTurn { Role = "tool", Content = res.Content, ToolCallId = tc.Id, ToolName = tc.Name, Images = res.Images });
            }
        }
        onError("Tool rounds limit exceeded");
    }

    /// <summary>
    /// OpenAI-совместимый non-stream запрос через browser bridge.
    /// Эмулирует один большой delta, чтобы UI работал как обычно.
    /// </summary>
    private async Task<(string text, List<ToolCall> toolCalls)> StreamViaBridgeOpenAIAsync(
        string payloadJson, ChatModelConfig model,
        Action<string> onDelta, Action<string> onReasoningDelta,
        Action<string>? onToolStart,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var toolBuf = new Dictionary<int, ToolCall>();
        var notifiedTools = new HashSet<int>();
        var lineBuf = new StringBuilder();
        bool sawDone = false;

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)!.AsObject();
            node["stream"] = true;
            string body = node.ToJsonString();

            string url = BuildUrl(model.BaseUrl, "/chat/completions");
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Authorization"] = "Bearer " + model.ApiKey,
                ["Accept"] = "text/event-stream",
            };

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(TimeSpan.FromMinutes(10));

            await foreach (var chunk in Bridge!.SendStreamingAsync(url, "POST", headers, body, overallCts.Token))
            {
                if (sawDone) break;
                lineBuf.Append(chunk);
                string all = lineBuf.ToString();
                int lastNl = all.LastIndexOf('\n');
                if (lastNl < 0) continue;

                string complete = all.Substring(0, lastNl + 1);
                string rest = all.Substring(lastNl + 1);
                lineBuf.Clear();
                lineBuf.Append(rest);

                foreach (var rawLine in complete.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data:")) continue;
                    string piece = line.Substring(5).Trim();
                    if (piece.Length == 0) continue;
                    if (piece == "[DONE]") { sawDone = true; break; }
                    ProcessOpenAIChunk(piece, sb, toolBuf, onDelta, onReasoningDelta, onToolStart, notifiedTools);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsStreamEndError(ex))
        {
            Console.WriteLine("[STREAM] bridge OpenAI ended early (graceful): " + ex.Message);
        }

        var calls = toolBuf.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        return (sb.ToString(), calls);
    }

    private async Task<(string text, List<ToolCall> toolCalls, string? error)> StreamViaBridgeAnthropicAsync(
        string payloadJson, ChatModelConfig model,
        Action<string> onDelta, Action<ToolStatus> onStatus,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var toolBuf = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        var lineBuf = new StringBuilder();
        bool stop = false;
        string? error = null;

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)!.AsObject();
            node["stream"] = true;
            string body = node.ToJsonString();

            string url = BuildUrl(model.BaseUrl, "/messages");
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "text/event-stream",
                ["x-api-key"] = model.ApiKey,
                ["anthropic-version"] = "2023-06-01",
                ["anthropic-dangerous-direct-browser-access"] = "true",
            };

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(TimeSpan.FromMinutes(10));

            await foreach (var chunk in Bridge!.SendStreamingAsync(url, "POST", headers, body, overallCts.Token))
            {
                if (stop) break;
                lineBuf.Append(chunk);
                string all = lineBuf.ToString();
                int lastNl = all.LastIndexOf('\n');
                if (lastNl < 0) continue;

                string complete = all.Substring(0, lastNl + 1);
                string rest = all.Substring(lastNl + 1);
                lineBuf.Clear();
                lineBuf.Append(rest);

                foreach (var rawLine in complete.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data:")) continue;
                    string piece = line.Substring(5).Trim();
                    if (piece.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(piece);
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("type", out var tt)) continue;
                        var tp = tt.GetString() ?? "";
                        if (tp == "content_block_start")
                        {
                            int idx = r.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                            if (r.TryGetProperty("content_block", out var cb) &&
                                cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                            {
                                string id = cb.TryGetProperty("id", out var idv) ? (idv.GetString() ?? "") : "";
                                string nm = cb.TryGetProperty("name", out var nmv) ? (nmv.GetString() ?? "") : "";
                                toolBuf[idx] = (id, nm, new StringBuilder());
                                if (!string.IsNullOrEmpty(nm))
                                    try { onStatus(new ToolStatus { Phase = "start", Tool = nm }); } catch { }
                            }
                        }
                        else if (tp == "content_block_delta")
                        {
                            int idx = r.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                            if (r.TryGetProperty("delta", out var delta))
                            {
                                var dt = delta.TryGetProperty("type", out var dtv) ? (dtv.GetString() ?? "") : "";
                                if (dt == "text_delta")
                                {
                                    var t = delta.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "") : "";
                                    if (!string.IsNullOrEmpty(t)) { sb.Append(t); onDelta(t); }
                                }
                                else if (dt == "input_json_delta")
                                {
                                    var pj = delta.TryGetProperty("partial_json", out var pjv) ? (pjv.GetString() ?? "") : "";
                                    if (toolBuf.TryGetValue(idx, out var bucket)) bucket.Args.Append(pj);
                                }
                            }
                        }
                        else if (tp == "message_stop") { stop = true; break; }
                        else if (tp == "error")
                        {
                            error = r.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                                ? (em.GetString() ?? "Anthropic error") : "Anthropic error";
                            stop = true; break;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsStreamEndError(ex))
        {
            Console.WriteLine("[STREAM] bridge Anthropic ended early (graceful): " + ex.Message);
        }

        var calls = toolBuf.OrderBy(kv => kv.Key)
            .Select(kv => new ToolCall { Id = kv.Value.Id, Name = kv.Value.Name, Arguments = kv.Value.Args.ToString() })
            .ToList();
        return (sb.ToString(), calls, error);
    }

    private static string BuildUrl(string baseUrl, string suffix)
    {
        string t = baseUrl.TrimEnd('/');
        if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return t;
        return t + suffix;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
}
