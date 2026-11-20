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

    readonly AppConfig _cfg;
    readonly GlmApiClient _client;
    readonly DetailForm _form;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly RelayManager _relay;
    TrayPreviewForm? _trayPreview;
    UsageSnapshot? _lastSnap;
    Icon? _icon;
    int _busy;
    DateTimeOffset _lastActivityFetch = DateTimeOffset.MinValue;
    ActivityInfo? _activity;

    public AppContext(AppConfig cfg)
    {
        _cfg = cfg;
        _client = new GlmApiClient(cfg);
        _form = new DetailForm();
        MainForm = _form;
        _form.RefreshRequested += (_, _) => _ = RefreshAsync(force: true);
        _form.PanelRequested += (_, _) => OpenAdminPanel();
        _form.RelayToggleRequested += (_, _) => _ = ToggleRelayAsync();

        // 中转：始终构造管理器（未初始化/显式关闭时开关与面板入口仍可用，消除静默无反应）；
        // 仅 enabled=true 时启动；启动失败只记 warnings，配额显示不受影响。
        // 未初始化（无 relay 段）用内存默认占位，等待用户点击「启用中转」才写回配置。
        var relayOpts = cfg.Relay ?? RelayOptions.CreateDefault();
        var relay = new RelayManager(cfg, relayOpts, initialized: cfg.Relay != null);
        if (cfg.Relay is { Enabled: true })
        {
            relay.Start(cfg.Warnings); // 失败原因写入 cfg.Warnings（详情窗可见）；实例保留供状态展示与 Dispose
        }
        _relay = relay;
        _form.SetWarnings(cfg.Warnings); // M4：启动期警告（含 relay 启动失败）在详情窗可见

        // 测试钩子：--auto-init-relay 走与「启用中转」开关完全相同的路径（首启引导沙箱验证用）
        if (Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--auto-init-relay", StringComparison.OrdinalIgnoreCase)))
        {
            _ = ToggleRelayAsync();
        }

        // 系统托盘：动态数字图标（不设置 Text，避免与自绘多行摘要浮窗重叠出现两个 tooltip）
        _tray = new NotifyIcon
        {
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开详情", null, (_, _) => ShowMainWindow());
        menu.Items.Add("立即刷新", null, (_, _) => _ = RefreshAsync(force: true));
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
        _form.Text = "GLM 用量 · 加载中…";
        SetIcon(IconRenderer.RenderTextIcon("…", Color.Gray));

        // 最小化显示：任务栏按钮出现且不弹窗
        _form.WindowState = FormWindowState.Minimized;
        _form.Show();

        _timer = new System.Windows.Forms.Timer { Interval = cfg.IntervalSeconds * 1000 };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync(force: true);
        _timer.Start();
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
            });
        }
        catch
        {
            /* ignore */
        }
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
        if (relay == null) return;
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
            // 活跃度独立节流（失败时立即重试，与插件一致）
            if (_cfg.Activity && (force || DateTimeOffset.Now - _lastActivityFetch >= TimeSpan.FromMilliseconds(ActivityTtlMs)))
            {
                _lastActivityFetch = DateTimeOffset.Now;
                var act = await _client.FetchActivityAsync().ConfigureAwait(true);
                if (act != null) _activity = act;
                else _lastActivityFetch = DateTimeOffset.MinValue;
            }

            var snap = await _client.FetchUsageAsync().ConfigureAwait(true);
            snap.Activity = _activity;
            Apply(snap);
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

    void Apply(UsageSnapshot s)
    {
        _lastSnap = s;
        try
        {
            // 最新快照喂给中转探测缓存（ZhipuMonitorProbe 优先复用）
            _relay?.FeedSnapshot(s);

            if (s.Error != null)
            {
                _form.Text = $"GLM: {s.Error}";
                SetIcon(IconRenderer.RenderTextIcon("?", Color.Gray));
                TaskbarProgress.Clear(_form.Handle);
            }
            else if ((s.Week ?? s.FiveHour ?? s.Mcp) is { Remaining: int r } main)
            {
                var tag = s.Week != null ? "周" : s.FiveHour != null ? "5h" : "MCP";
                var title = $"GLM {tag} {r}%";
                if (main.Detail != null) title += $" · {main.DetailLabel ?? "已用"} {main.Detail}";
                if (main.ResetLabel != null) title += $" · ↻ {main.ResetLabel}";
                _form.Text = title;
                // 多分辨率图标：每帧原生渲染，标题栏 16px / 任务栏 24-32px 都不缩放丢笔画
                SetIcon(IconRenderer.RenderTextIcon(r.ToString(CultureInfo.InvariantCulture), Colors.ForRemaining(r)));
                TaskbarProgress.SetValue(_form.Handle, (ulong)Math.Clamp(r, 0, 100), 100);
            }
            else
            {
                _form.Text = "GLM 用量 · 暂无配额数据";
                SetIcon(IconRenderer.RenderTextIcon("–", Color.Gray));
                TaskbarProgress.Clear(_form.Handle);
            }
            _form.UpdateData(s);
            _form.UpdateRelay(_relay?.Snapshot()); // 三态快照：运行/显式关闭/未初始化均由 DetailForm 渲染
        }
        catch (Exception ex)
        {
            try { _form.Text = "GLM: " + ex.Message; } catch { /* ignore */ }
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
            _trayPreview.ShowPreview(PreviewTitle(s), PreviewBody(s, RelayLine()), Cursor.Position);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>托盘悬停的"中转"行；仅中转运行时显示，其余状态（未初始化/显式关闭）与旧版无 relay 行为一致。</summary>
    string? RelayLine()
    {
        if (_relay is not { Running: true } r) return null;
        return r.PreviewLine();
    }

    /// <summary>
    /// 用系统默认浏览器打开中转管理面板；中转未运行时给出提示（消除静默无反应）。
    /// </summary>
    void OpenAdminPanel(string? url = null)
    {
        if (_relay is not { Running: true } r)
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
        var target = url ?? r.AdminUrl;
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

    static string PreviewBody(UsageSnapshot s, string? relayLine)
    {
        if (s.Error != null) return s.Error;
        var lines = new List<string>();
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
