namespace GlmBalanceTaskbar;

/// <summary>一条请求日志（不含请求体内容）。</summary>
public sealed record RequestLogEntry(
    DateTimeOffset Time,
    string Model,
    string ProviderId,
    IReadOnlyList<string> Chain,
    int? StatusCode,
    long DurationMs,
    string? Error);

/// <summary>请求日志：环形缓冲 500 条（日志失败不影响主流程）。</summary>
public sealed class RequestLog
{
    const int Capacity = 500;
    readonly object _gate = new();
    readonly Queue<RequestLogEntry> _items = new();

    public void Add(RequestLogEntry entry)
    {
        lock (_gate)
        {
            if (_items.Count >= Capacity) _items.Dequeue();
            _items.Enqueue(entry);
        }
    }

    public List<RequestLogEntry> Tail(int n)
    {
        lock (_gate)
        {
            return _items.Skip(Math.Max(0, _items.Count - n)).ToList();
        }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }
}
