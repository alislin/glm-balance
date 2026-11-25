using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GlmBalanceTaskbar;

/// <summary>
/// 应用上下文：轮询监控 API，驱动任务栏图标（动态文字图标 + 原生进度条 + 窗口标题 tooltip）与详情窗口。
/// </summary>
sealed class AppContext : ApplicationContext
{
    /// <summary>活跃度刷新间隔（毫秒），与插件 ACTIVITY_TTL 一致。</summary>
    const int ActivityTtlMs = 300_000;

    AppConfig _cfg; // 非 readonly：S5 回退重载配置后同步更新（A-F1，后续探活/SetWarnings 用新端口与新快照）
    readonly GlmApiClient _client;
    readonly DetailForm _form;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    RelayManager _relay; // 非 readonly：S5 回退时以重载配置重建（R1-F8）
    TrayPreviewForm? _trayPreview;
    UsageSnapshot? _lastSnap;
    Icon? _icon;
    int _busy;
    DateTimeOffset _lastActivityFetch = DateTimeOffset.MinValue;
    ActivityInfo? _activity;

    // ── 委托态（G3/§2.4）：探测到既有 relay 实例（headless/另一 UI）时只读渲染、零写盘 ──
    DelegatedRelayClient? _delegated;      // null = 非委托态
    RelayStatus? _delegatedStatus;         // 最近一次远端 /status 快照
    int _delegatedFailures;                // 连续探活失败计数（N 次后 S5 回退）
    System.Threading.Timer? _trimTimer;    // M7：工作集定时裁剪（改善观感）

    /// <summary>委托探活连续失败回退阈值（S5：30s 节奏 × 3 ≈ 90s 宽限，覆盖 headless 重启窗口期）。</summary>
    const int DelegatedFailureLimit = 3;

    public AppContext(AppConfig cfg)
    {
        _cfg = cfg;
        _client = new GlmApiClient(cfg);
        _form = new DetailForm();
        MainForm = _form;
        // dev 双实例消歧：窗口标题（托盘 tooltip）加 [DEV] 前缀（AUMID 已分离，任务栏独立身份）
        if (DevMode.IsDev) _form.Text = "[DEV] " + _form.Text;
        _form.RefreshRequested += (_, _) => _ = ManualRefreshAsync();
        _form.PanelRequested += (_, _) => OpenAdminPanel();
        _form.RelayToggleRequested += (_, _) => _ = ToggleRelayAsync();
        // sticky pin 强制通道：托盘右键菜单外派 → 路由器置/清 pin → 立即刷新详情窗中转分组
        // （委托态被 DetailForm.ShowRelayRowMenu 拦截，此处防御双保险）
        _form.PinRequested += (_, e) =>
        {
            var relay = _relay;
            if (relay == null || _delegated != null) return;
            if (e.Pin) relay.Router.SetForced(e.ProviderId);
            else relay.Router.ClearForced(e.ProviderId);
            _form.UpdateRelay(relay.Snapshot());
        };
        // 详情窗 Tab 切换（用户手动选择用量视图；AppContext 只记 id 并重渲，零请求）
        _form.ProviderViewSwitched += (_, id) => SelectProviderView(id);

        // 自启动分组（G2/§2.5）：注册/注销（提权子命令流，S1）与 HKCU Run 副开关接线
        _form.AutostartToggleRequested += (_, _) => _ = ToggleAutostartAsync();
        _form.RunAtLogonChangeRequested += (_, on) => SetRunAtLogon(on);

        // 中转：始终构造管理器（未初始化/显式关闭时开关与面板入口仍可用，消除静默无反应）；
        // 仅 enabled=true 时启动；启动失败只记 warnings，配额显示不受影响。
        // 未初始化（无 relay 段）用内存默认占位，等待用户点击「启用中转」才写回配置。
        var relayOpts = cfg.Relay ?? RelayOptions.CreateDefault();
        // M1：dev 下缺省（cfg.Relay == null 的从零启用路径）端口合成——CreateDefault 产物
        // PortExplicit=false 且 Port=8787，命中 8787→8788；否则从零启用必被 Start() 硬禁卡死。
        if (DevMode.IsDev)
        {
            DevMode.ApplyDevRelayDefaults(relayOpts, cfg.Warnings);
        }

        // 委托探活（§2.4）：relay 启用时探测既有实例（400ms 超时，避免拖慢启动）；
        // 委托态下 UI 不自起 relay、SetEnabled 不可达（开关禁用）→ 全程零写盘（单写者原则）。
        DelegatedRelayClient? delegated = null;
        RelayStatus? delegatedStatus = null;
        if (cfg.Relay is { Enabled: true } probeTarget)
        {
            delegatedStatus = new DelegatedRelayClient(probeTarget.Port).ProbeOnce();
            if (delegatedStatus is { Running: true })
            {
                delegated = new DelegatedRelayClient(probeTarget.Port);
                _delegated = delegated;
                _delegatedStatus = delegatedStatus;
            }
        }
        var delegatedMode = delegated != null;

        var relay = new RelayManager(cfg, relayOpts, initialized: cfg.Relay != null);
        if (cfg.Relay is { Enabled: true } startTarget && !delegatedMode)
        {
            relay.Start(cfg.Warnings); // 失败原因写入 cfg.Warnings（详情窗可见）；实例保留供状态展示与 Dispose
            // S4 启动竞态：本地 Start 失败（headless 或其他实例恰在启动窗口期）→ 补探活一次，
            // 成功即切委托态（期间「端口占用」警告保留可见，不静默；下一刷新周期收敛，A15）
            if (!relay.Running)
            {
                var retry = new DelegatedRelayClient(startTarget.Port).ProbeOnce();
                if (retry is { Running: true })
                {
                    delegated = new DelegatedRelayClient(startTarget.Port);
                    _delegated = delegated;
                    _delegatedStatus = retry;
                    cfg.Warnings.Add("本地中转启动失败：已由运行中的后台服务/其他实例承载（委托态，只读）。");
                }
            }
        }
        _relay = relay;
        AttachProbeListener(relay); // 探测产物落地 → 重渲（构造与 S5 回退重建后各挂一次）
        _form.SetWarnings(cfg.Warnings); // M4：启动期警告（含 relay 启动失败）在详情窗可见

        // 委托态 UI：禁用 relay 开关与 pin 菜单 + 提示文案（须在 UpdateRelay 首刷前设置）
        if (_delegated != null)
        {
            _form.SetDelegated(true, DelegatedHint());
        }

        // 系统托盘：动态数字图标（不设置 Text，避免与自绘多行摘要浮窗重叠出现两个 tooltip）
        _tray = new NotifyIcon
        {
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开详情", null, (_, _) => ShowMainWindow());
        menu.Items.Add("立即刷新", null, (_, _) => _ = ManualRefreshAsync());
        menu.Items.Add("管理面板", null, (_, _) => OpenAdminPanel());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _form.RequestExit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMainWindow();
        };
        // 悬停托盘图标（MouseMove）：弹出多行摘要浮窗（含活跃度）
        _tray.MouseMove += (_, _) => ShowTrayPreview();

        // 初始占位：加载中
        _form.Text = DevMode.IsDev ? "[DEV] GLM 用量 · 加载中…" : "GLM 用量 · 加载中…";
        SetIcon(IconRenderer.RenderTextIcon("…", Color.Gray));

        // 最小化显示：任务栏按钮出现且不弹窗
        _form.WindowState = FormWindowState.Minimized;
        _form.Show();

        _timer = new System.Windows.Forms.Timer { Interval = cfg.IntervalSeconds * 1000 };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync(force: true);
        _timer.Start();

        // M7：UI 最小化时裁剪 + 每 10 分钟定时裁剪（改善观感、非真实省内存）
        _form.Resize += (_, _) =>
        {
            if (_form.WindowState == FormWindowState.Minimized) WorkingSetTrimmer.Trim();
        };
        _trimTimer = new System.Threading.Timer(
            _ => WorkingSetTrimmer.Trim(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

        // 自启动注册态刷新（§2.5：详情窗打开时；schtasks /Query 子进程数十 ms，不做常驻轮询）
        RefreshAutostartState();
    }

    /// <summary>单实例二次启动时恢复窗口。</summary>
    public void ShowMainWindow()
    {
        if (_form.IsDisposed) return;
        try
        {
            _form.BeginInvoke(() =>
            {
                _form.WindowState = FormWindowState.Normal;
                _form.Activate();
                RefreshAutostartState(); // §2.5：打开详情前刷新注册态缓存
            });
        }
        catch
        {
            /* ignore */
        }
    }

    // ─────────────────────────── 自启动分组（G2/§2.2/§2.5/§5 S1） ───────────────────────────

    /// <summary>主开关：未注册 → 注册流；已注册/查询被拒 → 注销流（提权子命令，UAC）。
    /// Locked（查询被拒）走注销流：误判时注销流幂等兜底——/Delete 报「找不到」按注销成功，
    /// 刷新后恢复未注册态。</summary>
    async Task ToggleAutostartAsync()
    {
        if (DevMode.IsDev) return; // dev 硬禁（区块不创建，防御双保险，R5）
        if (Interlocked.CompareExchange(ref _relayBusy, 1, 0) != 0) return; // 与 relay 开关共用重入保护
        try
        {
            var state = ScheduledTask.ExistsState(); // 三态：No=注册流；Yes/Locked=注销流（Locked 宁可信其存在）
            if (state != ScheduledTask.TaskExistsState.No) await UnregisterAutostartAsync().ConfigureAwait(true);
            else await RegisterAutostartAsync().ConfigureAwait(true);
            RefreshAutostartState(); // §2.5：注册/注销后刷新注册态
        }
        finally
        {
            Interlocked.Exchange(ref _relayBusy, 0);
        }
    }

    /// <summary>
    /// 注册流（S1，R2-F2 时序）：确认框 → 本地 relay 让位（仅 Stop 停运行时，严禁
    /// SetEnabled(false) 等任何写盘路径——否则 headless 读到 enabled=false 命中 E3 直接 exit 0）
    /// → runas 自体子命令（UAC）→ ≤10s 探活为唯一成败判据（/Run 在任务引擎视角进程拉起即
    /// 成功，headless 自行退出无失败信号）：成功 → 委托态；失败 → 本地 Start 回滚 + 弹窗。
    /// UAC 取消分支同样回滚，避免静默断转发。
    /// </summary>
    async Task RegisterAutostartAsync()
    {
        var choice = MessageBox.Show(_form,
            "将在任务计划程序注册开机后台服务（无需登录即可中转）。\n\n" +
            "· 需要在 UAC 弹窗中确认管理员权限\n" +
            "· 本地中转将短暂让位给后台服务\n\n继续注册？",
            "注册开机服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (choice != DialogResult.Yes) return;

        // 步骤 0：本地 relay 让位（仅停运行时，不写盘）
        var wasLocalRunning = _relay is { Running: true };
        if (wasLocalRunning)
        {
            try { _relay.Stop(); } catch { /* ignore */ }
        }

        // 步骤 1–2：自体提权子命令（同一 exe --task-register；管理员上下文内 /Create → /Run）。
        // --config-dir 由本 UI 进程显式传参（A-F5：over-the-shoulder UAC 下提权子进程的
        // UserProfile ≠ 交互用户 profile，home 推导必须收敛到 UI 进程，「谁推导谁传参」）。
        var elevateError = (string?)null;
        try
        {
            var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                Verb = "runas",
                UseShellExecute = true,
                Arguments = "--task-register --config-dir \"" + profile + "\"",
            });
            p?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC 取消：零残留 + 本地 relay 回滚（防静默断转发）→ 回退提示
            if (wasLocalRunning) RollbackLocalRelay();
            ShowRegisterFallbackDialog("已取消授权，未创建任何任务。",
                offerLogon: true);
            return;
        }
        catch (Exception ex)
        {
            elevateError = ex.Message;
        }
        if (elevateError != null)
        {
            // 提权失败（非管理员组成员等）：回滚 + 双回退
            if (wasLocalRunning) RollbackLocalRelay();
            ShowRegisterFallbackDialog("无法启动提权子进程：" + elevateError, offerLogon: true);
            return;
        }

        // 步骤 3：探活为唯一成败判据（≤10s）
        if (await WaitHeadlessHealthyAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true))
        {
            MessageBox.Show(_form,
                "已注册：后台服务已启动，中转改由其承载。\n\n本窗口此后只读展示中转状态；" +
                "启停请用管理面板或注销自启动。",
                "注册开机服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            RollbackLocalRelay();
            MessageBox.Show(_form,
                "后台服务未启动，将在下次开机生效，期间由本程序承载。",
                "注册开机服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>注销流：runas --task-unregister（子进程 /End → /api/shutdown 兜底 → /Delete /F 并弹窗）。
    /// 注销后 headless 失联 → 既有 S5 通用回退在下一刷新周期恢复本地承载，此处无需特判。</summary>
    async Task UnregisterAutostartAsync()
    {
        var choice = MessageBox.Show(_form,
            "注销开机后台服务？\n\n· 运行中的后台中转将被停止\n· 下次开机不再自动运行中转\n\n继续注销？",
            "注销开机服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (choice != DialogResult.Yes) return;
        try
        {
            var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                Verb = "runas",
                UseShellExecute = true,
                Arguments = "--task-unregister",
            });
            p?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(_form, "已取消授权，未做任何更改。",
                "注销开机服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, "注销失败：" + ex.Message,
                "注销开机服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        MessageBox.Show(_form,
            "已注销：计划任务已删除，运行中的后台进程已停止。\n\n" +
            "中转将由本程序自动重新承载（约 1–2 分钟内生效），或立即在详情窗点击「启用中转」。",
            "注销开机服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>注册失败回滚：本地 Start 恢复承载（enabled 语义未动，零写盘）。
    /// 回滚路径必须同步清理委托态（C-F1）：否则本地 relay 已由本程序承载、UI 仍只读渲染陈旧
    /// 远端快照，下一周期回退还会级联「自我委托」——探活命中自己刚启动的 relay，只读自锁。</summary>
    void RollbackLocalRelay()
    {
        // 清委托态（幂等）：委托对象/快照/失败计数全部复位，开关与 pin 菜单恢复可用
        if (_delegated != null)
        {
            _delegated = null;
            _delegatedStatus = null;
            _delegatedFailures = 0;
            _form.SetDelegated(false, null);
        }
        try
        {
            var warnings = new List<string>();
            _relay?.Start(warnings);
            if (warnings.Count > 0)
            {
                _form.SetWarnings(_cfg.Warnings.Concat(warnings).ToList());
            }
            _form.UpdateRelay(_relay?.Snapshot());
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>注册回退对话框（§2.2 双回退）：降级注册「登录时启动」（HKCU Run，零提权）或手动指引。</summary>
    void ShowRegisterFallbackDialog(string reason, bool offerLogon)
    {
        var buttons = offerLogon ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OK;
        var text = reason +
            "\n\n开机后台服务未能完成注册。" +
            (offerLogon
                ? "\n\n「是」= 改注册登录时启动（无需管理员，登录后由本程序承载中转）\n" +
                  "「否」= 查看手动注册命令\n「取消」= 什么都不做"
                : "");
        var choice = MessageBox.Show(_form, text, "注册开机服务", buttons,
            offerLogon ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        if (offerLogon && choice == DialogResult.Yes)
        {
            if (ScheduledTask.SetRunAtLogon(true))
            {
                RefreshAutostartState();
            }
            else
            {
                MessageBox.Show(_form, "登录时启动设置失败（注册表写入被拒绝）。",
                    "登录时启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else if (offerLogon && choice == DialogResult.No)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            MessageBox.Show(_form,
                "在管理员终端执行以下命令完成注册：\n\n" + ScheduledTask.ManualRegisterCommand(profile),
                "手动注册", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>HKCU Run 副开关切换（零提权）；程序化设置触发的同值变更跳过（防回环）。</summary>
    void SetRunAtLogon(bool enable)
    {
        if (DevMode.IsDev) return;
        var current = ScheduledTask.RunAtLogonEnabled();
        if (enable == current) return;
        if (!ScheduledTask.SetRunAtLogon(enable))
        {
            MessageBox.Show(_form, "登录时启动设置失败（注册表写入被拒绝）。",
                "登录时启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshAutostartState();
    }

    /// <summary>自启动注册态刷新（§2.5：schtasks /Query 三态判定；「运行中」态复用委托探活缓存）。</summary>
    void RefreshAutostartState()
    {
        if (DevMode.IsDev) return;
        var state = ScheduledTask.ExistsState();
        var registered = state != ScheduledTask.TaskExistsState.No; // Locked（查询被拒）按「已注册」呈现
        _form.UpdateAutostart(registered, AutostartStateText(state), ScheduledTask.RunAtLogonEnabled());
    }

    /// <summary>状态行（§2.5）：No=未注册；Yes/Locked=已注册，「后台服务运行中」= 委托探活成功且
    /// /status 的 Headless=true（探活可能命中 UI 自身本地 relay，Headless 字段在本判定中为必用，
    /// R2-F2）。Locked 在行尾注明状态查询需管理员权限。</summary>
    string AutostartStateText(ScheduledTask.TaskExistsState state)
    {
        if (state == ScheduledTask.TaskExistsState.No) return "未注册";
        string text;
        if (_delegated != null && _delegatedStatus is { Running: true, Headless: true })
        {
            text = "已注册 · 后台服务运行中（委托）";
        }
        else if (_relay is { Running: true })
        {
            text = "已注册 · 由本程序承载（后台服务未运行，下次开机交接）";
        }
        else
        {
            text = "已注册 · 后台服务未运行";
        }
        return state == ScheduledTask.TaskExistsState.Locked ? text + "（状态查询需管理员权限）" : text;
    }

    /// <summary>开关重入保护（DetailForm 开关 / --auto-init-relay 钩子共用）。</summary>
    int _relayBusy;

    /// <summary>
    /// 「启用/关闭中转」开关（与 admin settings 的 enabled 开关同源：RelayManager.SetEnabled）。
    /// 关闭前弹确认（opencode 默认模型 localrelay 将不可用）；完成后立即刷新详情窗中转分组与警告行。
    /// </summary>
    async Task ToggleRelayAsync()
    {
        var relay = _relay;
        if (relay == null || _delegated != null) return; // 委托态开关禁用（防御：零写盘，§2.4）
        if (Interlocked.CompareExchange(ref _relayBusy, 1, 0) != 0) return;
        try
        {
            var enable = !relay.Running;
            if (!enable)
            {
                var choice = MessageBox.Show(_form,
                    "关闭后 opencode 默认模型 localrelay 将不可用。\n\n确认关闭中转？",
                    "关闭中转", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice != DialogResult.Yes) return;
            }
            var warnings = new List<string>();
            await Task.Run(() => relay.SetEnabled(enable, warnings)).ConfigureAwait(true);
            _form.SetWarnings(_cfg.Warnings.Concat(warnings).ToList());
            _form.UpdateRelay(relay.Snapshot());
            // 非委托态在 relay 操作失败（本地 Start 端口被占等）后补探活一次，成功即切委托态（§2.4，S4）
            if (!relay.Running && enable) ProbeDelegatedAfterStartFailure(relay.Options.Port);
        }
        finally
        {
            Interlocked.Exchange(ref _relayBusy, 0);
        }
    }

    async Task RefreshAsync(bool force = false)
    {        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            // 委托态：随既有 30s 刷新节流一并重取 /status（§2.4；连续失败 N 次回退，S5）
            if (_delegated != null)
            {
                await RefreshDelegatedStatusAsync().ConfigureAwait(true);
            }

            // 数据面分流：有探测产物 → 复用（探测是唯一数据面，零独立请求）；
            // 无探测渠道（或 relay 未运行）→ 全局链单视图（现状）。
            // 委托态：远端 /status 只有 pct → Tab 降级为仅百分比（明细/活跃度不可得）。
            var localSnaps = _relay is { Running: true } r ? r.ProviderSnaps() : null;
            var delegatedProbes = _delegatedStatus is { Running: true } st
                ? st.Providers.Where(p => p.Enabled && IsZhipuProbe(p.Probe)).OrderBy(p => p.Priority).ToList()
                : null;

            if (localSnaps is { Count: > 0 })
            {
                var defaultId = DefaultProbeViewId(localSnaps, _relay!.Router.Current(DateTimeOffset.Now)?.Id);
                var viewId = ResolveViewId(localSnaps, defaultId);
                var view = localSnaps.First(x => x.Id == viewId);
                var tabs = TabList(localSnaps.Select(x => (x.Id, x.Snap)).ToList(), _relay.Router.Current(DateTimeOffset.Now)?.Id);
                ApplyProbeMode(tabs, viewId, view.Snap, defaultId is null ? null : localSnaps.FirstOrDefault(x => x.Id == defaultId).Snap, pctOnly: false);
                return;
            }
            if (delegatedProbes is { Count: > 0 })
            {
                var snaps = delegatedProbes.Select(p => (p.Id, PctSnapshot(p))).ToList();
                var defaultId = DefaultProbeViewIdDelegated(snaps, _delegatedStatus!.CurrentId);
                var viewId = ResolveViewIdDelegated(snaps, defaultId);
                var view = snaps.First(x => x.Id == viewId);
                var tabs = TabList(snaps, _delegatedStatus!.CurrentId);
                ApplyProbeMode(tabs, viewId, view.Item2, defaultId is null ? null : snaps.FirstOrDefault(x => x.Id == defaultId).Item2, pctOnly: true);
                return;
            }

            // 无探测渠道：活跃度独立节流（失败时立即重试，与插件一致）
            if (_cfg.Activity && (force || DateTimeOffset.Now - _lastActivityFetch >= TimeSpan.FromMilliseconds(ActivityTtlMs)))
            {
                _lastActivityFetch = DateTimeOffset.Now;
                var act = await _client.FetchActivityAsync().ConfigureAwait(true);
                if (act != null) _activity = act;
                else _lastActivityFetch = DateTimeOffset.MinValue;
            }

            var fallbackSnap = await _client.FetchUsageAsync().ConfigureAwait(true);
            fallbackSnap.Activity = _activity;
            Apply(fallbackSnap);
        }
        catch (Exception ex)
        {
            Apply(new UsageSnapshot { Error = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ─────────────────── 多渠道用量视图（复用探测产物） ───────────────────

    /// <summary>用户手动选择的 Tab（provider id）；null = 未手动选择（跟随激活通道）。</summary>
    string? _userViewId;

    static bool IsZhipuProbe(string? probe) =>
        string.Equals(probe?.Trim(), "zhipu-monitor", StringComparison.OrdinalIgnoreCase);

    /// <summary>委托态 pct-only 快照：远端 /status 只有剩余百分比，无明细/重置时间/活跃度。</summary>
    static UsageSnapshot PctSnapshot(ProviderStatus p)
    {
        if (p.LastError is { } err && p.FiveHourPct == null && p.WeekPct == null)
        {
            return new UsageSnapshot { Error = err };
        }
        var s = new UsageSnapshot
        {
            FetchedAt = p.LastProbeAt ?? DateTimeOffset.Now,
            FiveHour = p.FiveHourPct is int fh ? new LimitInfo("5h", fh, null, null, null) : null,
            Week = p.WeekPct is int wk ? new LimitInfo(p.SourceLabel ?? "周积分", wk, null, null, null) : null,
        };
        return s;
    }

    /// <summary>Tab 集合：探测渠道按优先级排序；当前激活通道带 ▶ 标记（Tab 文本里体现）。</summary>
    List<(string Id, string Label)> TabList(IReadOnlyList<(string Id, UsageSnapshot Snap)> snaps, string? currentId)
    {
        var order = _relay?.Options.Providers.ToDictionary(p => p.Id, p => p.Priority);
        return snaps
            .OrderBy(x => order?.GetValueOrDefault(x.Id) ?? int.MaxValue)
            .Select(x => (x.Id, x.Id == currentId ? "▶ " + x.Id : x.Id))
            .ToList();
    }

    string? ResolveViewId(IReadOnlyList<(string Id, UsageSnapshot Snap, ActivityInfo? Activity)> snaps, string? defaultId) =>
        _userViewId is { } uid && snaps.Any(x => x.Id == uid) ? uid : defaultId;

    string? ResolveViewIdDelegated(IReadOnlyList<(string Id, UsageSnapshot Snap)> snaps, string? defaultId) =>
        _userViewId is { } uid && snaps.Any(x => x.Id == uid) ? uid : defaultId;

    /// <summary>默认视图（本地）：relay 当前激活通道是探测渠道 → 它；否则最高优先级探测渠道。</summary>
    string? DefaultProbeViewId(IReadOnlyList<(string Id, UsageSnapshot Snap, ActivityInfo? Activity)> snaps, string? currentId)
    {
        if (currentId != null && snaps.Any(x => x.Id == currentId)) return currentId;
        var order = _relay?.Options.Providers.ToDictionary(p => p.Id, p => p.Priority);
        return snaps
            .OrderBy(x => order?.GetValueOrDefault(x.Id) ?? int.MaxValue)
            .Select(x => (string?)x.Id)
            .FirstOrDefault();
    }

    /// <summary>默认视图（委托态）：当前激活通道是探测渠道 → 它；否则远端优先级最高的探测渠道。</summary>
    string? DefaultProbeViewIdDelegated(IReadOnlyList<(string Id, UsageSnapshot Snap)> snaps, string? currentId)
    {
        if (currentId != null && snaps.Any(x => x.Id == currentId)) return currentId;
        var order = _delegatedStatus?.Providers.ToDictionary(p => p.Id, p => p.Priority);
        return snaps
            .OrderBy(x => order?.GetValueOrDefault(x.Id) ?? int.MaxValue)
            .Select(x => (string?)x.Id)
            .FirstOrDefault();
    }

    /// <summary>探测产物模式渲染：面板显示用户所选视图；标题栏/托盘/图标固定跟随默认视图（激活通道）。</summary>
    void ApplyProbeMode(
        IReadOnlyList<(string Id, string Label)> tabs,
        string? viewId,
        UsageSnapshot viewSnap,
        UsageSnapshot? defaultSnap,
        bool pctOnly)
    {
        try
        {
            _lastSnap = defaultSnap ?? viewSnap; // 托盘浮窗数据源 = 默认视图
            _form.UpdateProviderTabs(tabs, viewId, pctOnly);
            _form.UpdateData(viewSnap);
            if (defaultSnap != null) RenderTitleIcon(defaultSnap);
            _form.UpdateRelay(_delegated != null ? _delegatedStatus : _relay?.Snapshot());
        }
        catch (Exception ex)
        {
            try { _form.Text = "GLM: " + ex.Message; } catch { /* ignore */ }
        }
    }

    /// <summary>详情窗 Tab 切换（用户手动选择；再次点击同 Tab = 恢复跟随激活通道）。</summary>
    public void SelectProviderView(string? providerId)
    {
        _userViewId = _userViewId == providerId ? null : providerId;
        _ = RefreshAsync();
    }

    /// <summary>挂接探测产物监听（构造与 S5 回退重建 relay 后调用；旧实例事件随 GC 失效）。</summary>
    void AttachProbeListener(RelayManager relay)
    {
        relay.ProbeDataChanged -= OnProbeDataChanged;
        relay.ProbeDataChanged += OnProbeDataChanged;
    }

    void OnProbeDataChanged(object? sender, string providerId)
    {
        try
        {
            if (_form.IsDisposed) return;
            _form.BeginInvoke(() => _ = RefreshAsync());
        }
        catch
        {
            /* 窗口句柄回收中：下一次 30s 轮询兜底 */
        }
    }

    /// <summary>手动刷新：委托态重拉 /status；本地探测模式立即重探全部探测渠道；否则主循环强制刷新。</summary>
    public async Task ManualRefreshAsync()
    {
        if (_delegated != null)
        {
            await RefreshAsync(force: true).ConfigureAwait(true);
            return;
        }
        if (_relay is { Running: true } relay && relay.ProviderSnaps().Count > 0)
        {
            await relay.ProbeNowAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return;
        }
        await RefreshAsync(force: true).ConfigureAwait(true);
    }

    // ─────────────────────────── 委托态（G3/§2.4/§5 S1·S4·S5） ───────────────────────────

    /// <summary>委托态 30s 节流刷新：拉取远端 /status；连续失败 N 次回退本地承载（S5）。
    /// 失败窗口内更新 hint 提示失联（C-F2/A-F8：保持上次快照 + 状态行提示的 S5 前半句落地），
    /// 成功即恢复常规委托文案。</summary>
    async Task RefreshDelegatedStatusAsync()
    {
        var st = await _delegated!.FetchStatusAsync().ConfigureAwait(true);
        if (st is { Running: true })
        {
            _delegatedStatus = st;
            _delegatedFailures = 0;
            _form.SetDelegated(true, DelegatedHint()); // 失联提示复位为常规委托文案
            return;
        }
        _delegatedFailures++;
        if (_delegatedFailures >= DelegatedFailureLimit)
        {
            FallBackToLocal();
            return;
        }
        // 一次性失败：保持上次快照 + hint 追加失联提示（下一周期自愈则自动复位）
        _form.SetDelegated(true,
            DelegatedHint() + $"（后台服务暂未响应，第 {_delegatedFailures}/{DelegatedFailureLimit} 次）");
    }

    /// <summary>
    /// S5 回退：委托对象连续失联后回退「未委托」。先 AppConfig.Load 重载并以新配置重建
    /// RelayManager 再本地 Start（R1-F8：委托期间 admin 面板对 providers/port/参数的变更不在
    /// 构造期 _opts 中，不重载会用过时池启动；UI 委托态全程零写盘，重载零副作用）。
    /// 同步更新 _cfg（A-F1：后续探活/SetWarnings 以新配置为准，否则读到旧端口旧快照）。
    /// Start 失败（headless 恰在重启，或 admin 已把 relay.port 迁移且 headless 在新端口承载，
    /// A-F2）→ S4 补探活**新配置端口**，命中即重新切委托态（端口迁移自愈，不滞留）。
    /// </summary>
    void FallBackToLocal()
    {
        _delegated = null;
        _delegatedStatus = null;
        _delegatedFailures = 0;
        _form.SetDelegated(false, null);
        try
        {
            var fresh = AppConfig.Load(DevMode.IsDev ? DevMode.ConfigRoot : null);
            if (DevMode.IsDev) DevMode.ApplyMockOverrides(fresh);
            var opts = fresh.Relay ?? RelayOptions.CreateDefault();
            if (DevMode.IsDev) DevMode.ApplyDevRelayDefaults(opts, fresh.Warnings);
            var relay = new RelayManager(fresh, opts, initialized: fresh.Relay != null);
            var warnings = new List<string>();
            if (fresh.Relay is { Enabled: true })
            {
                relay.Start(warnings);
                if (!relay.Running) ProbeDelegatedAfterStartFailure(relay.Options.Port); // A-F2：新端口重委托
            }
            var old = _relay;
            _relay = relay;
            AttachProbeListener(relay); // 重建后重挂探测产物监听
            _cfg = fresh; // A-F1：回退后全局同步新配置（旧引用仅 _client 持有，轮询语义不变）
            old?.Dispose();
            if (warnings.Count > 0)
            {
                _form.SetWarnings(fresh.Warnings.Concat(warnings).ToList());
            }
        }
        catch
        {
            /* 重载失败：保持未委托现状，下一刷新周期重试 */
        }
        RefreshAutostartState();
    }

    /// <summary>
    /// S4 补探活：relay 本地启动失败后探测一次既有实例，成功即切委托态（§2.4）。
    /// 端口由调用方显式传入（A-F1：此刻 _relay/_cfg 可能仍是旧值，读字段会探错目标）。
    /// 自体防护（C-F1②）：本进程 relay 仍在 Running 时绝不委托——此时探活命中的必是
    /// 自己的 relay，切委托态会导致 UI 只读自锁、无法经 UI 停止中转。
    /// </summary>
    void ProbeDelegatedAfterStartFailure(int port)
    {
        if (_relay is { Running: true }) return; // C-F1②：探活目标=自身时禁止自我委托
        var st = new DelegatedRelayClient(port).ProbeOnce();
        if (st is not { Running: true }) return;
        _delegated = new DelegatedRelayClient(port);
        _delegatedStatus = st;
        _delegatedFailures = 0;
        _form.SetDelegated(true, DelegatedHint());
        _form.UpdateRelay(st);
    }

    /// <summary>委托态提示文案（Headless 字段区分「开机后台服务」vs「另一实例」，§2.4 指纹策略）。</summary>
    string DelegatedHint() =>
        _delegatedStatus is { Headless: true }
            ? "中转由开机后台服务运行（只读）；启停请用管理面板或注销自启动。"
            : "中转由另一运行实例承载（只读）；启停请用管理面板。";

    /// <summary>注册流：/Run 拉起后 ≤10s 探活为唯一成败判据（R2-F2），成功切委托态。</summary>
    async Task<bool> WaitHeadlessHealthyAsync(TimeSpan limit)
    {
        var port = _cfg.Relay?.Port;
        if (port == null) return false;
        var client = new DelegatedRelayClient(port.Value);
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var st = await client.ProbeAsync().ConfigureAwait(true);
            if (st is { Running: true, Headless: true })
            {
                _delegated = client;
                _delegatedStatus = st;
                _delegatedFailures = 0;
                _form.SetDelegated(true, DelegatedHint());
                _form.UpdateRelay(st);
                return true;
            }
            await Task.Delay(500).ConfigureAwait(true);
        }
        return false;
    }

    /// <summary>全局链单视图渲染（无探测渠道的兜底路径）。</summary>
    void Apply(UsageSnapshot s)
    {
        _lastSnap = s;
        try
        {
            // 全局快照喂给中转探测缓存（--once/dev 注入链的运行时对应路径；探测渠道存在时不走此处）
            _relay?.FeedSnapshot(s);
            RenderTitleIcon(s);
            _form.UpdateData(s);
            // 委托态渲染远端快照（渲染代码零改动，§2.4）；非委托态走本地三态快照
            _form.UpdateRelay(_delegated != null ? _delegatedStatus : _relay?.Snapshot());
        }
        catch (Exception ex)
        {
            try { _form.Text = "GLM: " + ex.Message; } catch { /* ignore */ }
        }
    }

    /// <summary>标题栏文字 / 托盘图标 / 任务栏进度条（多渠道模式下固定跟随默认视图）。</summary>
    void RenderTitleIcon(UsageSnapshot s)
    {
        if (s.Error != null)
        {
            _form.Text = $"{(DevMode.IsDev ? "[DEV] " : "")}GLM: {s.Error}";
            SetIcon(IconRenderer.RenderTextIcon("?", Color.Gray));
            TaskbarProgress.Clear(_form.Handle);
        }
        else if ((s.Week ?? s.FiveHour ?? s.Mcp) is { Remaining: int r } main)
        {
            var tag = s.Week != null ? "周" : s.FiveHour != null ? "5h" : "MCP";
            var title = $"{(DevMode.IsDev ? "[DEV] " : "")}GLM {tag} {r}%";
            if (main.Detail != null) title += $" · {main.DetailLabel ?? "已用"} {main.Detail}";
            if (main.ResetLabel != null) title += $" · ↻ {main.ResetLabel}";
            _form.Text = title;
            // 多分辨率图标：每帧原生渲染，标题栏 16px / 任务栏 24-32px 都不缩放丢笔画
            SetIcon(IconRenderer.RenderTextIcon(r.ToString(CultureInfo.InvariantCulture), Colors.ForRemaining(r)));
            TaskbarProgress.SetValue(_form.Handle, (ulong)Math.Clamp(r, 0, 100), 100);
        }
        else
        {
            _form.Text = $"{(DevMode.IsDev ? "[DEV] " : "")}GLM 用量 · 暂无配额数据";
            SetIcon(IconRenderer.RenderTextIcon("–", Color.Gray));
            TaskbarProgress.Clear(_form.Handle);
        }
    }

    /// <summary>悬停托盘图标：显示多行摘要浮窗（周/5h/活跃度/中转），内容取最近一次刷新快照。</summary>
    void ShowTrayPreview()
    {
        var s = _lastSnap;
        if (s == null) return;
        try
        {
            _trayPreview ??= new TrayPreviewForm();
            _trayPreview.ShowPreview(PreviewTitle(s), PreviewBody(s, RelayLine(), ProbeChannelLines()), Cursor.Position);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>托盘浮窗的多探测渠道摘要行（每渠道一行：id + 各窗口剩余；单探测渠道 = null 走原明细展示）。</summary>
    string? ProbeChannelLines()
    {
        var snaps = _relay is { Running: true } r ? r.ProviderSnaps() : null;
        if (snaps is not { Count: > 1 }) return null;
        var currentId = _relay!.Router.Current(DateTimeOffset.Now)?.Id;
        var order = _relay.Options.Providers.ToDictionary(p => p.Id, p => (int?)p.Priority);
        var lines = snaps
            .OrderBy(x => order.GetValueOrDefault(x.Id) ?? int.MaxValue)
            .Select(x =>
            {
                var mark = x.Id == currentId ? "▶ " : "  ";
                var parts = new List<string>();
                if (x.Snap.Week?.Remaining is int wk) parts.Add($"{x.Snap.Week.Label ?? "周"} {wk}%");
                if (x.Snap.FiveHour?.Remaining is int fh) parts.Add($"5h {fh}%");
                var pct = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
                return $"{mark}{x.Id}{pct}";
            });
        return string.Join("\n", lines);
    }

    /// <summary>托盘悬停的"中转"行；仅中转运行时显示，其余状态（未初始化/显式关闭）与旧版无 relay 行为一致。
    /// 委托态：远端快照简化一行（渠道数与 current；§2.4 可选项按简化实现）。</summary>
    string? RelayLine()
    {
        if (_delegated != null)
        {
            var st = _delegatedStatus;
            if (st is not { Running: true }) return null;
            var by = st.Headless ? "开机后台服务" : "另一实例";
            var cur = st.CurrentId != null ? $"{st.CurrentId} · {st.Providers.Count} 渠道" : "无可用渠道";
            return $"中转  {cur}（{by}）";
        }
        if (_relay is not { Running: true } r) return null;
        return r.PreviewLine();
    }

    /// <summary>
    /// 用系统默认浏览器打开中转管理面板；中转未运行时给出提示（消除静默无反应）。
    /// 委托态分流（R1-F2）：绕过本地 Running 守卫——委托远端与 UI 同读一份配置文件，
    /// 从 relay.port 推导的 URL 指向 headless，全功能管理天然可用。详情窗按钮 / 托盘菜单 /
    /// 缺 key 引导按钮三入口共用此分流（§2.4）。
    /// </summary>
    void OpenAdminPanel(string? url = null)
    {
        var target = url;
        if (target == null)
        {
            if (_relay is { Running: true } r)
            {
                target = r.AdminUrl;
            }
            else if (_delegated != null)
            {
                target = $"http://127.0.0.1:{_delegated.Port}/admin"; // 委托远端面板（R1-F2）
            }
        }
        if (target == null)
        {
            try
            {
                MessageBox.Show(_form, "中转未启用，请先在详情窗口启用中转。",
                    "管理面板", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                /* ignore */
            }
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            /* ignore */
        }
    }

    static string PreviewTitle(UsageSnapshot s) =>
        $"GLM 用量{(s.Level != null ? $"  [{s.Level}]" : "")}";

    static string PreviewBody(UsageSnapshot s, string? relayLine, string? probeLines = null)
    {
        if (s.Error != null) return s.Error;
        var lines = new List<string>();
        if (probeLines != null) lines.Add(probeLines.TrimEnd());
        if (DetailForm.LimitLine(s.Week, "周积分") is { } week) lines.Add(week);
        if (DetailForm.LimitLine(s.FiveHour, "5h 积分") is { } five) lines.Add(five);
        if (s.Activity is { } a)
        {
            var act = string.Join(" · ", new[]
                { DetailForm.Cumulative(a), DetailForm.TodayLine(a), DetailForm.StreakLine(a) }
                .Where(x => x != null));
            if (act.Length > 0) lines.Add($"活跃度  {act}");
        }
        if (relayLine != null) lines.Add(relayLine);
        lines.Add($"更新于 {s.FetchedAt.LocalDateTime:HH:mm}");
        return string.Join("\n", lines);
    }

    void SetIcon(Icon next)
    {
        var old = _icon;
        _form.Icon = next;
        _tray.Icon = next;
        _icon = next;
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relay?.Dispose(); // 先停中转监听，再释放 UI 资源
            _timer.Dispose();
            _trimTimer?.Dispose();
            _client.Dispose();
            // 托盘图标必须先隐藏再释放，避免残留幽灵图标
            _tray.Visible = false;
            _tray.Icon = null;
            _tray.Dispose();
            _trayPreview?.Dispose();
            _icon?.Dispose();
            _icon = null;
        }
        base.Dispose(disposing);
    }
}
