namespace GlmBalanceTaskbar;

/// <summary>
/// UI 侧委托探活/拉取（G3/§2.4）：对既有 relay 实例（headless 或另一 UI）做 /health + /status 探测，
/// 委托态下只读渲染远端快照、零写盘（单写者原则）。回环 HttpClient 静态复用；
/// /health 400ms 超时（避免拖慢启动）、/status 1.5s 超时。
/// </summary>
sealed class DelegatedRelayClient
{
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });

    /// <summary>委托目标端口（来自配置 relay.port；同一配置文件推导，指向的实例按定义就是该端口的中转服务）。</summary>
    public int Port { get; }

    string Base => $"http://127.0.0.1:{Port}/";

    public DelegatedRelayClient(int port) => Port = port;

    /// <summary>同步探活（AppContext 构造期用）：/health 200 → /status 可解析 → RelayStatus；否则 null。</summary>
    public RelayStatus? ProbeOnce() => ProbeAsync().GetAwaiter().GetResult();

    /// <summary>探活 + 拉取状态：/health（400ms）→ /status（1.5s）→ TryParse；任一失败返回 null（未委托语义）。</summary>
    public async Task<RelayStatus?> ProbeAsync(CancellationToken ct = default)
    {
        if (await GetAsync(Base + "health", 400, ct).ConfigureAwait(false) == null) return null;
        return await FetchStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>仅拉取 /status（委托态 30s 节流增量刷新用；1.5s 超时；解析失败/null 均为探活失败）。</summary>
    public async Task<RelayStatus?> FetchStatusAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(Base + "status", 1500, ct).ConfigureAwait(false);
        return json == null ? null : RelayStatusJson.TryParse(json);
    }

    /// <summary>POST /api/shutdown（注销流兜底，§2.5：/End 后探活仍存活时终止 headless；Bearer localToken）。
    /// 返回 true = 服务端 200（进程将退出）；false = 失联/拒绝/超时（不阻塞注销流程）。</summary>
    public async Task<bool> RequestShutdown(string localToken, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + "api/shutdown");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + localToken);
            using var rsp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return (int)rsp.StatusCode == 200;
        }
        catch
        {
            return false;
        }
    }

    static async Task<string?> GetAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var rsp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if ((int)rsp.StatusCode != 200) return null;
            return await rsp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
