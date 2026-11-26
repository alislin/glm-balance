using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlmBalanceTaskbar;

/// <summary>
/// CLI 子命令入口（子命令 + 既有 flag 兼容并存；帮助体系三层：总览/分组/子命令，见 CliHelp）。
/// 直连运行实例 AdminApi（127.0.0.1:relay.port，Bearer localToken），token/port 取自配置文件
/// （--dev → DevMode.ConfigRoot，否则 exe 同目录；--port 显式覆盖）。
/// 退出码：0=成功 1=失败（含业务错误）2=实例不可达 3=用法错误。
/// dev（--dev 修饰子命令）仅切换目标实例与配置目录，不触发 dev 硬禁（硬禁只针对 --headless/--task-*）。
/// </summary>
static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitFail = 1;
    public const int ExitUnreachable = 2;
    public const int ExitUsage = 3;

    static int? _portOverride;
    static string? _configDirOverride;
    static HttpClient? _http;

    static HttpClient Http => _http ??= new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });

    /// <summary>注入全局选项（Main 分派子命令前调用；CLI 进程一次）。</summary>
    public static void Initialize(int? portOverride, string? configDirOverride)
    {
        _portOverride = portOverride;
        _configDirOverride = configDirOverride;
    }

    /// <summary>
    /// 子命令分派（Main 中在 --once 判定之前调用）。返回 null = args 不是 CLI 子命令
    /// （走既有 flag 分派 / UI）；非 null = 已处理并返回退出码。
    /// 分派规则：剥离全局选项（--json/--dev/--port/--config-dir 及 --port/--config-dir 的取值）后，
    /// 第一个非 -- 开头的 token = 命令名；未知命令返回 ExitUsage（由 Main 决定报错输出）。
    /// </summary>
    public static int? TryDispatch(string[] args, bool jsonOut)
    {
        var rest = ExtractGlobals(args, out var portOverride, out var configDir);
        if (rest.Length == 0) return null; // 纯全局选项，无命令 → 非 CLI 调用
        var cmd = rest[0];
        if (cmd.StartsWith('-')) return null;

        if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            CliHelp.Print(rest.Length > 1 ? rest[1..] : Array.Empty<string>());
            return ExitOk;
        }

        var entry = CliRegistry.Find(cmd, rest.Length > 1 && !rest[1].StartsWith('-') ? rest[1] : null);
        if (entry == null)
        {
            if (jsonOut)
            {
                Console.WriteLine(RelayJson.Serialize(new { error = "unknown_command", command = cmd }));
            }
            else
            {
                var input = entry == null && rest.Length > 1 && !rest[1].StartsWith('-') ? cmd + " " + rest[1] : cmd;
                Console.WriteLine($"error: 未知命令 \"{input}\"");
                var hint = CliRegistry.Nearest(cmd, rest.Length > 1 && !rest[1].StartsWith('-') ? rest[1] : null);
                if (hint != null) Console.WriteLine($"最接近的命令：{hint}");
                Console.WriteLine($"运行 GlmBalanceTaskbar.exe help 查看全部命令");
            }
            return ExitUsage;
        }

        Initialize(portOverride, configDir);
        // 二级命令（channel add …）跳过 group+sub 两个 token；单层命令跳过 1 个
        var runArgs = entry.Group == entry.Sub ? rest[1..] : rest[2..];
        try
        {
            return entry.Run(runArgs, jsonOut);
        }
        catch (CliUsageException ex)
        {
            // 用法错误：打印命令帮助片段（用法行 + 说明），帮助体系与错误就地结合
            Console.WriteLine("error: " + ex.Message);
            Console.WriteLine();
            Console.WriteLine(CliHelp.RenderUsage(entry));
            return ExitUsage;
        }
    }

    /// <summary>
    /// 剥离全局选项并取出取值：--json / --dev 为无值开关（--dev 已由 DevMode 消费）；
    /// --port / --config-dir 各带一个取值。其余 token 原样保留。
    /// </summary>
    static string[] ExtractGlobals(string[] args, out int? portOverride, out string? configDir)
    {
        portOverride = _portOverride;
        configDir = _configDirOverride;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--dev", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                // 无值全局开关：剥离
            }
            else if (a.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                portOverride = Math.Clamp(p, 1, 65535);
                i++;
            }
            else if (a.Equals("--config-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configDir = args[i + 1];
                i++;
            }
            else
            {
                rest.Add(a);
            }
        }
        return rest.ToArray();
    }

    // ─────────────────────────── 连接目标（配置 → port/token） ───────────────────────────

    /// <summary>目标端口：--port 覆盖 &gt; 配置 relay.port &gt; dev 默认 8788 / 生产默认 8787。</summary>
    static int TargetPort()
    {
        if (_portOverride is int p) return p;
        var relay = LoadConfig().Relay;
        if (relay is { } r) return r.Port;
        return DevMode.IsDev ? DevMode.DefaultRelayPort : DevMode.ProductionRelayPort;
    }

    /// <summary>Bearer token：配置 relay.localToken（空 = 免鉴权实例，不发 Authorization 头）。</summary>
    static string TargetToken()
    {
        var relay = LoadConfig().Relay;
        return relay?.LocalToken ?? "";
    }

    /// <summary>配置读取（dev → DevMode.ConfigRoot；--config-dir → HomeOverride 注入；否则 exe 同目录）。</summary>
    static AppConfig LoadConfig()
    {
        var prev = HomeOverride.GetUserProfile();
        if (_configDirOverride != null) HomeOverride.Set(_configDirOverride);
        try
        {
            return AppConfig.Load(DevMode.IsDev ? DevMode.ConfigRoot : null);
        }
        finally
        {
            HomeOverride.Set(prev == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ? null : prev);
        }
    }

    static string Base => $"http://127.0.0.1:{TargetPort()}/";

    /// <summary>HTTP 请求（status=0 = 连接失败/超时 = 实例不可达语义）。</summary>
    static (int Status, string Body) Request(string method, string path, string? jsonBody = null, int timeoutMs = 15000)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var req = new HttpRequestMessage(new HttpMethod(method), Base + path);
            var token = TargetToken();
            if (token.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (jsonBody != null)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            using var rsp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            var body = rsp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            return ((int)rsp.StatusCode, body);
        }
        catch
        {
            return (0, "");
        }
    }

    /// <summary>可达性前置（控制类命令共用）：/health 失败 → 报错 + 修复建议，返回 ExitUnreachable。</summary>
    static int RequireInstance(bool jsonOut)
    {
        var (status, _) = Request("GET", "health", timeoutMs: 2000);
        if (status == 200) return ExitOk;
        return Unreachable(jsonOut);
    }

    static int Unreachable(bool jsonOut)
    {
        var port = TargetPort();
        if (jsonOut)
        {
            Console.WriteLine(RelayJson.Serialize(new { error = "unreachable", port, hint = "中转实例未运行" }));
        }
        else
        {
            Console.WriteLine($"error: 中转实例未运行或不可达（127.0.0.1:{port}）");
            Console.WriteLine("启动 GlmBalanceTaskbar.exe（托盘/详情窗启用中转）或后台 GlmBalanceTaskbar.exe --headless 后重试。");
            Console.WriteLine("首次使用可先执行 GlmBalanceTaskbar.exe --relay-init 初始化配置。");
        }
        return ExitUnreachable;
    }

    /// <summary>管理 API 调用 + 401/403/5xx 统一转述。返回 null = 已输出错误（调用方直接返回 ExitFail）。</summary>
    static (int Status, string Body)? AdminCall(string method, string path, string? jsonBody = null, bool jsonOut = false, int timeoutMs = 15000)
    {
        var (status, body) = Request(method, path, jsonBody, timeoutMs);
        if (status == 0) { Unreachable(jsonOut); return null; }
        if (status is 401 or 403)
        {
            var msg = TryExtractError(body) ?? (status == 401 ? "unauthorized（localToken 不匹配）" : "forbidden");
            Err(jsonOut, $"{status}: {msg}");
            return null;
        }
        return (status, body);
    }

    static string? TryExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["error"]?.ToString() ?? node?["message"]?.ToString();
        }
        catch { return null; }
    }

    static void Err(bool jsonOut, string message) =>
        Console.WriteLine(jsonOut
            ? RelayJson.Serialize(new { error = message })
            : "error: " + message);

    /// <summary>--json 输出：响应体原样美化（解析失败原样输出）。</summary>
    static void PrintJson(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? body);
        }
        catch
        {
            Console.WriteLine(body);
        }
    }

    // ─────────────────────────── 位置参数 / 选项解析辅助 ───────────────────────────

    /// <summary>用法错误（由 TryDispatch 统一打印用法 + 帮助片段）。</summary>
    sealed class CliUsageException(string message) : Exception(message);

    /// <summary>取第 index 个位置参数（缺失 → 用法错误）。</summary>
    static string Arg(string[] pos, int index, string what)
    {
        if (index < pos.Length && pos[index].Length > 0 && !pos[index].StartsWith('-')) return pos[index];
        throw new CliUsageException($"缺少必填参数：{what}");
    }

    /// <summary>选项取值（--opt value 形式；未提供返回 null）。</summary>
    static string? Opt(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-')) return "";
                return args[i + 1];
            }
        }
        return null;
    }

    /// <summary>布尔选项（存在即 true）。</summary>
    static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>选项值必须是合法 int（否则用法错误）。</summary>
    static int? OptInt(string[] args, string name)
    {
        var v = Opt(args, name);
        return v == null ? null : int.TryParse(v, out var i) ? i : throw new CliUsageException($"选项 {name} 需要整数，得到 \"{v}\"");
    }

    /// <summary>选项值必须是合法 bool（true/false，缺省 true）。</summary>
    static bool? OptBool(string[] args, string name)
    {
        var v = Opt(args, name);
        return v == null ? null
            : bool.TryParse(v, out var b) ? b
            : throw new CliUsageException($"选项 {name} 需要 true/false，得到 \"{v}\"");
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ─────────────────────────── 查询类 ───────────────────────────

    internal static int CmdStatus(string[] args, bool jsonOut)
    {
        var (status, body) = Request("GET", "status", timeoutMs: 3000);
        if (status == 0) return Unreachable(jsonOut);
        if (status != 200) { Err(jsonOut, $"HTTP {status}: {TryExtractError(body) ?? body}"); return ExitFail; }
        if (jsonOut) { PrintJson(body); return ExitOk; }

        var st = RelayStatusJson.TryParse(body);
        if (st == null) { Err(jsonOut, "/status 响应解析失败"); return ExitFail; }
        Console.WriteLine($"running={st.Running} enabled={st.Enabled} initialized={st.Initialized} port={st.Port}" +
                          (DevMode.IsDev ? " dev=true" : "") +
                          (st.Headless ? " headless=true" : ""));
        if (st.Forced is { } f) Console.WriteLine($"forced: {f.Id} active={f.Active}" + (f.Reason != null ? $" ({f.Reason})" : ""));
        Console.WriteLine($"current={st.CurrentId ?? "-"} todayRequests={st.TodayRequests} todayFailovers={st.TodayFailovers}" +
                          (st.StartedAt is { } s ? $" uptime={FormatUptime(st.UptimeSec)}" : ""));
        Console.WriteLine($"authDisabled={st.AuthDisabled} models=[{string.Join(", ", st.Models)}]");
        if (st.Providers.Count == 0) { Console.WriteLine("providers: (空)"); return ExitOk; }
        Console.WriteLine("channels:");
        Console.WriteLine($"  {"PRI",-4} {"ID",-20} {"STATE",-9} {"REMAIN",-7} {"REQ",-6} {"FAIL",-5} {"ERR",-24} NAME");
        foreach (var p in st.Providers.OrderBy(p => p.Priority))
        {
            var remain = p.RemainingPct is { } r ? r + "%" : "-";
            var err = p.LastError == null ? "" : p.LastError.Length > 22 ? p.LastError[..22] + "…" : p.LastError;
            var pin = st.Forced?.Id == p.Id ? "*" : "";
            Console.WriteLine($"  {p.Priority,-4} {p.Id + pin,-20} {p.State,-9} {remain,-7} {p.RequestCount,-6} {p.FailCount,-5} {err,-24} {p.Name}");
        }
        return ExitOk;
    }

    internal static int CmdHealth(string[] args, bool jsonOut)
    {
        var (status, body) = Request("GET", "health", timeoutMs: 2000);
        if (status == 0) return Unreachable(jsonOut);
        if (status != 200) { Err(jsonOut, $"HTTP {status}: {body}"); return ExitFail; }
        Console.WriteLine(jsonOut ? body : $"ok (127.0.0.1:{TargetPort()})");
        return ExitOk;
    }

    internal static int CmdLogs(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var tail = OptInt(args, "--tail") is { } t ? Math.Clamp(t, 1, 500) : 50;
        var result = AdminCall("GET", $"api/logs?tail={tail}", jsonOut: jsonOut);
        if (result == null) return ExitFail;
        if (jsonOut) { PrintJson(result.Value.Body); return ExitOk; }
        try
        {
            var doc = JsonDocument.Parse(result.Value.Body);
            var entries = doc.RootElement.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array ? e : default;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            {
                Console.WriteLine("logs: (空)");
                return ExitOk;
            }
            Console.WriteLine($"{"TIME",-19} {"MODEL",-20} {"PROVIDER",-16} {"CHAIN",-20} {"CODE",-5} {"MS",-6} ERROR");
            foreach (var en in entries.EnumerateArray())
            {
                var time = en.TryGetProperty("time", out var t2) && DateTimeOffset.TryParse(t2.GetString(), out var dt)
                    ? dt.ToLocalTime().ToString("MM-dd HH:mm:ss") : "?";
                var model = en.TryGetProperty("model", out var m) ? m.GetString() : "?";
                var prov = en.TryGetProperty("providerId", out var pv) ? pv.GetString() : "?";
                var chain = en.TryGetProperty("chain", out var ch) && ch.ValueKind == JsonValueKind.Array
                    ? string.Join("→", ch.EnumerateArray().Select(c => c.GetString())) : "";
                var code2 = en.TryGetProperty("statusCode", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32().ToString() : "-";
                var dur = en.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64().ToString() : "-";
                var err = en.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
                Console.WriteLine($"{time,-19} {model,-20} {prov,-16} {chain,-20} {code2,-5} {dur,-6} {err ?? ""}");
            }
            return ExitOk;
        }
        catch (Exception ex)
        {
            Err(jsonOut, "日志响应解析失败：" + ex.Message);
            return ExitFail;
        }
    }

    internal static int CmdDiscover(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var result = AdminCall("GET", "api/agents", jsonOut: jsonOut);
        if (result == null) return ExitFail;
        if (jsonOut) { PrintJson(result.Value.Body); return ExitOk; }
        try
        {
            var doc = JsonNode.Parse(result.Value.Body);
            var agents = doc?["agents"] as JsonArray;
            if (agents == null || agents.Count == 0) { Console.WriteLine("discover: (未发现任何渠道)"); return ExitOk; }
            foreach (var ag in agents)
            {
                Console.WriteLine($"agent: {ag?["agent"]}");
                var ps = ag?["providers"] as JsonArray;
                if (ps == null || ps.Count == 0) { Console.WriteLine("  (无)"); continue; }
                foreach (var p in ps)
                {
                    Console.WriteLine($"  - {p?["id"]} ({p?["name"]}) key={p?["apiKey"]}" +
                                      $" models=[{string.Join(", ", (p?["models"] as JsonArray ?? []).Select(m => m?.ToString()))}]" +
                                      $" probe={p?["suggestedProbeType"]} inPool={p?["inPool"]}");
                }
            }
            return ExitOk;
        }
        catch (Exception ex)
        {
            Err(jsonOut, "发现响应解析失败：" + ex.Message);
            return ExitFail;
        }
    }

    internal static int CmdDeploy(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var agent = Arg(args, 0, "<agent>（opencode / pi）");
        var dryRun = Has(args, "--dry-run");
        var setDefault = Has(args, "--set-default");
        var query = $"?setDefault={(setDefault ? "true" : "false")}&confirm={(dryRun ? "false" : "true")}";
        var result = AdminCall("POST", $"api/agents/{Uri.EscapeDataString(agent)}/deploy{query}", jsonOut: jsonOut, timeoutMs: 60000);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (jsonOut) { PrintJson(body); return status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(body);
            var ok = node?["ok"]?.GetValue<bool>() ?? status == 200;
            Console.WriteLine(node?["preview"] is { } pv && pv.ToString().Length > 0
                ? $"preview [{agent}]:\n{pv}"
                : (node?["message"]?.ToString() ?? (ok ? "OK" : "deploy 失败")));
            if (node?["backup"] is { } bk && bk.ToString().Length > 0) Console.WriteLine("backup: " + bk);
            return ok ? ExitOk : ExitFail;
        }
        catch
        {
            Console.WriteLine(body);
            return status == 200 ? ExitOk : ExitFail;
        }
    }

    // ─────────────────────────── 服务控制（relay / shutdown） ───────────────────────────

    internal static int CmdRelayOnOff(string[] args, bool jsonOut, bool enable)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var result = AdminCall("PUT", "api/settings", RelayJson.Serialize(new JsonObject { ["enabled"] = enable }), jsonOut);
        if (result == null) return ExitFail;
        if (jsonOut) { PrintJson(result.Value.Body); return result.Value.Status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(result.Value.Body);
            var ok = node?["ok"]?.GetValue<bool>() ?? false;
            Console.WriteLine((node?["message"]?.ToString() ?? (ok ? "OK" : "FAIL")) +
                              (enable ? "" : "\n注意：关闭请求已提交；实例进程仍在运行（UI 实例中转已停，headless 实例同样停机但不退出）。"));
            return ok ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(result.Value.Body); return result.Value.Status == 200 ? ExitOk : ExitFail; }
    }

    internal static int CmdRelayShow(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var result = AdminCall("GET", "api/settings", jsonOut: jsonOut);
        if (result == null) return ExitFail;
        if (jsonOut) { PrintJson(result.Value.Body); return ExitOk; }
        try
        {
            var o = JsonNode.Parse(result.Value.Body);
            Console.WriteLine($"port={o?["port"]} running={o?["running"]} enabled={o?["enabled"]} initialized={o?["initialized"]}");
            Console.WriteLine($"probeInterval={o?["probeIntervalSeconds"]}s degradeBelow={o?["degradeBelowPct"]}% restoreAbove={o?["restoreAbovePct"]}%");
            var cd = o?["cooldown"];
            if (cd is { } c) Console.WriteLine($"cooldown: quota={c["quotaSeconds"]}s error={c["errorThreshold"]}次/{c["errorSeconds"]}s");
            var sm = o?["stream"];
            if (sm is { } s) Console.WriteLine($"stream: firstChunk={s["firstChunkSeconds"]}s idle={s["idleSeconds"]}s");
            Console.WriteLine($"hasToken={o?["hasToken"]} authDisabled={o?["authDisabled"]}");
            Console.WriteLine($"adminUrl={o?["adminUrl"]} baseUrl={o?["baseUrl"]}");
            return ExitOk;
        }
        catch (Exception ex) { Err(jsonOut, "settings 响应解析失败：" + ex.Message); return ExitFail; }
    }

    internal static int CmdRelaySet(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var body = new JsonObject();
        AddIntOpt(body, args, "port", "--port-set"); // 说明：端口变更用 --port-set 避免与全局 --port 冲突
        AddIntOpt(body, args, "probeIntervalSeconds", "--probe-interval");
        AddIntOpt(body, args, "degradeBelowPct", "--degrade-below");
        AddIntOpt(body, args, "restoreAbovePct", "--restore-above");
        if (Opt(args, "--cooldown-quota") != null || Opt(args, "--cooldown-error-threshold") != null || Opt(args, "--cooldown-error-seconds") != null)
        {
            var cd = new JsonObject();
            AddIntOpt(cd, args, "quotaSeconds", "--cooldown-quota");
            AddIntOpt(cd, args, "errorThreshold", "--cooldown-error-threshold");
            AddIntOpt(cd, args, "errorSeconds", "--cooldown-error-seconds");
            body["cooldown"] = cd;
        }
        if (body.Count == 0) throw new CliUsageException("没有提供任何要修改的选项");
        var result = AdminCall("PUT", "api/settings", RelayJson.Serialize(body), jsonOut);
        if (result == null) return ExitFail;
        var (status, respBody) = result.Value;
        if (jsonOut) { PrintJson(respBody); return status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(respBody);
            var ok = node?["ok"]?.GetValue<bool>() ?? status == 200;
            var msg = node?["message"]?.ToString() ?? (ok ? "OK" : "FAIL");
            Console.WriteLine(msg);
            if (node?["restartRequired"]?.GetValue<bool>() == true) Console.WriteLine("注意：端口变更需重启程序生效。");
            foreach (var w in node?["warnings"] as JsonArray ?? []) Console.WriteLine("warn: " + w);
            return ok ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(respBody); return status == 200 ? ExitOk : ExitFail; }
    }

    static void AddIntOpt(JsonObject target, string[] args, string key, string optName)
    {
        if (OptInt(args, optName) is { } v) target[key] = v;
    }

    internal static int CmdRelayResetToken(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var result = AdminCall("PUT", "api/settings", RelayJson.Serialize(new JsonObject { ["resetToken"] = true }), jsonOut);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (jsonOut) { PrintJson(body); return status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(body);
            var ok = node?["ok"]?.GetValue<bool>() ?? status == 200;
            Console.WriteLine((node?["message"]?.ToString() ?? (ok ? "OK" : "FAIL")));
            if (node?["newToken"] is { } tk) Console.WriteLine("newToken: " + tk);
            Console.WriteLine("注意：opencode 端 localrelay provider 需同步重新部署（GlmBalanceTaskbar.exe deploy opencode），否则请求 401。");
            return ok ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(body); return status == 200 ? ExitOk : ExitFail; }
    }

    internal static int CmdShutdown(string[] args, bool jsonOut)
    {
        // /api/shutdown 服务端仅 headless 实例生效；UI 承载时 403 原样转述
        var (status, body) = Request("POST", "api/shutdown", "{}");
        if (status == 0) return Unreachable(jsonOut);
        if (jsonOut) { PrintJson(body.Length > 0 ? body : "{\"ok\":true}"); return status == 200 ? ExitOk : ExitFail; }
        switch (status)
        {
            case 200:
                Console.WriteLine("已请求停止：headless 进程将退出。");
                return ExitOk;
            case 403:
                Console.WriteLine("error: " + (TryExtractError(body) ?? "forbidden"));
                Console.WriteLine("说明：/api/shutdown 仅对 --headless 后台进程生效；中转由 UI（托盘）承载时请用 UI 退出或 relay off。");
                return ExitFail;
            case 401:
                Console.WriteLine("error: 401 unauthorized（localToken 不匹配；token 可能已在配置或实例侧轮换）");
                return ExitFail;
            default:
                Console.WriteLine($"error: HTTP {status}: {TryExtractError(body) ?? body}");
                return ExitFail;
        }
    }

    // ─────────────────────────── 渠道管理 ───────────────────────────

    internal static int CmdChannelList(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var result = AdminCall("GET", "api/providers", jsonOut: jsonOut);
        if (result == null) return ExitFail;
        if (jsonOut) { PrintJson(result.Value.Body); return ExitOk; }
        try
        {
            var list = (JsonNode.Parse(result.Value.Body)?["providers"] as JsonArray) ?? [];
            if (list.Count == 0) { Console.WriteLine("channels: (空)"); return ExitOk; }
            Console.WriteLine($"{"PRI",-4} {"ID",-20} {"ENABLED",-8} {"PROBE",-14} {"KEY",-14} {"MODELS",-40} NAME");
            foreach (var p in list.OrderBy(p => (int?)p?["priority"] ?? int.MaxValue))
            {
                var models = string.Join(",", (p?["models"] as JsonArray ?? []).Select(m => m?.ToString()));
                if (models.Length > 38) models = models[..38] + "…";
                Console.WriteLine(
                    $"{p?["priority"],-4} {p?["id"],-20} {p?["enabled"],-8} {p?["probe"],-14} {p?["apiKey"],-14} {models,-40} {p?["name"]}");
            }
            return ExitOk;
        }
        catch (Exception ex) { Err(jsonOut, "providers 响应解析失败：" + ex.Message); return ExitFail; }
    }

    internal static int CmdChannelShow(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var result = AdminCall("GET", "api/providers/" + Uri.EscapeDataString(id), jsonOut: jsonOut);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (status == 404) { Err(jsonOut, "渠道不存在：" + id); return ExitFail; }
        if (jsonOut) { PrintJson(body); return ExitOk; }
        try
        {
            var p = JsonNode.Parse(body);
            Console.WriteLine($"id       = {p?["id"]}");
            Console.WriteLine($"name     = {p?["name"]}");
            Console.WriteLine($"priority = {p?["priority"]}");
            Console.WriteLine($"probe    = {p?["probe"]}");
            Console.WriteLine($"baseURL  = {p?["baseURL"]}");
            Console.WriteLine($"apiKey   = {p?["apiKey"]}（hasKey={p?["hasKey"]}）");
            Console.WriteLine($"models   = [{string.Join(", ", (p?["models"] as JsonArray ?? []).Select(m => m?.ToString()))}]");
            Console.WriteLine($"enabled  = {p?["enabled"]}");
            if (p?["organization"] is { } org) Console.WriteLine($"organization = {org}");
            if (p?["project"] is { } proj) Console.WriteLine($"project  = {proj}");
            if (p?["usageType"] is { } ut) Console.WriteLine($"usageType = {ut}");
            return ExitOk;
        }
        catch (Exception ex) { Err(jsonOut, "渠道响应解析失败：" + ex.Message); return ExitFail; }
    }

    internal static int CmdChannelAddSet(string[] args, bool jsonOut, bool isAdd)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var body = new JsonObject();
        if (Opt(args, "--name") is { } n) body["name"] = n;
        var baseUrl = Opt(args, "--baseURL") ?? Opt(args, "--baseurl") ?? Opt(args, "--base-url");
        if (NonEmpty(baseUrl) != null) body["baseURL"] = baseUrl!.Trim();
        if (Opt(args, "--apiKey") is { } k) body["apiKey"] = k;
        if (Opt(args, "--models") is { } ms)
            body["models"] = new JsonArray(ms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        if (OptInt(args, "--priority") is { } pr) body["priority"] = pr;
        if (Opt(args, "--probe") is { } pb) body["probe"] = pb;
        // 探测要素：--organization/--project 传空串 = 清除（回落全局）——与 ApplyProviderPatch 语义一致
        if (Opt(args, "--organization") is { } org2) body["organization"] = org2;
        if (Opt(args, "--project") is { } proj2) body["project"] = proj2;
        if (Opt(args, "--usageType") is { } ut2)
        {
            if (!int.TryParse(ut2, out var utv) || (utv != 1 && utv != 2))
                throw new CliUsageException("--usageType 仅支持 1（个人）/ 2（团队）");
            body["usageType"] = utv;
        }
        if (OptBool(args, "--enabled") is { } en) body["enabled"] = en;

        if (isAdd && !body.ContainsKey("baseURL"))
        {
            Console.WriteLine("warn: 未提供 --baseURL，渠道将无上游地址（不参与路由）");
        }
        var result = AdminCall("PUT", "api/providers/" + Uri.EscapeDataString(id), RelayJson.Serialize(body), jsonOut, timeoutMs: 30000);
        if (result == null) return ExitFail;
        var (status, resp) = result.Value;
        if (status == 400) { Err(jsonOut, TryExtractError(resp) ?? resp); return ExitFail; }
        if (jsonOut) { PrintJson(resp); return status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(resp);
            var ok = node?["ok"]?.GetValue<bool>() ?? status == 200;
            Console.WriteLine(node?["message"]?.ToString() ?? (ok ? "OK" : "FAIL"));
            foreach (var w in node?["warnings"] as JsonArray ?? []) Console.WriteLine("warn: " + w);
            return ok ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(resp); return status == 200 ? ExitOk : ExitFail; }
    }

    internal static int CmdChannelRemove(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var result = AdminCall("DELETE", "api/providers/" + Uri.EscapeDataString(id), jsonOut: jsonOut);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (status == 404) { Err(jsonOut, "渠道不存在：" + id); return ExitFail; }
        if (jsonOut) { PrintJson(body); return status == 200 ? ExitOk : ExitFail; }
        Console.WriteLine(TryExtractMessage(body) ?? (status == 200 ? $"已删除 {id} 并生效" : "FAIL"));
        return status == 200 ? ExitOk : ExitFail;
    }

    internal static int CmdChannelEnableDisable(string[] args, bool jsonOut, bool enable)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var body = RelayJson.Serialize(new JsonObject { ["enabled"] = enable });
        var result = AdminCall("PUT", "api/providers/" + Uri.EscapeDataString(id), body, jsonOut);
        if (result == null) return ExitFail;
        var (status, resp) = result.Value;
        if (status == 400) { Err(jsonOut, TryExtractError(resp) ?? resp); return ExitFail; }
        if (jsonOut) { PrintJson(resp); return status == 200 ? ExitOk : ExitFail; }
        Console.WriteLine(TryExtractMessage(resp) ?? (status == 200 ? $"已{(enable ? "启用" : "停用")} {id} 并生效" : "FAIL"));
        return status == 200 ? ExitOk : ExitFail;
    }

    internal static int CmdChannelTest(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var result = AdminCall("POST", $"api/providers/{Uri.EscapeDataString(id)}/test", "{}", jsonOut, timeoutMs: 60000);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (jsonOut) { PrintJson(body); return ExitOk; }
        try
        {
            var node = JsonNode.Parse(body);
            var ok = node?["ok"]?.GetValue<bool>() ?? false;
            Console.WriteLine($"{id}: {(ok ? "OK" : "FAIL")} {node?["message"]?.ToString() ?? ""}");
            return ok ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(body); return ExitOk; }
    }

    internal static int CmdChannelMove(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var dir = Arg(args, 1, "up|down");
        if (!dir.Equals("up", StringComparison.OrdinalIgnoreCase) && !dir.Equals("down", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException($"方向只能是 up 或 down，得到 \"{dir}\"");
        var body = RelayJson.Serialize(new JsonObject { ["dir"] = dir.ToLowerInvariant() });
        var result = AdminCall("POST", $"api/providers/{Uri.EscapeDataString(id)}/move", body, jsonOut);
        if (result == null) return ExitFail;
        var (status, resp) = result.Value;
        if (status == 404) { Err(jsonOut, "渠道不存在：" + id); return ExitFail; }
        if (jsonOut) { PrintJson(resp); return status == 200 ? ExitOk : ExitFail; }
        Console.WriteLine(TryExtractMessage(resp) ?? (status == 200 ? "OK" : "FAIL"));
        return status == 200 ? ExitOk : ExitFail;
    }

    internal static int CmdChannelReorder(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var ids = args.Where(a => !a.StartsWith('-')).ToArray();
        if (ids.Length == 0) throw new CliUsageException("缺少渠道 id 列表（须恰好为全部渠道 id，按新顺序排列）");
        var body = RelayJson.Serialize(new JsonObject { ["ids"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) });
        var result = AdminCall("POST", "api/providers/reorder", body, jsonOut);
        if (result == null) return ExitFail;
        var (status, resp) = result.Value;
        if (status == 400) { Err(jsonOut, TryExtractError(resp) ?? resp); return ExitFail; }
        if (jsonOut) { PrintJson(resp); return status == 200 ? ExitOk : ExitFail; }
        Console.WriteLine(status == 200 ? "已重排优先级并生效" : "FAIL");
        return status == 200 ? ExitOk : ExitFail;
    }

    internal static int CmdChannelPin(string[] args, bool jsonOut, bool pin)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var id = Arg(args, 0, "<id>");
        var method = pin ? "POST" : "DELETE";
        var result = AdminCall(method, $"api/providers/{Uri.EscapeDataString(id)}/pin", pin ? "{}" : null, jsonOut);
        if (result == null) return ExitFail;
        var (status, body) = result.Value;
        if (status == 404) { Err(jsonOut, "渠道不存在：" + id); return ExitFail; }
        if (jsonOut) { PrintJson(body); return status == 200 ? ExitOk : ExitFail; }
        Console.WriteLine(status == 200
            ? (pin ? $"已设 {id} 为强制通道（运行时单槽，重启失效）" : $"已解除 {id} 的强制通道")
            : "FAIL");
        return status == 200 ? ExitOk : ExitFail;
    }

    internal static int CmdChannelImport(string[] args, bool jsonOut)
    {
        var code = RequireInstance(jsonOut);
        if (code != ExitOk) return code;
        var ids = args.Where(a => !a.StartsWith('-')).ToArray();
        if (ids.Length == 0)
        {
            // 无参 = 全部候选：从 /api/agents 取未入池渠道（格式 agent:id）
            var disc = AdminCall("GET", "api/agents", jsonOut: jsonOut);
            if (disc == null) return ExitFail;
            try
            {
                var agents = JsonNode.Parse(disc.Value.Body)?["agents"] as JsonArray ?? [];
                ids = agents.SelectMany(ag => (ag?["providers"] as JsonArray ?? [])
                        .Where(p => p?["inPool"]?.GetValue<bool>() != true)
                        .Select(p => $"{ag?["agent"]}:{p?["id"]}"))
                    .ToArray();
                if (ids.Length == 0)
                {
                    Console.WriteLine("没有可导入的新渠道（发现列表均已入池）");
                    return ExitOk;
                }
            }
            catch (Exception ex) { Err(jsonOut, "发现响应解析失败：" + ex.Message); return ExitFail; }
            Console.WriteLine($"候选渠道 {ids.Length} 个：{string.Join(", ", ids)}");
        }
        var body = RelayJson.Serialize(new JsonObject { ["ids"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) });
        var result = AdminCall("POST", "api/providers/import", body, jsonOut, timeoutMs: 60000);
        if (result == null) return ExitFail;
        var (status, resp) = result.Value;
        if (status == 400) { Err(jsonOut, TryExtractError(resp) ?? resp); return ExitFail; }
        if (jsonOut) { PrintJson(resp); return status == 200 ? ExitOk : ExitFail; }
        try
        {
            var node = JsonNode.Parse(resp);
            Console.WriteLine($"added: [{string.Join(", ", node?["added"] as JsonArray ?? [])}]");
            var skipped = (node?["skipped"] as JsonArray ?? []).Where(s => s?.ToString().Length > 0).ToList();
            if (skipped.Count > 0) Console.WriteLine("skipped: " + string.Join(", ", skipped));
            foreach (var w in node?["warnings"] as JsonArray ?? []) Console.WriteLine("warn: " + w);
            return status == 200 ? ExitOk : ExitFail;
        }
        catch { Console.WriteLine(resp); return status == 200 ? ExitOk : ExitFail; }
    }

    // ─────────────────────────── 开机任务 / 初始化 ───────────────────────────

    internal static int CmdTaskStatus(string[] args, bool jsonOut)
    {
        var exists = ScheduledTask.ExistsState();
        var existsLabel = exists switch
        {
            ScheduledTask.TaskExistsState.Yes => "已注册",
            ScheduledTask.TaskExistsState.No => "未注册",
            _ => "已注册（推断：查询受限，需管理员权限确认）",
        };
        // 后台服务探活（同 UI 委托态判定语义：/health 探测）
        var relay = LoadConfig().Relay;
        var port = _portOverride ?? relay?.Port ?? (DevMode.IsDev ? DevMode.DefaultRelayPort : DevMode.ProductionRelayPort);
        var headlessRunning = false;
        var headlessFlag = false;
        var (status, body) = Request("GET", "status", timeoutMs: 1500);
        if (status == 200 && RelayStatusJson.TryParse(body) is { } st)
        {
            headlessRunning = true;
            headlessFlag = st.Headless;
        }
        if (jsonOut)
        {
            Console.WriteLine(RelayJson.Serialize(new
            {
                taskName = ScheduledTask.TaskName,
                registered = exists != ScheduledTask.TaskExistsState.No,
                existsState = exists.ToString(),
                relayReachable = headlessRunning,
                headless = headlessFlag,
                runAtLogon = ScheduledTask.RunAtLogonEnabled(),
            }));
            return ExitOk;
        }
        Console.WriteLine($"task: {ScheduledTask.TaskName} — {existsLabel}");
        if (headlessRunning) Console.WriteLine($"relay: 后台服务运行中（port={port}{(headlessFlag ? ", headless" : "")}）");
        else Console.WriteLine("relay: 未探测到运行中的后台服务");
        Console.WriteLine($"runAtLogon: {(ScheduledTask.RunAtLogonEnabled() ? "已启用" : "未启用")}（登录时启动，仅界面）");
        if (exists == ScheduledTask.TaskExistsState.Locked)
            Console.WriteLine("提示：任务存在性查询受限（SYSTEM 任务默认拒普通用户查询）；如需确认请以管理员运行。");
        return ExitOk;
    }

    internal static int CmdTaskRegister(string[] args, bool jsonOut)
    {
        if (DevMode.IsDev)
        {
            Err(jsonOut, "--dev 与 task register 组合被禁止（dev 实例禁止注册系统任务）");
            return ExitFail;
        }
        var profile = _configDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!jsonOut) Console.WriteLine($"注册开机任务 {ScheduledTask.TaskName}（schtasks /SC ONSTART /RU SYSTEM，需要管理员权限）…");
        var ok = ScheduledTask.Register(profile, out var error);
        if (!ok)
        {
            Err(jsonOut, "注册失败：" + (error.Length > 0 ? error : "schtasks 返回非零退出码"));
            if (!jsonOut) Console.WriteLine("可复制以下命令到管理员终端手动执行：\n" + ScheduledTask.ManualRegisterCommand(profile));
            return ExitFail;
        }
        var runOk = ScheduledTask.RunTask();
        if (jsonOut)
        {
            Console.WriteLine(RelayJson.Serialize(new { ok = true, task = ScheduledTask.TaskName, started = runOk }));
        }
        else
        {
            Console.WriteLine(runOk
                ? $"已注册并立即启动 headless（任务名：{ScheduledTask.TaskName}）"
                : $"已注册，但立即启动未成功（将在下次开机生效；任务名：{ScheduledTask.TaskName}）");
        }
        return ExitOk;
    }

    internal static int CmdTaskUnregister(string[] args, bool jsonOut)
    {
        if (DevMode.IsDev)
        {
            Err(jsonOut, "--dev 与 task unregister 组合被禁止（dev 实例禁止注销系统任务）");
            return ExitFail;
        }
        var notes = new List<string>();
        if (!jsonOut) Console.WriteLine("停止运行中的任务实例…");
        ScheduledTask.EndTask(); // 失败不阻塞（任务可能未在运行）
        // /api/shutdown 兜底（同 ScheduledTask.RunUnregisterCommand 语义：手工启动的 headless 不受 /End 管辖）
        try
        {
            var relay = LoadConfig().Relay;
            if (relay is { } r)
            {
                var client = new DelegatedRelayClient(r.Port);
                if (client.ProbeOnce() != null)
                {
                    var stopped = client.RequestShutdown(r.LocalToken).GetAwaiter().GetResult();
                    notes.Add(stopped ? "已通过 /api/shutdown 停止运行中的后台进程。"
                                      : "后台进程停止请求未确认（token 可能不匹配）；若进程仍驻留，可在任务管理器手动结束。");
                }
            }
        }
        catch (Exception ex) { notes.Add("后台进程兜底停止失败：" + ex.Message); }

        if (!jsonOut) Console.WriteLine("删除计划任务…");
        var (code, output) = SchtasksDelete();
        var goneNote = "";
        if (code != 0)
        {
            var state = ScheduledTask.ExistsState();
            if (state == ScheduledTask.TaskExistsState.Yes)
            {
                Err(jsonOut, "注销失败：" + (output.Length > 0 ? output : "schtasks 返回非零退出码"));
                if (!jsonOut) Console.WriteLine($"可手动执行：schtasks /Delete /TN \"{ScheduledTask.TaskName}\" /F");
                return ExitFail;
            }
            if (state == ScheduledTask.TaskExistsState.No) goneNote = "任务不存在，视为未注册。";
            else notes.Add("任务状态查询受限（需管理员权限），无法确认删除结果；" +
                           $"若下次开机仍自启，请以管理员执行：schtasks /Delete /TN \"{ScheduledTask.TaskName}\" /F");
        }
        if (jsonOut)
        {
            Console.WriteLine(RelayJson.Serialize(new { ok = true, task = ScheduledTask.TaskName, notes }));
        }
        else
        {
            Console.WriteLine($"开机后台服务已注销：计划任务已删除，运行中的后台进程已停止。{goneNote}");
            foreach (var n in notes) Console.WriteLine("note: " + n);
        }
        return ExitOk;
    }

    /// <summary>schtasks /Delete /F（OEM 编码输出，与 ScheduledTask 内部一致的处理留待其公开；此处轻量复制，零行为分叉风险点仅在删除）。</summary>
    static (int Code, string Output) SchtasksDelete()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "schtasks", $"/Delete /TN \"{ScheduledTask.TaskName}\" /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (1, "schtasks 启动失败");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, (stderr.Trim().Length > 0 ? stderr : stdout).Trim());
        }
        catch (Exception ex) { return (1, ex.Message); }
    }

    internal static int CmdInit(string[] args, bool jsonOut)
    {
        var dir = Arg(args, 0, "<dir>（目标配置目录）");
        var code = Program.RunRelayInit(dir);
        return code == 0 ? ExitOk : ExitFail;
    }

    static string? TryExtractMessage(string body)
    {
        try { return JsonNode.Parse(body)?["message"]?.ToString(); }
        catch { return null; }
    }

    static string FormatUptime(double sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h{ts.Minutes}m" : $"{ts.Minutes}m{ts.Seconds}s";
    }
}
