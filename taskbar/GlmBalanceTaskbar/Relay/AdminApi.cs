using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlmBalanceTaskbar;

/// <summary>
/// /api/* 管理 JSON API（全部 Bearer localToken，无 token → 401；免鉴权模式放行）。
/// key 只写不读：任何响应中的 apiKey 一律掩码。配置写回只走 RelayManager.PersistAndApply。
/// </summary>
public sealed class AdminApi
{
    readonly RelayManager _mgr;

    public AdminApi(RelayManager mgr) => _mgr = mgr;

    public async Task HandleAsync(HttpListenerContext ctx, bool authorized)
    {
        var req = ctx.Request;
        var rsp = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        if (!authorized)
        {
            await WriteAsync(rsp, 401, "{\"error\":\"unauthorized\"}").ConfigureAwait(false);
            return;
        }

        try
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, ...
            switch (segments.Length >= 2 ? segments[1] : "")
            {
                case "overview" when method == "GET":
                    await WriteAsync(rsp, 200, RelayJson.Serialize(_mgr.Snapshot())).ConfigureAwait(false);
                    return;

                case "providers":
                    await HandleProvidersAsync(req, rsp, method, segments).ConfigureAwait(false);
                    return;

                case "agents":
                    await HandleAgentsAsync(req, rsp, method, segments).ConfigureAwait(false);
                    return;

                case "settings":
                    await HandleSettingsAsync(req, rsp, method).ConfigureAwait(false);
                    return;

                case "logs" when method == "GET":
                    var tail = QueryInt(req, "tail", 200);
                    await WriteAsync(rsp, 200, RelayJson.Serialize(new { entries = _mgr.LogTail(Math.Clamp(tail, 1, 500)) }))
                        .ConfigureAwait(false);
                    return;

                default:
                    await WriteAsync(rsp, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            try { await WriteAsync(rsp, 500, RelayJson.Serialize(new { error = ex.Message })).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    async Task HandleProvidersAsync(HttpListenerRequest req, HttpListenerResponse rsp, string method, string[] segments)
    {
        // /api/providers | /api/providers/{id} | /api/providers/{id}/move|test | /api/providers/import
        var warnings = new List<string>();

        if (segments.Length == 2 && method == "GET")
        {
            var list = _mgr.Pool.Select(rt => ProviderToJson(rt)).ToList();
            await WriteAsync(rsp, 200, RelayJson.Serialize(new { providers = list })).ConfigureAwait(false);
            return;
        }

        if (segments.Length == 3 && segments[2] == "import" && method == "POST")
        {
            var body = await ReadJsonAsync(req).ConfigureAwait(false);
            var ids = body?["ids"] as JsonArray;
            if (ids == null) { await WriteAsync(rsp, 400, "{\"error\":\"缺少 ids 数组\"}").ConfigureAwait(false); return; }

            var discovered = await DiscoverAllAsync().ConfigureAwait(false);
            var added = new List<string>();
            var skipped = new List<string>();
            var snapshot = _mgr.ProvidersSnapshot();
            var existing = new HashSet<string>(snapshot.Select(p => p.Id), StringComparer.Ordinal);
            var nextPriority = snapshot.Count > 0 ? snapshot.Max(p => p.Priority) + 1 : 1;
            foreach (var node in ids)
            {
                var fullId = node?.ToString() ?? "";
                var dash = fullId.IndexOf(':');
                var agent = dash > 0 ? fullId[..dash] : "";
                var id = dash > 0 ? fullId[(dash + 1)..] : fullId;
                var hit = discovered.FirstOrDefault(d => d.Agent == agent && d.Id == id);
                if (hit == null) { skipped.Add(fullId + "(未发现)"); continue; }
                if (existing.Contains(id)) { skipped.Add(id + "(已在池)"); continue; }
                var pc = new ProviderConfig
                {
                    Id = id,
                    Name = hit.Name == id ? id : hit.Name,
                    Priority = nextPriority++,
                    Probe = hit.SuggestedProbeType,
                    BaseUrl = hit.BaseUrl ?? "",
                    ApiKey = hit.ApiKey ?? "",
                    Models = hit.Models?.ToList() ?? new List<string>(),
                    Enabled = true,
                };
                _mgr.MutateProviders(list => { list.Add(pc); return true; });
                existing.Add(id);
                added.Add(id);
            }
            if (added.Count == 0)
            {
                await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok = true, added, skipped, message = "没有可导入的新渠道" })).ConfigureAwait(false);
                return;
            }
            var okWrite = _mgr.PersistAndApply(relay => ReplaceProviders(relay, _mgr.ProvidersSnapshot()), warnings);
            await WriteAsync(rsp, okWrite ? 200 : 500,
                RelayJson.Serialize(new { ok = okWrite, added, skipped, warnings })).ConfigureAwait(false);
            return;
        }

        // 全量重排优先级（M1：置于 404 前置之前、与 import 同级——若放在 switch(segments[3])
        // 自然位置则永不可达；"reorder" 由此成为渠道 id 保留字：POST /api/providers/reorder 被
        // 本端点拦截（现状该路径为 404 not_found，无既有功能破坏），对该渠道的
        // PUT/GET/DELETE /api/providers/reorder 不受影响）
        if (segments.Length == 3 && segments[2] == "reorder" && method == "POST")
        {
            var body = await ReadJsonAsync(req).ConfigureAwait(false);
            var idsNode = body?["ids"] as JsonArray;
            if (idsNode == null) { await WriteAsync(rsp, 400, "{\"error\":\"缺少 ids 数组\"}").ConfigureAwait(false); return; }
            var ids = idsNode.Where(n => n != null).Select(n => n!.ToString()).ToList();

            // 全排列校验：数量相等、无重复、无未知 id；空池+空数组 → 200 空操作（m7），
            // 非空池+空数组 → 400
            var snapshot = _mgr.ProvidersSnapshot();
            var poolIds = snapshot.Select(p => p.Id).ToList();
            var valid = ids.Count == poolIds.Count
                && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
                && ids.All(id => poolIds.Contains(id));
            if (!valid)
            {
                await WriteAsync(rsp, 400, "{\"error\":\"ids 必须恰好包含全部渠道 id（无重复、无缺失）\"}").ConfigureAwait(false);
                return;
            }

            // 按 ids 重排 + Priority 重编号 1..n（与 NextPriority/max+1 及 move 相邻交换兼容）
            _mgr.MutateProviders(list =>
            {
                var byId = list.ToDictionary(p => p.Id, StringComparer.Ordinal);
                var reordered = ids.Select(id => byId[id]).ToList();
                list.Clear();
                list.AddRange(reordered);
                for (var i = 0; i < list.Count; i++)
                {
                    list[i].Priority = i + 1;
                }
                return true;
            });
            var ok = _mgr.PersistAndApply(relay => ReplaceProviders(relay, _mgr.ProvidersSnapshot()), warnings);
            await WriteAsync(rsp, ok ? 200 : 500, RelayJson.Serialize(new { ok, warnings })).ConfigureAwait(false);
            return;
        }

        // 其余均需 {id}
        var providerId = segments.Length >= 3 ? Uri.UnescapeDataString(segments[2]) : "";
        var rt = _mgr.Pool.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        if (rt == null && method != "PUT") { await WriteAsync(rsp, 404, "{\"error\":\"渠道不存在\"}").ConfigureAwait(false); return; }

        switch (segments.Length >= 4 ? segments[3] : "")
        {
            case "" when method == "GET":
                await WriteAsync(rsp, 200, RelayJson.Serialize(ProviderToJson(rt!))).ConfigureAwait(false);
                return;

            case "pin" when method == "POST": // 设为强制通道（sticky pin，运行时单槽，不写文件）
            {
                // SetForced 以池内存在为权威校验：false = 渠道不在池（含前置 404 后池被重建的
                // 极小竞态，m5）→ 与前置同语义 404
                if (!_mgr.Router.SetForced(providerId))
                {
                    await WriteAsync(rsp, 404, "{\"error\":\"渠道不存在\"}").ConfigureAwait(false);
                    return;
                }
                await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok = true, forcedId = providerId })).ConfigureAwait(false);
                return;
            }

            case "pin" when method == "DELETE": // 解除强制（幂等；pin 指向其他 id 时不清除）
            {
                _mgr.Router.ClearForced(providerId);
                await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok = true, forcedId = (string?)null })).ConfigureAwait(false);
                return;
            }

            case "" when method == "PUT": // 新建或更新（key 只写不读）
            {
                var body = await ReadJsonAsync(req).ConfigureAwait(false);
                if (body == null) { await WriteAsync(rsp, 400, "{\"error\":\"请求体不是合法 JSON\"}").ConfigureAwait(false); return; }

                var isNew = rt == null;
                var pc = isNew ? new ProviderConfig { Id = providerId, Priority = NextPriority() } : CloneConfig(rt!.Config);
                ApplyProviderPatch(pc, body);
                pc.Id = providerId;

                // 内存同步更新（ApplyOptions 依此重建池），再写回配置文件（F6：串行化）
                _mgr.MutateProviders(list =>
                {
                    var i2 = list.FindIndex(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
                    if (i2 >= 0) list[i2] = pc; else list.Add(pc);
                    return true;
                });

                Func<JsonObject, JsonObject> mutate = relay =>
                {
                    var arr = ProvidersArray(relay);
                    var idx = arr.AsArray().ToList().FindIndex(n => (string?)n?["id"] == providerId);
                    var node = pc.ToJsonNode(); // 真实 key：仅写回配置文件，不经任何响应返回
                    if (idx >= 0) arr[idx] = node; else arr.Add(node);
                    return relay;
                };
                var ok = _mgr.PersistAndApply(mutate, warnings);
                if (ok)
                {
                    // 配置修正后：非冷却渠道立即重新参与路由；冷却中的渠道保留冷却，
                    // 到期惰性归位时经 RefreshKey 重解析新配置（H1-F2：避免持旧 key 归位）
                    var fresh = _mgr.Pool.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
                    if (fresh != null && fresh.Inspect(DateTimeOffset.Now).State != ProviderState.Cooldown)
                    {
                        fresh.ForceReevaluate();
                    }
                }
                await WriteAsync(rsp, ok ? 200 : 500,
                    RelayJson.Serialize(new { ok, created = isNew, warnings, message = ok ? (isNew ? "已新建并生效" : "已更新并生效") : "写回失败" })).ConfigureAwait(false);
                return;
            }

            case "" when method == "DELETE":
            {
                if (rt == null) { await WriteAsync(rsp, 404, "{\"error\":\"渠道不存在\"}").ConfigureAwait(false); return; }
                _mgr.MutateProviders(list => list.RemoveAll(p => p.Id == rt.Id) > 0);
                // 删除被 pin 渠道 → 连带清 pin（R4 双保险之一；ReplacePool 另有防御性校验）
                if (_mgr.Router.ForcedId == rt.Id) _mgr.Router.ClearForced(rt.Id);
                var ok = _mgr.PersistAndApply(relay => ReplaceProviders(relay, _mgr.ProvidersSnapshot()), warnings);
                await WriteAsync(rsp, ok ? 200 : 500,
                    RelayJson.Serialize(new { ok, warnings, message = ok ? $"已删除 {rt.Id} 并生效" : "写回失败" })).ConfigureAwait(false);
                return;
            }

            case "move" when method == "POST": // 优先级上移/下移（按 Priority 升序相邻交换）
            {
                var body = await ReadJsonAsync(req).ConfigureAwait(false);
                var dir = body?["dir"]?.ToString() ?? "up";
                var moved = _mgr.MutateProviders(list =>
                {
                    var ordered = list.OrderBy(p => p.Priority).ToList();
                    var idx = ordered.FindIndex(p => p.Id == providerId);
                    var swapWith = dir == "up" ? idx - 1 : idx + 1;
                    if (idx < 0 || swapWith < 0 || swapWith >= ordered.Count) return false;
                    (ordered[idx].Priority, ordered[swapWith].Priority) = (ordered[swapWith].Priority, ordered[idx].Priority);
                    return true;
                });
                if (!moved)
                {
                    await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok = true, message = "已在边界，无需移动" })).ConfigureAwait(false);
                    return;
                }
                var ok = _mgr.PersistAndApply(relay => ReplaceProviders(relay, _mgr.ProvidersSnapshot()), warnings);
                await WriteAsync(rsp, ok ? 200 : 500, RelayJson.Serialize(new { ok, warnings })).ConfigureAwait(false);
                return;
            }

            case "test" when method == "POST":
            {
                var (ok, message) = await _mgr.TestProviderAsync(rt!, CancellationToken.None).ConfigureAwait(false);
                await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok, message })).ConfigureAwait(false);
                return;
            }

            default:
                await WriteAsync(rsp, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
                return;
        }
    }

    async Task HandleAgentsAsync(HttpListenerRequest req, HttpListenerResponse rsp, string method, string[] segments)
    {
        // GET /api/agents | POST /api/agents/{agent}/deploy
        if (segments.Length == 2 && method == "GET")
        {
            var discovered = await DiscoverAllAsync().ConfigureAwait(false);
            var poolIds = new HashSet<string>(_mgr.Pool.Select(rt => rt.Id), StringComparer.Ordinal);
            var agents = discovered
                .GroupBy(d => d.Agent)
                .Select(g => new
                {
                    agent = g.Key,
                    providers = g.Select(d => new
                    {
                        d.Id,
                        d.Name,
                        d.BaseUrl,
                        apiKey = RelayJson.MaskKey(d.ApiKey ?? ""),
                        d.Models,
                        d.SuggestedProbeType,
                        d.Source,
                        inPool = poolIds.Contains(d.Id),
                    }).ToList(),
                })
                .ToList();
            await WriteAsync(rsp, 200, RelayJson.Serialize(new { agents })).ConfigureAwait(false);
            return;
        }

        if (segments.Length == 4 && segments[3] == "deploy" && method == "POST")
        {
            var agent = segments[2];
            var deployer = _mgr.CreateDeployers().FirstOrDefault(d => d.AgentId == agent);
            if (deployer == null) { await WriteAsync(rsp, 404, "{\"error\":\"未知智能体\"}").ConfigureAwait(false); return; }

            var setDefault = QueryBool(req, "setDefault", false);
            var dryRun = QueryBool(req, "dryRun", false);
            var confirm = QueryBool(req, "confirm", false);
            var request = new DeployRequest(_mgr.BaseUrl, _mgr.EffectiveToken, _mgr.Router.ModelUnion(), setDefault);

            if (dryRun || !confirm)
            {
                var preview = await deployer.PreviewAsync(request).ConfigureAwait(false);
                await WriteAsync(rsp, 200, RelayJson.Serialize(new { ok = preview.Ready, preview = preview.Summary, agent })).ConfigureAwait(false);
                return;
            }

            var result = await deployer.DeployAsync(request).ConfigureAwait(false);
            await WriteAsync(rsp, result.Success ? 200 : 500,
                RelayJson.Serialize(new { ok = result.Success, message = result.Message, backup = result.BackupPath, agent })).ConfigureAwait(false);
            return;
        }

        await WriteAsync(rsp, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
    }

    async Task HandleSettingsAsync(HttpListenerRequest req, HttpListenerResponse rsp, string method)
    {
        var warnings = new List<string>();
        if (method == "GET")
        {
            var o = _mgr.Options;
            await WriteAsync(rsp, 200, RelayJson.Serialize(new
            {
                port = o.Port,
                probeIntervalSeconds = o.ProbeIntervalSeconds,
                degradeBelowPct = o.DegradeBelowPct,
                restoreAbovePct = o.RestoreAbovePct,
                cooldown = new { o.Cooldown.QuotaSeconds, o.Cooldown.ErrorThreshold, o.Cooldown.ErrorSeconds },
                hasToken = o.LocalToken.Length > 0,
                authDisabled = !_mgr.Running ? false : _mgr.EffectiveToken.Length == 0,
                adminUrl = o.AdminUrl,
                baseUrl = o.BaseUrl,
                running = _mgr.Running,
                enabled = o.Enabled,
                initialized = _mgr.Initialized,
            })).ConfigureAwait(false);
            return;
        }
        if (method == "PUT")
        {
            var body = await ReadJsonAsync(req).ConfigureAwait(false);
            if (body == null) { await WriteAsync(rsp, 400, "{\"error\":\"请求体不是合法 JSON\"}").ConfigureAwait(false); return; }
            var o = _mgr.Options;
            var portChanged = false;

            // 中转启停开关（与 DetailForm 开关同源语义，RelayManager.SetEnabled）：
            // enabled=true → 未运行即启用（未初始化时自动初始化空配置）；
            // enabled=false → 停止运行时并把配置置为关闭（覆盖启动失败的残留态）；失败回滚进 warnings。
            var startStopWarnings = new List<string>();
            var enabledApplied = true;
            var disableAfterResponse = false; // 关闭路径：Stop 推迟到本响应写出之后（见下方）
            if (body["enabled"] is JsonNode en)
            {
                var want = en.GetValue<bool>();
                if (want && !_mgr.Running)
                {
                    enabledApplied = _mgr.SetEnabled(true, startStopWarnings);
                }
                else if (!want && _mgr.Options.Enabled)
                {
                    // 与 SetEnabled(false) 相同语义，但拆成两步：Stop 会 Shutdown listener 并
                    // 切断当前连接，响应必须在停机前写出，否则客户端只会看到连接重置。
                    // ① 此处仅持久化 enabled=false；② 响应写出完成后再停运行时（下方 fire-and-forget）。
                    enabledApplied = _mgr.PersistAndApply(relay => { relay["enabled"] = false; return relay; }, startStopWarnings);
                    if (enabledApplied)
                    {
                        _mgr.Options.Enabled = false;
                        disableAfterResponse = true;
                    }
                }
            }

            if ((int?)body["port"] is int port && port != o.Port)
            {
                o.Port = Math.Clamp(port, 1, 65535);
                portChanged = true; // 生效需重启程序
            }
            if ((int?)body["probeIntervalSeconds"] is int pi) o.ProbeIntervalSeconds = Math.Clamp(pi, 10, 3600);
            if ((int?)body["degradeBelowPct"] is int db) o.DegradeBelowPct = Math.Clamp(db, 0, 100);
            if ((int?)body["restoreAbovePct"] is int ra) o.RestoreAbovePct = Math.Clamp(ra, 0, 100);
            if (body["cooldown"] is JsonObject cd)
            {
                if ((int?)cd["quotaSeconds"] is int qs) o.Cooldown.QuotaSeconds = Math.Clamp(qs, 5, 86400);
                if ((int?)cd["errorThreshold"] is int et) o.Cooldown.ErrorThreshold = Math.Clamp(et, 1, 100);
                if ((int?)cd["errorSeconds"] is int es) o.Cooldown.ErrorSeconds = Math.Clamp(es, 5, 86400);
            }

            var tokenReset = body["resetToken"]?.GetValue<bool>() == true;
            var newToken = "";
            if (tokenReset)
            {
                newToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                o.LocalToken = newToken;
                warnings.Add("token 已重置：opencode 端 localrelay provider 需同步重新部署，否则请求将 401");
            }

            var ok = enabledApplied && _mgr.PersistAndApply(relay => _mgr.Options.ToJson(), warnings); // 设置整节点重序列化（含 providers 原样）
            var allWarnings = startStopWarnings.Concat(warnings).ToList();
            await WriteAsync(rsp, ok ? 200 : 500, RelayJson.Serialize(new
            {
                ok,
                warnings = allWarnings,
                restartRequired = portChanged,
                tokenReset,
                newToken = tokenReset ? newToken : null,
                running = disableAfterResponse ? false : _mgr.Running,
                initialized = _mgr.Initialized,
                message = !enabledApplied
                    ? "中转启停失败（见 warnings）"
                    : disableAfterResponse
                        ? "中转已关闭"
                        : ok
                            ? portChanged ? "设置已写回；端口变更需重启程序生效" : "设置已写回并即时生效"
                            : "写回失败",
            })).ConfigureAwait(false);
            if (disableAfterResponse)
            {
                // 响应已提交（WriteAsync 内 rsp.Close()），现在停机安全；后台执行以免阻塞响应收尾。
                _ = Task.Run(() =>
                {
                    try { _mgr.Stop(); }
                    catch { /* 停机异常经 /status / 详情窗可见，不影响已发出的响应 */ }
                });
            }
            return;
        }
        await WriteAsync(rsp, 405, "{\"error\":\"method_not_allowed\"}").ConfigureAwait(false);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>API 响应节点：key 一律掩码（只写不读），附 hasKey 供 UI 判断。</summary>
    static JsonObject ProviderToJson(ProviderRuntime rt)
    {
        var p = rt.Config;
        return new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["priority"] = p.Priority,
            ["probe"] = p.Probe,
            ["baseURL"] = p.BaseUrl,
            ["apiKey"] = RelayJson.MaskKey(p.ApiKey),
            ["hasKey"] = !string.IsNullOrEmpty(p.ApiKey),
            ["models"] = new JsonArray(p.Models.Select(m => JsonValue.Create(m)).ToArray()),
            ["enabled"] = p.Enabled,
        };
    }

    static void ApplyProviderPatch(ProviderConfig pc, JsonObject body)
    {
        if (body["name"] is JsonNode n) pc.Name = n.ToString();
        if ((int?)body["priority"] is int pr) pc.Priority = pr;
        if (body["probe"] is JsonNode pb) pc.Probe = pb.ToString()!;
        if (body["baseURL"] is JsonNode bu) pc.BaseUrl = bu.ToString()!;
        if (body["baseUrl"] is JsonNode bu2) pc.BaseUrl = bu2.ToString()!;
        if (body["apiKey"] is JsonNode ak)
        {
            var key = ak.ToString() ?? "";
            // M3：掩码形态视为"未修改"，保留原 key 不覆盖，防 GET→PUT 回环把掩码写成真 key。
            // 除标准掩码（含 U+2026 省略号）外，兼容传输中省略号被损坏的形态：
            // "sk-?" + 恰好 4 位尾巴 且与当前 key 尾 4 位一致（真实 key 远长于 8 字符，误伤可忽略）。
            if (!IsMaskedKey(key, pc.ApiKey)) pc.ApiKey = key;
        }
        if (body["models"] is JsonArray ms)
        {
            pc.Models = ms.Where(m => m != null).Select(m => m!.ToString()).Where(s => s.Length > 0).ToList();
        }
        if (body["enabled"] is JsonNode en) pc.Enabled = en.GetValue<bool>();
    }

    internal static bool IsMaskedKey(string incoming, string original)
    {
        if (string.IsNullOrEmpty(incoming)) return false;
        if (incoming.Contains('…')) return true; // 标准掩码：sk-…后4位
        // 损坏变体：短串（≤9）以 "sk-" 开头、含一个非字母数字占位符、尾 4 位与当前 key 尾 4 位一致
        if (incoming.Length is >= 7 and <= 9 && incoming.StartsWith("sk-", StringComparison.Ordinal))
        {
            var tail = incoming[^4..];
            var mid = incoming[3..^4];
            var originalTail = original.Length >= 4 ? original[^4..] : original;
            var midIsPlaceholder = mid.Length == 1 && !char.IsLetterOrDigit(mid[0]);
            return midIsPlaceholder && tail == originalTail;
        }
        return false;
    }

    int NextPriority()
    {
        var snapshot = _mgr.ProvidersSnapshot();
        return snapshot.Count > 0 ? snapshot.Max(p => p.Priority) + 1 : 1;
    }

    static ProviderConfig CloneConfig(ProviderConfig p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Priority = p.Priority,
        Probe = p.Probe,
        BaseUrl = p.BaseUrl,
        ApiKey = p.ApiKey,
        Models = p.Models.ToList(),
        Enabled = p.Enabled,
    };

    static JsonArray ProvidersArray(JsonObject relayRoot)
    {
        if (relayRoot["providers"] is not JsonArray arr)
        {
            arr = new JsonArray();
            relayRoot["providers"] = arr;
        }
        return arr;
    }

    static JsonObject ReplaceProviders(JsonObject relayRoot, List<ProviderConfig> providers)
    {
        relayRoot["providers"] = new JsonArray(providers.Select(p => p.ToJsonNode()).ToArray());
        return relayRoot;
    }

    static async Task<JsonObject?> ReadJsonAsync(HttpListenerRequest req)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    async Task<IReadOnlyList<DiscoveredProvider>> DiscoverAllAsync() =>
        await _mgr.DiscoverAsync().ConfigureAwait(false);

    static int QueryInt(HttpListenerRequest req, string name, int def) =>
        int.TryParse(req.QueryString[name], out var v) ? v : def;

    static bool QueryBool(HttpListenerRequest req, string name, bool def) =>
        bool.TryParse(req.QueryString[name], out var v) ? v : def;

    static async Task WriteAsync(HttpListenerResponse rsp, int status, string json)
    {
        rsp.StatusCode = status;
        rsp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        rsp.ContentLength64 = bytes.Length;
        await rsp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        rsp.Close();
    }
}
