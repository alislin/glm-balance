using System.Globalization;
using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>
/// 智谱监控 API 客户端（与 opencode 插件同源接口）：
/// /api/monitor/usage/quota/limit、/api/monitor/usage/model-usage、/api/monitor/credit-usage/activity。
/// </summary>
public sealed class GlmApiClient : IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly AppConfig _cfg;

    public GlmApiClient(AppConfig cfg) => _cfg = cfg;

    public void Dispose() => _http.Dispose();

    /// <summary>quota/limit + 24h model-usage 并行抓取；失败信息写入 snapshot.Error（对齐插件 refresh）。</summary>
    public async Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
        {
            return new UsageSnapshot { Error = "未找到 API key（auth.json / opencode.json / glmbalance-taskbar.json）" };
        }

        var base_ = _cfg.MonitorBase.TrimEnd('/');
        var tq = _cfg.UsageType is int t ? $"type={t}&" : "";
        var quotaTask = TryGetAsync($"{base_}/api/monitor/usage/quota/limit?{tq}", ct);
        var modelTask = TryGetAsync($"{base_}/api/monitor/usage/model-usage?{tq}{Window24h()}", ct);

        JsonDocument? quotaDoc = null, modelDoc = null;
        try
        {
            var (qDoc, qErr) = await quotaTask.ConfigureAwait(false);
            quotaDoc = qDoc;
            if (qErr != null) return new UsageSnapshot { Error = qErr };

            UsageSnapshot snap;
            if (quotaDoc != null)
            {
                var root = quotaDoc.RootElement;
                if (root.Prop("success").BoolVal() == false)
                {
                    var msg = root.Prop("msg").Str();
                    return new UsageSnapshot
                    {
                        Error = string.IsNullOrWhiteSpace(msg)
                            ? $"code {root.Prop("code").Num()?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                            : msg,
                    };
                }
                snap = QuotaParser.Parse(root, DateTimeOffset.Now);
            }
            else
            {
                snap = new UsageSnapshot();
            }

            // model-usage 失败静默（团队维度可能无数据）
            var (mDoc, _) = await modelTask.ConfigureAwait(false);
            modelDoc = mDoc;
            if (modelDoc?.RootElement.Prop("success").BoolVal() != false)
            {
                var total = modelDoc?.RootElement.Prop("data").Prop("totalUsage");
                snap.Tokens24h = ToLong(total.Prop("totalTokensUsage").Num());
                if (total.Prop("modelSummaryList") is { ValueKind: JsonValueKind.Array } ms)
                {
                    snap.Models = ms.EnumerateArray()
                        .Select(m => new ModelUsage(m.Prop("modelName").Str() ?? "?", ToLong(m.Prop("totalTokens").Num()) ?? 0))
                        .Where(m => m.Tokens > 0)
                        .OrderByDescending(m => m.Tokens)
                        .ToList();
                }
            }
            return snap;
        }
        catch (Exception ex)
        {
            return new UsageSnapshot { Error = ex.Message };
        }
        finally
        {
            quotaDoc?.Dispose();
            modelDoc?.Dispose();
        }
    }

    /// <summary>活跃度（一年窗口）；失败返回 null 由调用方决定重试。</summary>
    public async Task<ActivityInfo?> FetchActivityAsync(CancellationToken ct = default)
    {
        var base_ = _cfg.MonitorBase.TrimEnd('/');
        var url = $"{base_}/api/monitor/credit-usage/activity?type={_cfg.ActivityType}&{ActivityWindow()}";
        var (doc, _) = await TryGetAsync(url, ct).ConfigureAwait(false);
        if (doc == null) return null;
        try
        {
            var root = doc.RootElement;
            if (root.Prop("success").BoolVal() == false) return null;
            return ActivityParser.Parse(root, DateTimeOffset.Now);
        }
        finally
        {
            doc.Dispose();
        }
    }

    async Task<(JsonDocument? Doc, string? Error)> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", _cfg.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
            req.Headers.TryAddWithoutValidation("User-Agent", "GlmBalanceTaskbar/1.0");
            if (_cfg.Organization != null) req.Headers.TryAddWithoutValidation("bigmodel-organization", _cfg.Organization);
            if (_cfg.Project != null) req.Headers.TryAddWithoutValidation("bigmodel-project", _cfg.Project);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return (null, $"HTTP {(int)res.StatusCode}");
            if (string.IsNullOrWhiteSpace(text)) return (null, "空响应");
            return (JsonDocument.Parse(text), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>近 24 小时窗口查询串（与插件 usageWindow 一致）。</summary>
    static string Window24h()
    {
        var end = DateTime.Now;
        var start = end.AddHours(-24);
        return $"startTime={Fmt(start)}&endTime={Fmt(end)}";

        static string Fmt(DateTime d) =>
            Uri.EscapeDataString(d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>近一年窗口查询串（与插件 activityWindow 一致）。</summary>
    static string ActivityWindow()
    {
        var today = DateTime.Today;
        var start = today.AddDays(-365);
        var ci = CultureInfo.InvariantCulture;
        return $"startTime={Uri.EscapeDataString(start.ToString("yyyy-MM-dd", ci) + " 00:00:00")}" +
               $"&endTime={Uri.EscapeDataString(today.ToString("yyyy-MM-dd", ci) + " 23:59:59")}";
    }

    static long? ToLong(double? d) => d is double v ? (long)v : null;
}
