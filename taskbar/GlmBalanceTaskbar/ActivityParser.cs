using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>活跃度数据（对应插件的 ActivityInfo）。</summary>
public sealed class ActivityInfo
{
    public long? TotalTokens { get; init; }
    public string? DurationLabel { get; init; }
    public int? CurrentStreakDays { get; init; }
    public int? LongestStreakDays { get; init; }
    public long? TodayTokens { get; init; }
    public long? PeakTokens { get; init; }
    public string? PeakDate { get; init; }
    public string? Spark { get; init; }
    /// <summary>近 14 天每日 tokens（末尾对齐今天，柱状图用）</summary>
    public long[]? DailyTokens { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>
/// /api/monitor/credit-usage/activity 响应解析（移植自插件 parseActivity）。
/// </summary>
public static class ActivityParser
{
    static readonly string[] SparkChars = { "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█" };
    const int SparkWidth = 14;

    public static ActivityInfo? Parse(JsonElement root, DateTimeOffset now)
    {
        var data = root.Prop("data");
        var s = data?.Prop("summary");
        if (s is not { ValueKind: JsonValueKind.Object }) return null;

        var tokens = new List<double>();
        if (data?.Prop("series") is { ValueKind: JsonValueKind.Array } series)
        {
            foreach (var x in series.EnumerateArray())
            {
                tokens.Add(x.Prop("totalTokens").Num() ?? 0);
            }
        }

        var info = new ActivityInfo
        {
            TotalTokens = ToLong(s.Prop("totalTokens").Num()),
            DurationLabel = s.Prop("totalUsageDurationMs").Num() is double ms ? FmtDuration((long)ms) : null,
            CurrentStreakDays = ToInt(s.Prop("currentStreakDays").Num()),
            LongestStreakDays = ToInt(s.Prop("longestStreakDays").Num()),
            TodayTokens = tokens.Count > 0 ? (long)tokens[^1] : null,
            PeakTokens = ToLong(s.Prop("peakDailyTokens").Num()),
            PeakDate = ShortDate(s.Prop("peakDailyTokensDate").Str()),
            Spark = Sparkline(tokens),
            DailyTokens = tokens.Count > 0
                ? tokens.Skip(Math.Max(0, tokens.Count - SparkWidth)).Select(v => (long)v).ToArray()
                : null,
            FetchedAt = now,
        };
        return info.TotalTokens != null || info.TodayTokens != null || info.CurrentStreakDays != null || info.DurationLabel != null
            ? info
            : null;
    }

    static string? Sparkline(List<double> values)
    {
        if (values.Count == 0) return null;
        var slice = values.Skip(Math.Max(0, values.Count - SparkWidth)).ToList();
        double max = slice.Max();
        if (max <= 0) return null;
        var sb = new StringBuilder(slice.Count);
        foreach (var v in slice)
        {
            sb.Append(SparkChars[Math.Min(7, (int)Math.Floor(v / max * 7.999))]);
        }
        return sb.ToString();
    }

    public static string FmtTokens(long n) =>
        n >= 1_000_000_000 ? (n / 1e9).ToString("F2", CultureInfo.InvariantCulture) + "G"
        : n >= 1_000_000 ? Math.Round(n / 1e6).ToString(CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? Math.Round(n / 1e3).ToString(CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>毫秒时长 → "139h53m"（与插件 fmtDuration 一致）。</summary>
    static string FmtDuration(long ms)
    {
        long totalMin = ms / 60000;
        long h = totalMin / 60;
        long m = totalMin % 60;
        return m > 0 ? $"{h}h{m.ToString("D2", CultureInfo.InvariantCulture)}m" : $"{h}h";
    }

    static string? ShortDate(string? d) => !string.IsNullOrEmpty(d) && d.Length >= 10 ? d[5..10] : null;

    static long? ToLong(double? d) => d is double v ? (long)v : null;
    static int? ToInt(double? d) => d is double v ? (int)v : null;
}
