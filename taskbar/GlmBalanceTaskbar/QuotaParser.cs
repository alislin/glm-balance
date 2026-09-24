using System.Globalization;
using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>配额窗口信息（对应插件的 LimitInfo）。</summary>
public sealed record LimitInfo(
    string? Label,
    int? Remaining,
    string? Detail,
    string? DetailLabel,
    string? ResetLabel);

public sealed record ModelUsage(string Name, long Tokens);

/// <summary>一次用量抓取的结果快照。</summary>
public sealed class UsageSnapshot
{
    public LimitInfo? FiveHour { get; init; }
    public LimitInfo? Week { get; init; }
    public LimitInfo? Mcp { get; init; }
    public string? Level { get; init; }
    public long? Tokens24h { get; set; }
    public IReadOnlyList<ModelUsage> Models { get; set; } = Array.Empty<ModelUsage>();
    public ActivityInfo? Activity { get; set; }
    public string? Error { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>原始 limits 摘要行（--once 调试用，如 "CREDIT_LIMIT/unit=6/48%"）。</summary>
    public IReadOnlyList<string> RawLimits { get; init; } = Array.Empty<string>();
}

/// <summary>
/// /api/monitor/usage/quota/limit 响应解析（移植自插件 parseQuotaLimits）。
/// 窗口单位：3=小时 4=天 5=月 6=周；类型 CREDIT_LIMIT=积分（新版）、TOKENS_LIMIT=token（旧版）、TIME_LIMIT=MCP 月度。
/// </summary>
public static class QuotaParser
{
    static readonly Dictionary<int, long> UnitSeconds = new() { [3] = 3600, [4] = 86400, [5] = 2629800, [6] = 604800 };
    const int HourUnit = 3;
    const int WeekUnit = 6;

    static readonly string[] ResetKeys =
    {
        "resetTime", "refreshTime", "nextResetTime", "resetAt", "refreshAt", "endTime", "windowEnd",
        "expireTime", "expiryTime", "nextWindowTime", "resetTimestamp", "refreshTimestamp", "windowEndTime",
    };

    static readonly string[] WeekdayNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public static UsageSnapshot Parse(JsonElement root, DateTimeOffset now)
    {
        var data = root.Prop("data") ?? root;
        var windows = new List<TokenWindow>();
        LimitInfo? mcp = null;
        var rawLines = new List<string>();

        if (data.Prop("limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            var index = 0;
            foreach (var l in limits.EnumerateArray())
            {
                try
                {
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    var type = l.Prop("type").Str();
                    if (type == null) continue;

                    var unit = ToInt(l.Prop("unit").Num());
                    var pct = l.Prop("percentage").Num();
                    rawLines.Add($"{type}/unit={unit?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{pct?.ToString(CultureInfo.InvariantCulture) ?? "?"}%");

                    if (type == "TOKENS_LIMIT" || type == "CREDIT_LIMIT")
                    {
                        var limit = ToLimitInfo(l, data);
                        if (limit != null)
                        {
                            long? durationSec = null;
                            var number = l.Prop("number").Num();
                            if (unit is int u && number is double n and > 0 && UnitSeconds.TryGetValue(u, out var sec))
                            {
                                durationSec = (long)(sec * n);
                            }
                            windows.Add(new TokenWindow(limit, durationSec, unit, FindReset(l), index));
                        }
                    }
                    else if (type == "TIME_LIMIT")
                    {
                        mcp ??= ToLimitInfo(l, data);
                    }
                }
                finally
                {
                    index++;
                }
            }
        }

        // 精确按 unit 区分：3=5 小时窗口，6=周窗口
        var fiveHour = windows.Find(w => w.Unit == HourUnit)?.Limit;
        var week = windows.Find(w => w.Unit == WeekUnit)?.Limit;

        // unit 缺失时按窗口时长（或重置时间远近）排序兜底：最短→5h，次短→周
        if (fiveHour == null && week == null && windows.Count > 0)
        {
            var ranked = windows
                .OrderBy(w => w.DurationSec ?? long.MaxValue)
                .ThenBy(w => w.Reset is { } r ? Math.Max(0, (r - now).TotalSeconds) : double.MaxValue)
                .ThenBy(w => w.Index)
                .ToList();
            fiveHour = ranked.ElementAtOrDefault(0)?.Limit;
            week = ranked.ElementAtOrDefault(1)?.Limit;
        }

        return new UsageSnapshot
        {
            FiveHour = fiveHour,
            Week = week,
            Mcp = mcp,
            Level = data.Prop("level").Str(),
            RawLimits = rawLines,
            FetchedAt = now,
        };
    }

    static LimitInfo? ToLimitInfo(JsonElement l, JsonElement data)
    {
        var used = l.Prop("percentage").Num();
        int? remaining = used is double u ? (int)Math.Round(Math.Clamp(100 - u, 0, 100)) : null;

        string? detail = null;
        string? detailLabel = null;
        if (l.Prop("currentValue").Num() is double && l.Prop("usage").Num() is double)
        {
            detail = $"{FmtNum(l.Prop("currentValue").Num())}/{FmtNum(l.Prop("usage").Num())}";
            detailLabel = "已用";
        }
        else if (l.Prop("remaining").Num() != null && l.Prop("usage").Num() != null)
        {
            detail = $"{FmtNum(l.Prop("remaining").Num())}/{FmtNum(l.Prop("usage").Num())}";
            detailLabel = "剩余";
        }

        var unit = ToInt(l.Prop("unit").Num());
        var reset = ExtractResetLabel(l, data, weekly: unit == WeekUnit);
        if (remaining == null && detail == null && reset == null) return null;

        var isCredit = l.Prop("type").Str() == "CREDIT_LIMIT";
        string? label = unit == HourUnit ? (isCredit ? "5h 积分" : "5h token")
            : unit == WeekUnit ? (isCredit ? "周积分" : "周额度")
            : null;
        return new LimitInfo(label, remaining, detail, detailLabel, reset);
    }

    static string? ExtractResetLabel(JsonElement item, JsonElement data, bool weekly)
    {
        foreach (var source in new[] { item, data, item.Prop("usageDetails") ?? default })
        {
            if (source.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in ResetKeys)
            {
                var v = source.Prop(key);
                if (v == null) continue;
                var s = FormatTime(v, weekly);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    static DateTimeOffset? FindReset(JsonElement item)
    {
        foreach (var key in ResetKeys)
        {
            var v = item.Prop(key);
            if (v == null) continue;
            var t = ParseTime(v);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>兼容数字时间戳（秒/毫秒）与字符串日期。</summary>
    static DateTimeOffset? ParseTime(JsonElement? v)
    {
        if (v.Num() is double n and > 0)
        {
            return n > 1e12
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)n).ToLocalTime()
                : DateTimeOffset.FromUnixTimeSeconds((long)n).ToLocalTime();
        }
        var s = v.Str();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var t)
            ? t
            : null;
    }

    /// <summary>今天显示 HH:mm；周窗口显示 周X HH:mm；其他显示 MM-dd HH:mm（与插件 formatTimeValue 一致）。</summary>
    static string? FormatTime(JsonElement? v, bool weekly)
    {
        var t = ParseTime(v);
        if (t == null) return v.Str()?.Trim();
        var d = t.Value.LocalDateTime;
        string hm = d.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (weekly) return $"{WeekdayNames[(int)d.DayOfWeek]} {hm}";
        if (d.Date == DateTime.Today) return hm;
        return d.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + hm;
    }

    static string FmtNum(double? d) =>
        d is double v
            ? v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture)
            : "?";

    static int? ToInt(double? d) => d is double v ? (int)v : null;

    sealed record TokenWindow(LimitInfo Limit, long? DurationSec, int? Unit, DateTimeOffset? Reset, int Index);
}

internal static class JsonExtensions
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : null;

    public static JsonElement? Prop(this JsonElement? e, string name) =>
        e is { } el ? el.Prop(name) : null;

    public static string? Str(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static string? Str(this JsonElement? e) => e is { } el ? el.Str() : null;

    public static double? Num(this JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    public static double? Num(this JsonElement? e) => e is { } el ? el.Num() : null;

    public static bool? BoolVal(this JsonElement e) =>
        e.ValueKind == JsonValueKind.True ? true
        : e.ValueKind == JsonValueKind.False ? false
        : null;

    public static bool? BoolVal(this JsonElement? e) => e is { } el ? el.BoolVal() : null;
}
