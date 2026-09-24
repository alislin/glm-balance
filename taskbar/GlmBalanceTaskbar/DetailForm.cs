using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GlmBalanceTaskbar;

/// <summary>
/// 详情窗口：点击任务栏图标展开；关闭按钮 = 最小化，退出走窗口内按钮。
/// 区块化流式布局：隐藏区块不占位，各区块自动上移收紧，窗口高度随内容自适应。
/// </summary>
sealed class DetailForm : Form
{
    const int PageMargin = 16;
    const int SectionGap = 18;
    const int RowGap = 8;

    readonly Label _lblTitle;
    readonly Label _lblLevel;
    readonly Label _lblError;

    readonly Label _lbl5h;
    readonly Label _lbl5hPct;
    readonly BarControl _bar5h;
    readonly Label _lbl5hDetail;

    readonly Label _lblWeek;
    readonly Label _lblWeekPct;
    readonly BarControl _barWeek;
    readonly Label _lblWeekDetail;

    readonly Label _lblMcp;
    readonly Label _lblMcpPct;
    readonly BarControl _barMcp;
    readonly Label _lblMcpDetail;

    readonly Label _lbl24hKey;
    readonly Label _lbl24hVal;

    readonly Label _lblActHeader;
    readonly Label _lblActCumKey;
    readonly Label _lblActCumVal;
    readonly Label _lblActTodayKey;
    readonly Label _lblActTodayVal;
    readonly Label _lblActStreakKey;
    readonly Label _lblActStreakVal;
    readonly Label _lblActSparkKey;
    readonly SparkChartControl _sparkChart;

    readonly Label _lblModels;
    readonly Label _lblUpdated;
    readonly Button _btnRefresh;
    readonly Button _btnExit;

    readonly List<Section> _sections;
    bool _reallyExit;
    int _desiredHeight = 588;

    /// <summary>用户点击"立即刷新"。</summary>
    public event EventHandler? RefreshRequested;

    public DetailForm()
    {
        Text = "GLM 用量";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Colors.Bg;
        ClientSize = new Size(440, 588);
        Font = new Font("Segoe UI", 9F);

        int W = ClientSize.Width;
        _lblTitle = AddLabel("GLM 用量", PageMargin, 0, 140, 26, bold: true, fontSize: 12F);
        _lblLevel = AddLabel("", 168, 0, 240, 24, muted: true);
        _lblError = AddLabel("", PageMargin, 0, W - PageMargin * 2, 44, color: Colors.Error);

        QuotaSection("5h 积分", out _lbl5h, out _lbl5hPct, out _bar5h, out _lbl5hDetail);
        QuotaSection("周积分", out _lblWeek, out _lblWeekPct, out _barWeek, out _lblWeekDetail);
        QuotaSection("MCP 月度", out _lblMcp, out _lblMcpPct, out _barMcp, out _lblMcpDetail);

        _lbl24hKey = AddLabel("24h", PageMargin, 0, 44, 20, muted: true);
        _lbl24hVal = AddLabel("", 68, 0, W - 84, 20);

        _lblActHeader = AddLabel("活跃度", PageMargin, 0, 100, 20, muted: true, bold: true);
        _lblActCumKey = AddLabel("累计", PageMargin, 0, 64, 20, muted: true);
        _lblActCumVal = AddLabel("", 88, 0, W - 104, 20);
        _lblActTodayKey = AddLabel("今日", PageMargin, 0, 64, 20, muted: true);
        _lblActTodayVal = AddLabel("", 88, 0, W - 104, 20);
        _lblActStreakKey = AddLabel("连续", PageMargin, 0, 64, 20, muted: true);
        _lblActStreakVal = AddLabel("", 88, 0, W - 104, 20);
        _lblActSparkKey = AddLabel("近14天", PageMargin, 0, 64, 20, muted: true);
        _sparkChart = new SparkChartControl { Location = new Point(88, 0), Size = new Size(W - 104, 44), TabStop = false };
        Controls.Add(_sparkChart);

        _lblModels = AddLabel("", PageMargin, 0, W - PageMargin * 2, 72);
        _lblUpdated = AddLabel("", PageMargin, 0, 210, 18, muted: true);

        _btnRefresh = new Button { Text = "立即刷新", Location = new Point(W - 196, 0), Size = new Size(92, 28) };
        _btnRefresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_btnRefresh);

        _btnExit = new Button { Text = "退出", Location = new Point(W - 92, 0), Size = new Size(72, 28) };
        _btnExit.Click += (_, _) => { _reallyExit = true; Close(); };
        Controls.Add(_btnExit);

        // 数据区块初始全部隐藏：加载中只显示标题，首刷后填充
        _lblError.Visible = false;
        _lbl5h.Visible = _lbl5hPct.Visible = _bar5h.Visible = _lbl5hDetail.Visible = false;
        _lblWeek.Visible = _lblWeekPct.Visible = _barWeek.Visible = _lblWeekDetail.Visible = false;
        _lblMcp.Visible = _lblMcpPct.Visible = _barMcp.Visible = _lblMcpDetail.Visible = false;
        _lbl24hKey.Visible = _lbl24hVal.Visible = false;
        _lblActHeader.Visible = false;
        SetRow(_lblActCumKey, _lblActCumVal, false, null);
        SetRow(_lblActTodayKey, _lblActTodayVal, false, null);
        SetRow(_lblActStreakKey, _lblActStreakVal, false, null);
        _lblActSparkKey.Visible = _sparkChart.Visible = false;
        _lblModels.Visible = false;

        // 区块注册（顺序即布局顺序）
        _sections = new List<Section>
        {
            new() { Rows = { Rw(26, _lblTitle, _lblLevel) } },
            new() { Rows = { Rw(44, _lblError) } },
            new() { Rows = { Rw(24, _lbl5h, _lbl5hPct), Rw(12, _bar5h), Rw(20, _lbl5hDetail) } },
            new() { Rows = { Rw(24, _lblWeek, _lblWeekPct), Rw(12, _barWeek), Rw(20, _lblWeekDetail) } },
            new() { Rows = { Rw(24, _lblMcp, _lblMcpPct), Rw(12, _barMcp), Rw(20, _lblMcpDetail) } },
            new() { Rows = { Rw(20, _lbl24hKey, _lbl24hVal) } },
            new()
            {
                Rows =
                {
                    Rw(20, _lblActHeader),
                    Rw(20, _lblActCumKey, _lblActCumVal),
                    Rw(20, _lblActTodayKey, _lblActTodayVal),
                    Rw(20, _lblActStreakKey, _lblActStreakVal),
                    Rw(20, _lblActSparkKey),
                    Rw(44, _sparkChart),
                },
            },
            new() { Rows = { new Row { Controls = new Control[] { _lblModels }, Height = () => _lblModels.Height } } },
        };

        Relayout();
    }

    void QuotaSection(string fallback, out Label lbl, out Label pct, out BarControl bar, out Label detail)
    {
        int W = ClientSize.Width;
        lbl = AddLabel(fallback, PageMargin, 0, 160, 24, muted: true);
        pct = AddLabel("?", W - PageMargin - 84, 0, 84, 24, bold: true, fontSize: 11F, right: true);
        bar = new BarControl { Location = new Point(PageMargin, 0), Size = new Size(W - PageMargin * 2, 12), TabStop = false };
        Controls.Add(bar);
        detail = AddLabel("", PageMargin, 0, W - PageMargin * 2, 20, muted: true);
    }

    Label AddLabel(string text, int x, int y, int w, int h, bool muted = false, bool bold = false, float fontSize = 9F, Color? color = null, bool right = false, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
    {
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        var l = new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            AutoEllipsis = true,
            Anchor = anchor,
            Font = new Font("Segoe UI", fontSize, style),
            ForeColor = color ?? (muted ? Colors.Muted : Colors.Text),
            TextAlign = right ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
        };
        Controls.Add(l);
        return l;
    }

    public void UpdateData(UsageSnapshot s)
    {
        _lblLevel.Text = s.Level != null ? $"[{s.Level}]" : "";
        _lblError.Visible = !string.IsNullOrEmpty(s.Error);
        _lblError.Text = s.Error ?? "";

        ApplyLimit("5h 积分", _lbl5h, _lbl5hPct, _bar5h, _lbl5hDetail, s.FiveHour);
        ApplyLimit("周积分", _lblWeek, _lblWeekPct, _barWeek, _lblWeekDetail, s.Week);
        ApplyLimit("MCP 月度", _lblMcp, _lblMcpPct, _barMcp, _lblMcpDetail, s.Mcp);

        bool has24 = s.Tokens24h != null;
        _lbl24hKey.Visible = _lbl24hVal.Visible = has24;
        if (s.Tokens24h is long t24) _lbl24hVal.Text = $"{ActivityParser.FmtTokens(t24)} tokens";

        var a = s.Activity;
        bool hasAct = a != null;
        _lblActHeader.Visible = hasAct;
        SetRow(_lblActCumKey, _lblActCumVal, hasAct, Cumulative(a));
        SetRow(_lblActTodayKey, _lblActTodayVal, hasAct, TodayLine(a));
        SetRow(_lblActStreakKey, _lblActStreakVal, hasAct, StreakLine(a));

        var daily = a?.DailyTokens;
        var showSpark = hasAct && daily is { Length: > 0 };
        _lblActSparkKey.Visible = _sparkChart.Visible = showSpark;
        if (showSpark) _sparkChart.Values = daily;

        bool hasModels = s.Models.Count > 0;
        _lblModels.Visible = hasModels;
        if (hasModels)
        {
            var lines = s.Models.Take(4)
                .Select(m => $"· {m.Name}  {ActivityParser.FmtTokens(m.Tokens)}")
                .ToList();
            _lblModels.Text = string.Join(Environment.NewLine, lines);
            _lblModels.Height = lines.Count * 18;
        }

        _lblUpdated.Text = $"更新于 {s.FetchedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
        Relayout();
    }

    /// <summary>
    /// 流式重排：从上到下累加可见区块与行，不可见不占位；底部栏贴内容。
    /// 窗口最小化时 Win32 层面改尺寸无效，目标高度记入 _desiredHeight，恢复时由 OnResize 应用。
    /// </summary>
    void Relayout()
    {
        int y = PageMargin;
        var needGap = false;
        foreach (var s in _sections)
        {
            if (s.Rows[0].Controls[0].Visible == false) continue;
            if (needGap) y += SectionGap;
            needGap = true;
            foreach (var row in s.Rows)
            {
                if (row.Controls[0].Visible == false) continue;
                foreach (var c in row.Controls)
                {
                    c.Top = y;
                }
                y += row.Height() + RowGap;
            }
        }
        if (needGap) y += SectionGap - RowGap;

        _btnRefresh.Top = y;
        _btnExit.Top = y;
        _lblUpdated.Top = y + 7;
        _desiredHeight = y + 40;
        ApplyDesiredSize();
    }

    void ApplyDesiredSize()
    {
        if (WindowState != FormWindowState.Minimized && ClientSize.Height != _desiredHeight)
        {
            ClientSize = new Size(440, _desiredHeight);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyDesiredSize();
    }

    static string? Cumulative(ActivityInfo? a)
    {
        if (a?.TotalTokens is not long total) return null;
        var t = $"{ActivityParser.FmtTokens(total)} tokens";
        return a.DurationLabel != null ? $"{t} · {a.DurationLabel}" : t;
    }

    static string? TodayLine(ActivityInfo? a)
    {
        if (a?.TodayTokens == null && a?.PeakTokens == null) return null;
        var today = a?.TodayTokens != null ? ActivityParser.FmtTokens(a.TodayTokens.Value) : "?";
        var peak = a?.PeakTokens != null
            ? $" · 峰值 {ActivityParser.FmtTokens(a.PeakTokens.Value)}{(a?.PeakDate != null ? $" @ {a.PeakDate}" : "")}"
            : "";
        return $"{today}{peak}";
    }

    static string? StreakLine(ActivityInfo? a)
    {
        if (a?.CurrentStreakDays == null && a?.LongestStreakDays == null) return null;
        var cur = a?.CurrentStreakDays != null ? $"{a.CurrentStreakDays} 天" : "?";
        var longest = a?.LongestStreakDays is { } lg && lg != a?.CurrentStreakDays ? $" · 最长 {lg}" : "";
        return $"{cur}{longest}";
    }

    static void SetRow(Label key, Label val, bool visible, string? value)
    {
        key.Visible = val.Visible = visible && value != null;
        if (value != null) val.Text = value;
    }

    static void ApplyLimit(string fallbackLabel, Label lbl, Label pct, BarControl bar, Label detail, LimitInfo? lim)
    {
        var visible = lim != null;
        lbl.Visible = pct.Visible = bar.Visible = detail.Visible = visible;
        if (!visible) return;

        lbl.Text = lim!.Label ?? fallbackLabel;
        pct.Text = lim.Remaining != null ? $"{lim.Remaining}%" : "?";
        var color = Colors.ForRemaining(lim.Remaining);
        pct.ForeColor = color;
        bar.Percent = lim.Remaining;
        bar.BarColor = color;
        detail.Text = lim.ResetLabel != null ? $"↻ {lim.ResetLabel}" : "";
        if (lim.Detail != null)
        {
            detail.Text = $"{lim.DetailLabel ?? "已用"} {lim.Detail}" + (lim.ResetLabel != null ? $" · ↻ {lim.ResetLabel}" : "");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关闭 = 最小化常驻；真正退出走窗口内"退出"按钮
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
        }
        base.OnFormClosing(e);
    }

    sealed class Row
    {
        public required Control[] Controls { get; init; }
        public required Func<int> Height { get; init; }
    }

    sealed class Section
    {
        public List<Row> Rows { get; } = new();
    }

    static Row Rw(int h, params Control[] c) => new() { Controls = c, Height = () => h };
}

/// <summary>胶囊进度条（高度 12、全圆角，颜色随剩余量）。</summary>
sealed class BarControl : Control
{
    double? _percent;
    Color? _barColor;

    public BarControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 12;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double? Percent
    {
        set { _percent = value; Invalidate(); }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? BarColor
    {
        set { _barColor = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new RectangleF(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using (var bg = RoundPath(rect, Height / 2F))
        using (var bgBrush = new SolidBrush(Colors.Track))
        {
            g.FillPath(bgBrush, bg);
        }
        if (_percent is > 0)
        {
            var w = (float)(Math.Clamp(_percent.Value, 0, 100) / 100 * rect.Width);
            if (w >= Height / 2F)
            {
                using var fg = RoundPath(new RectangleF(0, 0, w, rect.Height), Height / 2F);
                using var fgBrush = new SolidBrush(_barColor ?? Colors.Success);
                g.FillPath(fgBrush, fg);
            }
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundPath(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// 近 N 天用量柱状图：底边基线、高度按 max 归一；今天实心绿、历史浅绿；
/// 悬停高亮该柱并显示日期与用量 tooltip（日期由今天倒推，末位 = 今天）。
/// </summary>
sealed class SparkChartControl : Control
{
    readonly ToolTip _tip = new();
    IReadOnlyList<long>? _values;
    int _hoverIndex = -1;

    public SparkChartControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 44;
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<long>? Values
    {
        set { _values = value; _hoverIndex = -1; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
        _tip.Hide(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var idx = HitBar(e.X);
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
        Invalidate();
        try
        {
            if (idx >= 0 && _values is { Count: > 0 })
            {
                var n = _values.Count;
                var label = idx == n - 1 ? "今天" : DateTime.Today.AddDays(-(n - 1 - idx)).ToString("MM-dd", CultureInfo.InvariantCulture);
                _tip.Show($"{label}  {ActivityParser.FmtTokens(_values[idx])}", this, e.X + 8, Math.Max(0, e.Y - 26), 2000);
            }
            else
            {
                _tip.Hide(this);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    int HitBar(int x)
    {
        if (_values is not { Count: > 0 }) return -1;
        int n = _values.Count;
        float gap = 2F;
        float barW = Math.Max(2F, (Width - gap * (n - 1)) / n);
        var i = (int)Math.Floor(x / (barW + gap));
        if (i < 0 || i >= n) return -1;
        var bx = i * (barW + gap);
        return x >= bx && x <= bx + barW ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_values is not { Count: > 0 }) return;
        long max = 0;
        foreach (var v in _values)
        {
            if (v > max) max = v;
        }
        if (max <= 0) return;

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int n = _values.Count;
        float gap = 2F;
        float barW = Math.Max(2F, (Width - gap * (n - 1)) / n);
        float baseline = Height - 1F;
        for (var i = 0; i < n; i++)
        {
            var isToday = i == n - 1;
            Color color;
            if (i == _hoverIndex) color = isToday ? Colors.SuccessDark : Colors.BarPastHover;
            else color = isToday ? Colors.Success : Colors.BarPast;
            // 0 值也保留最小可见高度（与 sparkline ▁ 的表现一致）
            var h = Math.Max(3F, _values[i] / (float)max * (Height - 2F));
            using var path = TopRoundPath(i * (barW + gap), baseline - h, barW, h, Math.Min(3F, barW / 2F));
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath TopRoundPath(float x, float y, float w, float h, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        if (r < 0.5F)
        {
            p.AddRectangle(new RectangleF(x, y, w, h));
            return p;
        }
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddLine(x + w, y + r, x + w, y + h);
        p.AddLine(x, y + h, x, y + r);
        p.CloseFigure();
        return p;
    }
}
