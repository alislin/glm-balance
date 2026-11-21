using System.Net;
using System.Text;
using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>devMock 配置段（dev 配置文件顶层 "devMock" 节；生产 Load 不读该键，零影响）。</summary>
public sealed class DevMockOptions
{
    public int UpstreamPort { get; set; } = DevMode.DefaultMockUpstreamPort;
    public string ChatContent { get; set; } = "[mock] dev 假响应";
    public int LatencyMs { get; set; } = 50;
    public int FiveHourRemainingPct { get; set; } = 80;
    public int WeekRemainingPct { get; set; } = 60;
    public Dictionary<string, DevMockProviderRules> Providers { get; set; } = new(StringComparer.Ordinal);
    /// <summary>activity 响应 series 的每日 tokens（缺省用内置静态数据，近 14 天）。</summary>
    public List<long>? ActivitySeries { get; set; }
}

/// <summary>按渠道 id 的 mock 规则（chatStatus / modelsStatus / streamBreak）。</summary>
public sealed class DevMockProviderRules
{
    public int ChatStatus { get; set; } = 200;
    public int ModelsStatus { get; set; } = 200;
    /// <summary>true = 发 2 个 chunk 后直接断开 TCP（触发 NoteStreamBroken）。</summary>
    public bool StreamBreak { get; set; }
}

/// <summary>
/// 进程内假上游（HttpListener，默认 127.0.0.1:8789）：
/// POST /v1/chat/completions（含 SSE 与规则引擎）、GET /v1/models、
/// GET /api/monitor/usage/quota/limit、GET /api/monitor/usage/model-usage、
/// GET /api/monitor/credit-usage/activity。
/// 线程模型与 RelayServer 同款（异步 accept + 每连接 Task，延迟用 Task.Delay，handler 异常全捕获）。
/// </summary>
public sealed class MockUpstreamServer : IDisposable
{
    readonly DevMockOptions _rules;
    HttpListener? _listener;
    CancellationTokenSource? _cts;

    public MockUpstreamServer(DevMockOptions? rules)
    {
        _rules = rules ?? new DevMockOptions();
    }

    /// <summary>启动监听；端口自环校验（upstreamPort ∉ {relayPort, 8787}）与启动失败均抛出，由调用方决定失败语义。</summary>
    public void Start(int relayPort, List<string> warnings)
    {
        if (DevMode.MockUpstreamPort is DevMode.ProductionRelayPort || DevMode.MockUpstreamPort == relayPort)
        {
            throw new InvalidOperationException(
                $"mock upstream 端口 {DevMode.MockUpstreamPort} 与 relay 监听端口（{relayPort}）/生产端口 8787 冲突，拒绝启动");
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{DevMode.MockUpstreamPort}/");
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        DevMode.SetMockUpstreamRunning(true);
        warnings.Add($"[dev] mock upstream 已启动：{DevMode.MockBaseUrl}（chat={_rules.ChatContent.Length} 字符、latency={_rules.LatencyMs}ms）");
    }

    public void Dispose()
    {
        DevMode.SetMockUpstreamRunning(false);
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
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

            switch ((path, method))
            {
                case ("/v1/chat/completions", "POST"):
                    await HandleChatAsync(req, rsp, ct).ConfigureAwait(false);
                    break;

                case ("/v1/models", "GET"):
                    await HandleModelsAsync(req, rsp).ConfigureAwait(false);
                    break;

                case ("/api/monitor/usage/quota/limit", "GET"):
                    await WriteJsonAsync(rsp, 200, BuildQuotaJson()).ConfigureAwait(false);
                    break;

                case ("/api/monitor/usage/model-usage", "GET"):
                    await WriteJsonAsync(rsp, 200, BuildModelUsageJson()).ConfigureAwait(false);
                    break;

                case ("/api/monitor/credit-usage/activity", "GET"):
                    await WriteJsonAsync(rsp, 200, BuildActivityJson()).ConfigureAwait(false);
                    break;

                default:
                    await WriteJsonAsync(rsp, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            // handler 内异常全捕获并回 500，避免未观察异常拖垮进程（§5.5）
            try
            {
                var rsp = ctx.Response;
                rsp.StatusCode = 500;
                rsp.ContentLength64 = 0;
                rsp.Close();
                }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }

    // ───────────────────────── chat/completions 规则引擎 ─────────────────────────

    async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse rsp, CancellationToken ct)
    {
        var rules = RulesByKey(ResolveBearer(req));

        if (_rules.LatencyMs > 0)
        {
            try { await Task.Delay(_rules.LatencyMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var status = rules?.ChatStatus ?? 200;
        string body;
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            body = "{}";
        }

        string model = "glm-5.3-flash";
        bool stream = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            model = doc.RootElement.Prop("model").Str() ?? model;
            stream = doc.RootElement.Prop("stream").BoolVal() ?? false;
        }
        catch
        {
            // 解析失败按非流式最小响应处理
        }

        if (status is 429 or 402 or 403)
        {
            await WriteJsonAsync(rsp, status, BuildQuotaErrorJson(status)).ConfigureAwait(false);
            return;
        }
        if (status >= 500)
        {
            await WriteJsonAsync(rsp, status, BuildServerErrorJson(status)).ConfigureAwait(false);
            return;
        }
        if (status is >= 400 and < 500)
        {
            await WriteJsonAsync(rsp, status, BuildServerErrorJson(status)).ConfigureAwait(false);
            return;
        }

        if (stream) await WriteSseAsync(rsp, model, rules, ct).ConfigureAwait(false);
        else await WriteNonStreamAsync(rsp, model).ConfigureAwait(false);
    }

    /// <summary>Bearer key（dev 配置播种的 mock-&lt;id&gt;）反查渠道规则；未命中返回 null（按 200 处理）。</summary>
    DevMockProviderRules? RulesByKey(string? key)
    {
        if (key == null) return null;
        var id = key.StartsWith("mock-", StringComparison.Ordinal) ? key["mock-".Length..] : key;
        return _rules.Providers.TryGetValue(id, out var r) ? r : null;
    }

    static string? ResolveBearer(HttpListenerRequest req)
    {
        var auth = req.Headers["Authorization"] ?? "";
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
    }

    async Task WriteNonStreamAsync(HttpListenerResponse rsp, string model)
    {
        var payload = new
        {
            id = $"chatcmpl-mock-{Interlocked.Increment(ref _seq)}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = _rules.ChatContent },
                    finish_reason = "stop",
                },
            },
            usage = new { prompt_tokens = 10, completion_tokens = 8, total_tokens = 18 },
        };
        await WriteJsonAsync(rsp, 200, JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    long _seq;

    /// <summary>SSE：N 个 delta chunk 拼出 chatContent；streamBreak=true 时发 2 个 chunk 后直接断开 TCP。</summary>
    async Task WriteSseAsync(HttpListenerResponse rsp, string model, DevMockProviderRules? rules, CancellationToken ct)
    {
        rsp.StatusCode = 200;
        rsp.ContentType = "text/event-stream; charset=utf-8";
        rsp.SendChunked = true;

        var content = _rules.ChatContent;
        var chunks = (int)Math.Ceiling(content.Length / 4.0);
        if (rules?.StreamBreak == true && chunks < 3) chunks = 3;

        try
        {
            var sent = 0;
            for (var i = 0; i < chunks; i++)
            {
                if (rules?.StreamBreak == true && ++sent > 2) break;

                var piece = content.Substring(i * 4, Math.Min(4, content.Length - i * 4));
                var delta = new
                {
                    id = $"chatcmpl-mock-{Interlocked.Increment(ref _seq)}",
                    @object = "chat.completion.chunk",
                    model,
                    choices = new[]
                    {
                        new { index = 0, delta = new { content = piece }, finish_reason = (string?)null },
                    },
                };
                var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(delta) + "\n\n");
                await rsp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await rsp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                try { await Task.Delay(10, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            if (rules?.StreamBreak == true)
            {
                // 中途断开 TCP（不写 [DONE]、不 Close）→ 客户端侧触发 NoteStreamBroken
                rsp.Abort();
                return;
            }

            var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await rsp.OutputStream.WriteAsync(done, ct).ConfigureAwait(false);
            await rsp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            rsp.Close();
        }
        catch
        {
            try { rsp.Abort(); } catch { /* ignore */ }
        }
    }

    // ───────────────────────── /v1/models 与监控端点 ─────────────────────────

    async Task HandleModelsAsync(HttpListenerRequest req, HttpListenerResponse rsp)
    {
        var status = RulesByKey(ResolveBearer(req))?.ModelsStatus ?? 200;
        if (status != 200)
        {
            await WriteJsonAsync(rsp, status, BuildServerErrorJson(status)).ConfigureAwait(false);
            return;
        }
        var models = new[] { "glm-5.3-flash", "glm-5.3" };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder("""{"object":"list","data":[""");
        var first = true;
        foreach (var m in models)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("""{"id":""").Append(RelayJson.Quote(m))
              .Append(""","object":"model","created":""").Append(now)
              .Append(""","owned_by":"mock-upstream"}""");
        }
        sb.Append("]}");
        await WriteJsonAsync(rsp, 200, sb.ToString()).ConfigureAwait(false);
    }

    string BuildQuotaJson()
    {
        // 形状与 QuotaParser 期望一致：data.limits[]，CREDIT_LIMIT、unit=3（5h）/6（周）、percentage=已用 %
        var used5 = 100 - Math.Clamp(_rules.FiveHourRemainingPct, 0, 100);
        var usedW = 100 - Math.Clamp(_rules.WeekRemainingPct, 0, 100);
        return "{\"success\":true,\"data\":{\"limits\":["
            + "{\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"percentage\":" + used5
            + ",\"currentValue\":" + (100 - used5) * 150 + ",\"usage\":15000},"
            + "{\"type\":\"CREDIT_LIMIT\",\"unit\":6,\"number\":1,\"percentage\":" + usedW
            + ",\"currentValue\":" + (100 - usedW) * 660 + ",\"usage\":66000}"
            + "]}}";
    }

    string BuildModelUsageJson() =>
        "{\"success\":true,\"data\":{\"totalUsage\":{\"totalTokensUsage\":1234567,\"modelSummaryList\":["
        + "{\"modelName\":\"glm-5.3-flash\",\"totalTokens\":1234567}"
        + "]}}}";

    string BuildActivityJson() =>
        "{\"success\":true,\"data\":{\"summary\":{\"totalTokens\":3520000000,\"totalUsageDurationMs\":539400000,"
        + "\"currentStreakDays\":25,\"longestStreakDays\":40,\"peakDailyTokens\":59000000,\"peakDailyTokensDate\":\"2026-11-20\"},"
        + "\"series\":[" + BuildSeries() + "]}}";

    string BuildSeries()
    {
        IReadOnlyList<long> values = _rules.ActivitySeries is { Count: > 0 } s ? s : DefaultSeries;
        return string.Join(",", values.Select(v => $"{{\"totalTokens\":{v}}}"));
    }

    static readonly long[] DefaultSeries = { 10, 20, 30, 40, 50, 60, 70, 80, 70, 60, 50, 40, 30, 20 };

    // ───────────────────────── 错误体 ─────────────────────────

    static string BuildQuotaErrorJson(int status) => status switch
    {
        429 => """{"error":{"message":"mock: quota exhausted","type":"insufficient_quota","code":"429"}}""",
        402 => """{"error":{"message":"mock: payment required","type":"insufficient_quota","code":"402"}}""",
        _ => """{"error":{"message":"mock: forbidden","type":"forbidden","code":"403"}}""",
    };

    static string BuildServerErrorJson(int status) =>
        "{\"error\":{\"message\":\"mock: upstream error\",\"type\":\"upstream_error\",\"code\":\"" + status + "\"}}";

    static async Task WriteJsonAsync(HttpListenerResponse rsp, int status, string json)
    {
        rsp.StatusCode = status;
        rsp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        rsp.ContentLength64 = bytes.Length;
        await rsp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        rsp.Close();
    }
}
