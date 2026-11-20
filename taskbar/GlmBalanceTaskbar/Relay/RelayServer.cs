using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlmBalanceTaskbar;

/// <summary>
/// HttpListener 中转服务：/v1/* 聊天端点 + /admin 面板 + /api/* 管理 API。
/// 请求体读原始字节（仅解析副本取 model/stream），转发原始字节不做 JSON 重序列化；
/// 失败转移仅发生在未向客户端写入任何字节前；每连接一个 Task，UI 线程零阻塞。
/// </summary>
public sealed class RelayServer : IDisposable
{
    const long MaxBodyBytes = 256L * 1024 * 1024;
    const int ToHeadersTimeoutSec = 120;
    const int SseIdleTimeoutSec = 120;

    readonly RelayManager _mgr;
    readonly AdminApi _admin;
    readonly HttpClient _upstream;
    HttpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _acceptLoop;
    int _inflight; // 进行中的 /v1/chat/completions 请求数（Stop 宽限期依据）

    public RelayServer(RelayManager mgr)
    {
        _mgr = mgr;
        _admin = new AdminApi(mgr);
        _upstream = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            // 流式响应不受默认 100s 超时掐断；"到响应头"的时限用每请求 CTS 控制
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>启动监听；端口被占用/启动失败返回 false 并写入 warnings。</summary>
    public bool Start(int port, List<string> warnings)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            listener.Close();
            warnings.Add($"中转服务启动失败（端口 {port}）：{ex.Message}。已跳过中转，配额显示不受影响。");
            return false;
        }
        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        return true;
    }

    /// <summary>进行中的 /v1/chat/completions 请求数（RelayManager.Stop 宽限期轮询依据）。</summary>
    public int Inflight => Interlocked.CompareExchange(ref _inflight, 0, 0);

    /// <summary>停止接受新请求：listener 停止后 pending GetContext 退出；已受理请求的响应可继续写完。</summary>
    public void StopAccepting()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
    }

    /// <summary>宽限期后的全量关停：取消请求 CTS（残余 in-flight 经异常路径 Abort）并关闭 listener。</summary>
    public void Shutdown()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public void Dispose()
    {
        Shutdown();
        _upstream.Dispose();
    }

    async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break; // listener 已停止
            }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var rsp = ctx.Response;
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            switch (path)
            {
                case "/health" when method == "GET":
                    await WriteJsonAsync(rsp, 200, "{\"status\":\"ok\"}").ConfigureAwait(false);
                    break;

                case "/status" when method == "GET":
                    await WriteJsonAsync(rsp, 200, RelayJson.Serialize(_mgr.Snapshot())).ConfigureAwait(false);
                    break;

                case "/admin" when method == "GET":
                    await WriteBytesAsync(rsp, 200, "text/html; charset=utf-8", AdminResource.Html).ConfigureAwait(false);
                    break;

                case "/v1/models" when method == "GET":
                    if (!Authorize(req)) { await WriteErrorAsync(rsp, 401, "Invalid or missing token", "invalid_request_error", "invalid_api_key").ConfigureAwait(false); break; }
                    await WriteJsonAsync(rsp, 200, BuildModelsJson()).ConfigureAwait(false);
                    break;

                case "/v1/chat/completions" when method == "POST":
                    // in-flight 计数：Stop 的宽限期据此等待长请求写完（其余端点均短请求，不计）
                    Interlocked.Increment(ref _inflight);
                    try
                    {
                        await HandleChatAsync(ctx, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inflight);
                    }
                    break;

                case var p when p.StartsWith("/api/", StringComparison.Ordinal):
                    await _admin.HandleAsync(ctx, Authorize(req)).ConfigureAwait(false);
                    break;

                default:
                    await WriteErrorAsync(rsp, 404, $"Not found: {method} {path}", "invalid_request_error", "not_found").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            // r3 修复：响应可能已开始（部分头/体已发出），二次写必抛异常且客户端拿到畸形响应；
            // 写 500 失败时 Abort 连接，让失败显式化而非静默空 body
            try
            {
                await WriteErrorAsync(ctx.Response, 500, ex.Message, "internal_error", null).ConfigureAwait(false);
            }
            catch
            {
                Abort(ctx.Response);
            }
        }
    }

    /// <summary>Bearer localToken 鉴权；token 为空（写回失败的免鉴权模式）恒放行。仅本机回环监听。</summary>
    bool Authorize(HttpListenerRequest req)
    {
        var token = _mgr.EffectiveToken;
        if (token.Length == 0) return true; // 免鉴权模式
        var auth = req.Headers["Authorization"] ?? "";
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var presented = auth["Bearer ".Length..].Trim();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
    }

    string BuildModelsJson()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder("""{"object":"list","data":[""");
        var first = true;
        foreach (var m in _mgr.Router.ModelUnion())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("""{"id":""").Append(RelayJson.Quote(m))
              .Append(""","object":"model","created":""").Append(now)
              .Append(""","owned_by":"localrelay"}""");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ───────────────────────── chat/completions 转发与失败转移 ─────────────────────────

    async Task HandleChatAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var rsp = ctx.Response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!Authorize(req))
        {
            await WriteErrorAsync(rsp, 401, "Invalid or missing token", "invalid_request_error", "invalid_api_key").ConfigureAwait(false);
            return;
        }

        // 读原始字节（上限 256MB → 413）
        byte[] body;
        try
        {
            body = await ReadBodyAsync(req, ct).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            await WriteErrorAsync(rsp, 413, "Request body too large (limit 256MB)", "invalid_request_error", "request_too_large").ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(rsp, 400, $"读取请求体失败：{ex.Message}", "invalid_request_error", "bad_body").ConfigureAwait(false);
            return;
        }

        // 仅解析副本读 model / stream；转发原始字节不做 JSON 重序列化
        string model;
        try
        {
            using var doc = JsonDocument.Parse(body);
            model = doc.RootElement.Prop("model").Str()
                ?? throw new InvalidOperationException("缺少 model 字段");
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(rsp, 400, $"请求体不是合法的 chat 请求：{ex.Message}", "invalid_request_error", "bad_body").ConfigureAwait(false);
            return;
        }

        // 路由 + 失败转移（仅限未向客户端写入任何字节前）
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<string>();
        var failures = new List<string>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rt = _mgr.Router.Select(model, tried, DateTimeOffset.Now);
            if (rt == null)
            {
                var detail = failures.Count > 0
                    ? "所有可用渠道均失败：" + string.Join("; ", failures)
                    : $"没有支持模型 {model} 的可用渠道";
                _mgr.RecordRequest(model, chain, null, sw.ElapsedMilliseconds, detail);
                await WriteErrorAsync(rsp, 502, $"中转失败：{detail}", "relay_upstream_failed", "no_channel", failures).ConfigureAwait(false);
                return;
            }
            chain.Add(rt.Id);

            var outcome = await TryUpstreamAsync(rt, body, req, ct).ConfigureAwait(false);

            switch (outcome.Kind)
            {
                case UpstreamKind.Success:
                    var ok = await WriteUpstreamResponseAsync(rsp, rt, outcome.Response!, model, chain, sw.ElapsedMilliseconds, ct).ConfigureAwait(false);
                    if (!ok) { tried.Add(rt.Id); failures.Add($"{rt.Id}(读响应体失败)"); _mgr.CountFailover(); break; }
                    return;

                case UpstreamKind.QuotaFailure: // 429/402/403 → 硬冷却 + 转移
                    rt.NoteQuotaFailure(Summarize(outcome), DateTimeOffset.Now);
                    tried.Add(rt.Id);
                    failures.Add($"{rt.Id}({(int)outcome.StatusCode}: {Trunc(outcome.ErrorText, 80)})");
                    _mgr.CountFailover();
                    break;

                case UpstreamKind.ServerError: // 5xx → 计数 + 转移
                    rt.NoteErrorFailure(Summarize(outcome), DateTimeOffset.Now);
                    tried.Add(rt.Id);
                    failures.Add($"{rt.Id}({(int)outcome.StatusCode}: {Trunc(outcome.ErrorText, 80)})");
                    _mgr.CountFailover();
                    break;

                case UpstreamKind.NetworkError: // fetch 异常/超时 → 计数 + 转移
                    rt.NoteErrorFailure(outcome.ErrorText ?? "网络异常", DateTimeOffset.Now);
                    tried.Add(rt.Id);
                    failures.Add($"{rt.Id}(网络: {Trunc(outcome.ErrorText, 80)})");
                    _mgr.CountFailover();
                    break;

                default: // 4xx 业务错 → 原样回传不转移
                    rt.NoteClientError((int)outcome.StatusCode, Trunc(outcome.ErrorText, 120), DateTimeOffset.Now);
                    await WriteUpstreamClientErrorAsync(rsp, outcome, rt.Id).ConfigureAwait(false);
                    _mgr.RecordRequest(model, chain, (int)outcome.StatusCode, sw.ElapsedMilliseconds, Trunc(outcome.ErrorText, 120));
                    return;
            }
        }
    }

    async Task<UpstreamOutcome> TryUpstreamAsync(ProviderRuntime rt, byte[] body, HttpListenerRequest req, CancellationToken ct)
    {
        var key = rt.ResolvedKey;
        if (key == null) return UpstreamOutcome.Network($"渠道 {rt.Id} 无 API key");

        var url = rt.Config.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ToHeadersTimeoutSec)); // 到响应头 120s；超时视为网络类失败触发转移

        try
        {
            using var upReq = new HttpRequestMessage(HttpMethod.Post, url);
            upReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            upReq.Headers.TryAddWithoutValidation("Accept", req.Headers["Accept"] ?? "*/*");
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", req.Headers["Content-Type"] ?? "application/json");
            upReq.Content = content;

            var res = await _upstream.SendAsync(upReq, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)res.StatusCode;

            if (res.IsSuccessStatusCode) return UpstreamOutcome.Success(res);

            string errText = "";
            string? contentType = null;
            try
            {
                contentType = res.Content.Headers.ContentType?.ToString();
                errText = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // r3 修复：读取失败重试一次（读原始字节宽松解码），不再静默变空串——
                // 空串会被 4xx 透传成空 body（round3 缺陷 1 的间歇根因）
                try
                {
                    var raw = await res.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
                    errText = Encoding.UTF8.GetString(raw);
                }
                catch
                {
                    errText = "";
                }
            }
            res.Dispose();

            if (status is 401 or 429 or 402 or 403) // 401 鉴权失效属渠道级失效（arch 裁决 H1），与 403 同类：转移 + 硬冷却
            {
                var reason = status == 401 ? "鉴权失效（HTTP 401）" : Trunc(errText, 300);
                return UpstreamOutcome.Quota(status, reason);
            }
            if (status >= 500) return UpstreamOutcome.Server(status, Trunc(errText, 300));
            return UpstreamOutcome.Client(status, errText, contentType); // 4xx 业务错：原样透传不截断（M2）
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpstreamOutcome.Network($"上游 {ToHeadersTimeoutSec}s 未返回响应头（超时）");
        }
        catch (OperationCanceledException)
        {
            throw; // 服务停机等外部取消
        }
        catch (Exception ex)
        {
            return UpstreamOutcome.Network(ex.Message);
        }
    }

    /// <summary>2xx 写回客户端（SSE 逐块 flush / 普通整块透传）。返回 false = 写出前失败（可转移）。</summary>
    async Task<bool> WriteUpstreamResponseAsync(HttpListenerResponse rsp, ProviderRuntime rt,
        HttpResponseMessage res, string model, List<string> chain, long elapsedMs, CancellationToken ct)
    {
        var contentType = res.Content.Headers.ContentType?.ToString() ?? "application/json";
        var isSse = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (!isSse)
        {
            // 非流式：读全部字节后原样透传；读失败（未写客户端字节）→ 交上层转移
            byte[] bytes;
            try
            {
                bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                res.Dispose();
                rt.NoteErrorFailure("读响应体失败：" + ex.Message, DateTimeOffset.Now);
                _mgr.RecordRequest(model, chain, null, elapsedMs, "读响应体失败");
                return false;
            }
            res.Dispose();
            rsp.StatusCode = 200;
            rsp.ContentType = contentType;
            rsp.ContentLength64 = bytes.Length;
            await rsp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            rsp.Close();
            rt.NoteSuccess(elapsedMs);
            _mgr.RecordRequest(model, chain, 200, elapsedMs, null);
            return true;
        }

        // SSE 流式：响应头（含 Content-Type）透传，上游流逐块 copy + flush；idle 120s 无 chunk 才断
        rsp.StatusCode = 200;
        rsp.ContentType = contentType;
        rsp.SendChunked = true;
        Stream? upstream = null;
        try
        {
            upstream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[16 * 1024];
            while (true)
            {
                int n;
                using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idleCts.CancelAfter(TimeSpan.FromSeconds(SseIdleTimeoutSec));
                    try
                    {
                        n = await upstream.ReadAsync(buf, idleCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // idle 断流：不重试，该渠道错误计数 +1
                        rt.NoteStreamBroken($"SSE 空闲 {SseIdleTimeoutSec}s 断流", DateTimeOffset.Now);
                        _mgr.RecordRequest(model, chain, 200, elapsedMs, "SSE 断流（已断开客户端）");
                        Abort(rsp);
                        return true;
                    }
                }
                if (n == 0) break;
                await rsp.OutputStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                await rsp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            }
            rsp.Close();
            rt.NoteSuccess(elapsedMs);
            _mgr.RecordRequest(model, chain, 200, elapsedMs, null);
            return true;
        }
        catch (Exception ex)
        {
            // 客户端主动断开等写失败：不惩罚渠道（仅 idle 断流计数），如实记日志
            _mgr.RecordRequest(model, chain, 200, elapsedMs, "SSE 写回中断：" + ex.Message);
            Abort(rsp);
            return true;
        }
        finally
        {
            upstream?.Dispose();
            res.Dispose();
        }
    }

    static void Abort(HttpListenerResponse rsp)
    {
        try { rsp.Abort(); } catch { /* ignore */ }
    }

    /// <summary>
    /// 4xx 业务错原样回传（状态码 + Content-Type + body）。
    /// 上游未提供错误体或读取失败时，由 relay 生成最小错误说明兜底——客户端永远拿到非空可诊断的 body（round3 缺陷 1）。
    /// </summary>
    async Task WriteUpstreamClientErrorAsync(HttpListenerResponse rsp, UpstreamOutcome o, string providerId)
    {
        var body = o.ErrorText;
        if (string.IsNullOrWhiteSpace(body))
        {
            body = RelayJson.Serialize(new
            {
                error = new
                {
                    message = $"上游渠道 {providerId} 返回 HTTP {(int)o.StatusCode}（错误体为空或读取失败）",
                    type = "upstream_error",
                    code = ((int)o.StatusCode).ToString(CultureInfo.InvariantCulture),
                    provider = providerId,
                },
            });
            rsp.ContentType = "application/json; charset=utf-8"; // 兜底体必为 JSON，覆盖上游可能缺失/异常的 Content-Type
        }
        else
        {
            rsp.ContentType = o.ContentType ?? "application/json";
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        rsp.StatusCode = (int)o.StatusCode;
        rsp.ContentLength64 = bytes.Length;
        await rsp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        rsp.Close();
    }

    async Task<byte[]> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        if (!req.HasEntityBody) return Array.Empty<byte>();
        if (req.ContentLength64 > MaxBodyBytes) throw new BodyTooLargeException();

        var ms = new MemoryStream();
        var stream = req.InputStream;
        var buf = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (total > MaxBodyBytes) throw new BodyTooLargeException();
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    static string Summarize(UpstreamOutcome o) =>
        o.ErrorText is { Length: > 0 } t ? $"HTTP {o.StatusCode}: {t}" : $"HTTP {o.StatusCode}";

    static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    static async Task WriteJsonAsync(HttpListenerResponse rsp, int status, string json)
    {
        rsp.StatusCode = status;
        rsp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        rsp.ContentLength64 = bytes.Length;
        await rsp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        rsp.Close();
    }

    /// <summary>OpenAI 风格错误包装：{"error":{"message","type","code","failures"?}}（不含请求体内容）。</summary>
    static async Task WriteErrorAsync(HttpListenerResponse rsp, int status, string message, string type, string? code, List<string>? failures = null)
    {
        var err = new JsonObject { ["message"] = message, ["type"] = type };
        if (code != null) err["code"] = code;
        if (failures is { Count: > 0 })
        {
            err["failures"] = new JsonArray(failures.Select(f => JsonValue.Create(f)).ToArray());
        }
        await WriteJsonAsync(rsp, status, new JsonObject { ["error"] = err }.ToJsonString(RelayJson.Options));
    }

    static async Task WriteBytesAsync(HttpListenerResponse rsp, int status, string contentType, byte[] bytes)
    {
        rsp.StatusCode = status;
        rsp.ContentType = contentType;
        rsp.ContentLength64 = bytes.Length;
        await rsp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        rsp.Close();
    }

    sealed class BodyTooLargeException : Exception
    {
        public BodyTooLargeException() : base("请求体超过 256MB 上限") { }
    }

    enum UpstreamKind { Success, QuotaFailure, ServerError, NetworkError, ClientError }

    sealed class UpstreamOutcome
    {
        public UpstreamKind Kind { get; private init; }
        public int StatusCode { get; private init; }
        public HttpResponseMessage? Response { get; private init; }
        public string? ErrorText { get; private init; }
        public string? ContentType { get; private init; }

        public static UpstreamOutcome Success(HttpResponseMessage res) => new() { Kind = UpstreamKind.Success, Response = res };
        public static UpstreamOutcome Quota(int status, string? err) => new() { Kind = UpstreamKind.QuotaFailure, StatusCode = status, ErrorText = err };
        public static UpstreamOutcome Server(int status, string? err) => new() { Kind = UpstreamKind.ServerError, StatusCode = status, ErrorText = err };
        public static UpstreamOutcome Network(string err) => new() { Kind = UpstreamKind.NetworkError, ErrorText = err };
        public static UpstreamOutcome Client(int status, string? err, string? contentType) => new() { Kind = UpstreamKind.ClientError, StatusCode = status, ErrorText = err, ContentType = contentType };
    }
}

/// <summary>admin.html 嵌入式资源读取（构建期 EmbeddedResource）。</summary>
public static class AdminResource
{
    static byte[]? _html;

    public static byte[] Html => _html ??= LoadHtml();

    static byte[] LoadHtml()
    {
        var asm = typeof(AdminResource).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("admin.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到嵌入式资源 admin.html");
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

internal static class RelayJsonExtensions2
{
    // Serialize/Quote 已并入 RelayJson；此类别保留为空占位
}
