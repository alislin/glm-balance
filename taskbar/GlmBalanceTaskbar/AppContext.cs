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
    readonly System.Windows.Forms.Timer _timer;
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

    async Task RefreshAsync(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
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
        try
        {
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
        }
        catch (Exception ex)
        {
            try { _form.Text = "GLM: " + ex.Message; } catch { /* ignore */ }
        }
    }

    void SetIcon(Icon next)
    {
        var old = _icon;
        _form.Icon = next;
        _icon = next;
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _client.Dispose();
            _icon?.Dispose();
            _icon = null;
        }
        base.Dispose(disposing);
    }
}
