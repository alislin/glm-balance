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
    readonly Label _lblWarn;

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
    readonly Button _btnPanel;

    readonly Label _lblRelayHeader;
    readonly Label[] _lblRelayRows;
    readonly Label _lblRelayHint;
    readonly Button _btnRelayToggle;
    readonly Button _btnRelayGuide;
    const int MaxRelayRows = 6;

    // ── 自启动分组（G2/§2.5；dev 不创建，字段保持 null）──
    readonly Label? _lblAutostartHeader;
    readonly Label? _lblAutostartState;
    readonly Button? _btnAutostartToggle;
    readonly CheckBox? _chkRunAtLogon;
    readonly Label? _lblAutostartNote;
    bool _suppressRunAtLogonEvent; // 程序化设置勾选态时不外派事件（防回环）

    // ── 委托态（G3/§2.4）：只读渲染参数 ──
    bool _delegated;
    string? _delegatedHint;

    readonly List<Section> _sections;
    bool _reallyExit;
    int _desiredHeight = 588;
    /// <summary>最近一次 UpdateRelay 收到的快照（右键菜单读 pin 态用）。</summary>
    RelayStatus? _lastRelayStatus;

    /// <summary>渠道行共享字体（M5：每刷新 4+ 次 new Font → 静态复用；共享 Font 不可 Dispose，进程生命周期）。</summary>
    static readonly Font RelayRowFont = new("Segoe UI", 8.5F);
    static readonly Font RelayRowCurrentFont = new("Segoe UI", 9F, FontStyle.Bold);

    /// <summary>用户点击"立即刷新"。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>用户点击"管理面板"。</summary>
    public event EventHandler? PanelRequested;

    /// <summary>用户点击中转分组头部的「启用/关闭中转」开关。</summary>
    public event EventHandler? RelayToggleRequested;

    /// <summary>用户在渠道行右键菜单选择"强制路由到 {id} / 解除强制"（pin=true 设为强制）。</summary>
    public event EventHandler<(string ProviderId, bool Pin)>? PinRequested;

    /// <summary>用户点击「注册/注销开机服务」主开关（AppContext 按当前注册态分流）。</summary>
    public event EventHandler? AutostartToggleRequested;

    /// <summary>用户切换「登录时启动（仅界面）」副开关（HKCU Run）。</summary>
    public event EventHandler<bool>? RunAtLogonChangeRequested;

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
        _lblWarn = AddLabel("", PageMargin, 0, W - PageMargin * 2, 40, color: Colors.Warning);

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

        // 模型中转分组（三态：未初始化/显式关闭 → 开关 + 说明；运行 → 渠道行；运行中缺 key → 引导态）
        _lblRelayHeader = AddLabel("模型中转", PageMargin, 0, 200, 20, muted: true, bold: true);
        _lblRelayRows = new Label[MaxRelayRows];
        for (var i = 0; i < MaxRelayRows; i++)
        {
            _lblRelayRows[i] = AddLabel("", 34, 0, W - PageMargin - 34, 20, muted: false, fontSize: 8.5F);
            _lblRelayRows[i].Visible = false;
            var row = _lblRelayRows[i];
            row.MouseClick += (s, e) => ShowRelayRowMenu(row, e);
        }
        int W2 = ClientSize.Width;
        _lblRelayHint = AddLabel("", PageMargin, 0, W2 - PageMargin * 2, 32, muted: true, fontSize: 8.5F);
        _lblRelayHint.Visible = false;
        _btnRelayToggle = new Button { Text = "启用中转", Location = new Point(W2 - PageMargin - 76, 0), Size = new Size(76, 22) };
        _btnRelayToggle.Click += (_, _) => RelayToggleRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_btnRelayToggle);
        _btnRelayGuide = new Button { Text = "打开管理面板完成配置", Location = new Point(PageMargin, 0), Size = new Size(168, 26) };
        _btnRelayGuide.Click += (_, _) => PanelRequested?.Invoke(this, EventArgs.Empty);
        _btnRelayGuide.Visible = false;
        Controls.Add(_btnRelayGuide);

        _lblModels = AddLabel("", PageMargin, 0, W - PageMargin * 2, 72);
        _lblUpdated = AddLabel("", 116, 0, 126, 18, muted: true);

        // 自启动分组（G2/§2.5：位于「模型中转」区块之后；dev 硬禁整个区块不创建，R5）
        if (!DevMode.IsDev)
        {
            _lblAutostartHeader = AddLabel("自启动", PageMargin, 0, 200, 20, muted: true, bold: true);
            _lblAutostartState = AddLabel("未注册", PageMargin, 0, W - PageMargin * 2, 20);
            _btnAutostartToggle = new Button
            {
                Text = "注册开机服务（无需登录）",
                Location = new Point(PageMargin, 0),
                Size = new Size(180, 26),
            };
            _btnAutostartToggle.Click += (_, _) => AutostartToggleRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(_btnAutostartToggle);
            _chkRunAtLogon = new CheckBox
            {
                Text = "登录时启动（仅界面）",
                Location = new Point(PageMargin, 0),
                AutoSize = true,
            };
            _chkRunAtLogon.CheckedChanged += (_, _) =>
            {
                if (!_suppressRunAtLogonEvent)
                {
                    RunAtLogonChangeRequested?.Invoke(this, _chkRunAtLogon.Checked);
                }
            };
            Controls.Add(_chkRunAtLogon);
            _lblAutostartNote = AddLabel(
                "开机服务独立于本窗口运行；登录后本程序仅显示用量。",
                PageMargin, 0, W - PageMargin * 2, 20, muted: true, fontSize: 8.5F);
        }

        _btnPanel = new Button { Text = "管理面板", Location = new Point(PageMargin, 0), Size = new Size(92, 28) };
        _btnPanel.Click += (_, _) => PanelRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_btnPanel);

        _btnRefresh = new Button { Text = "立即刷新", Location = new Point(W - 196, 0), Size = new Size(92, 28) };
        _btnRefresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_btnRefresh);

        _btnExit = new Button { Text = "退出", Location = new Point(W - 92, 0), Size = new Size(72, 28) };
        _btnExit.Click += (_, _) => RequestExit();
        Controls.Add(_btnExit);

        // 数据区块初始全部隐藏：加载中只显示标题，首刷后填充
        _lblError.Visible = false;
        _lblWarn.Visible = false;
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
        // 中转分组头部与开关常驻（三态均显示开关；未初始化另有说明行）

        // 区块注册（顺序即布局顺序）
        _sections = new List<Section>
        {
            new() { Rows = { Rw(26, _lblTitle, _lblLevel) } },
            new() { Rows = { Rw(44, _lblError) } },
            new() { Rows = { new Row { Controls = new Control[] { _lblWarn }, Height = () => _lblWarn.Height } } },
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
            RelaySection(),
        };
        if (!DevMode.IsDev)
        {
            _sections.Add(AutostartSection());
        }
        _sections.Add(new() { Rows = { new Row { Controls = new Control[] { _lblModels }, Height = () => _lblModels.Height } } });

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

    /// <summary>「模型中转」区块：标题行（含启停开关）+ 说明行（未初始化）+ 引导按钮行（运行中缺 key）+
    /// 每渠道一行（随 UpdateRelay 显隐）。</summary>
    Section RelaySection()
    {
        var s = new Section();
        s.Rows.Add(new Row { Controls = new Control[] { _lblRelayHeader, _btnRelayToggle }, Height = () => 24 });
        s.Rows.Add(new Row { Controls = new Control[] { _lblRelayHint }, Height = () => _lblRelayHint.Visible ? _lblRelayHint.Height : 0 });
        s.Rows.Add(new Row { Controls = new Control[] { _btnRelayGuide }, Height = () => _btnRelayGuide.Visible ? _btnRelayGuide.Height + 4 : 0 });
        foreach (var lbl in _lblRelayRows)
        {
            s.Rows.Add(new Row { Controls = new Control[] { lbl }, Height = () => lbl.Visible ? 20 : 0 });
        }
        return s;
    }

    /// <summary>「自启动」区块（§2.5）：状态行四态 + 注册/注销主开关 + 登录时启动副开关 + 说明行。</summary>
    Section AutostartSection()
    {
        var s = new Section();
        s.Rows.Add(new Row { Controls = new Control[] { _lblAutostartHeader! }, Height = () => 20 });
        s.Rows.Add(new Row { Controls = new Control[] { _lblAutostartState! }, Height = () => _lblAutostartState!.Visible ? 20 : 0 });
        s.Rows.Add(new Row { Controls = new Control[] { _btnAutostartToggle! }, Height = () => 26 });
        s.Rows.Add(new Row { Controls = new Control[] { _chkRunAtLogon! }, Height = () => 24 });
        s.Rows.Add(new Row { Controls = new Control[] { _lblAutostartNote! }, Height = () => 20 });
        return s;
    }

    /// <summary>渠道行状态文本；返回 (文本, 颜色, 是否当前通道高亮)。
    /// forcedId 为 sticky pin 强制通道 id：被 pin 行加 "[强制] " 前缀（叠加 ▶ 标记）。</summary>
    static (string Text, Color Color, bool Current) RelayRowText(ProviderStatus p, string? currentId, string? forcedId)
    {
        var current = p.Id == currentId;
        var mark = current ? "▶ " : "  ";
        if (p.Id == forcedId) mark += "[强制] ";
        var state = p.State switch
        {
            "normal" => "正常",
            "cooldown" => $"冷却 {p.CooldownRemainingSec ?? 0}s",
            "degraded" => "降级",
            "no-key" => "无 key",
            _ => p.State,
        };
        if (!p.Enabled) state = "已停用";
        var color = p.State switch
        {
            "normal" => current ? Colors.SuccessDark : Colors.Success,
            "degraded" => Colors.Warning,
            "cooldown" => Colors.Error,
            _ => Colors.Muted,
        };
        var text = $"{mark}{p.Id}  {state}";
        if (p.RemainingPct is { } pct) text += $" · {p.SourceLabel ?? "剩余"} {pct}%";
        if (p.LastUsedAt is { } used) text += $" · {used.LocalDateTime:HH:mm:ss}";
        if (p.LastError is { } err && err.Length > 0) text += $" · {Truncate(err, 48)}";
        return (text, color, current);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>启动期警告（如 relay 启动失败/禁用态）接入详情窗（M4）。</summary>
    public void SetWarnings(IReadOnlyList<string> warnings)
    {
        var text = string.Join(Environment.NewLine, warnings);
        _lblWarn.Visible = text.Length > 0;
        _lblWarn.Text = text;
        _lblWarn.Height = Math.Max(20, warnings.Count * 20);
        Relayout();
    }

    /// <summary>自启动分组状态刷新（AppContext 调用；§2.5 四态状态行 + 主/副开关渲染；dev 区块不创建）。</summary>
    public void UpdateAutostart(bool registered, string stateText, bool runAtLogon)
    {
        if (_btnAutostartToggle == null) return;
        _lblAutostartState!.Text = stateText;
        _lblAutostartState!.Visible = stateText.Length > 0;
        _btnAutostartToggle.Text = registered ? "注销开机服务" : "注册开机服务（无需登录）";
        _suppressRunAtLogonEvent = true;
        try
        {
            _chkRunAtLogon!.Checked = runAtLogon;
        }
        finally
        {
            _suppressRunAtLogonEvent = false;
        }
    }

    /// <summary>
    /// 委托态渲染（G3/§2.4）：禁用 relay 开关（只读）+ 提示文案；UpdateRelay 的说明行渲染
    /// 依此分流（委托 hint 与未初始化 hint 互斥，避免互踩）。hint 文案由 AppContext 提供。
    /// </summary>
    public void SetDelegated(bool delegated, string? hint)
    {
        _delegated = delegated;
        _delegatedHint = hint;
        _btnRelayToggle.Enabled = !delegated;
    }

    /// <summary>
    /// 更新「模型中转」分组（随刷新节流调用）。三态：
    /// 未初始化（无 relay 段）→ 开关「启用中转」+ 一句话说明；
    /// 显式关闭 → 开关「启用中转」（保留既有配置，点按后原样启用）；
    /// 运行 → 开关「关闭中转」+ 渠道行；启用但存在缺 key 渠道 → 引导态（列待完成项 + 打开面板按钮）。
    /// 委托态：hint 行显示委托文案，开关已禁用（SetDelegated）。
    /// </summary>
    public void UpdateRelay(RelayStatus? st)
    {
        _lastRelayStatus = st;
        var running = st is { Running: true };
        _btnRelayToggle.Text = running ? "关闭中转" : "启用中转";

        // 说明行（委托态 → 委托文案；未初始化且未运行 → 引导说明；互斥）
        var showHint = _delegated || (st is { Initialized: false } && !running);
        _lblRelayHint.Visible = showHint;
        if (_delegated)
        {
            _lblRelayHint.Text = _delegatedHint ?? "中转由后台服务运行（只读）；启停请用管理面板。";
        }
        else if (showHint)
        {
            _lblRelayHint.Text = "尚未初始化：点击「启用中转」自动生成配置并启动本地中转服务。";
        }

        // 引导态：运行中但存在启用且缺 key 的渠道 → 列待完成项 + 打开面板按钮
        var missing = running && st!.Providers.Any(p => p.Enabled && !p.HasKey);
        _btnRelayGuide.Visible = missing;

        // sticky pin 强制通道 id（渲染 [强制] 前缀与右键菜单勾选态）
        var forcedId = st?.Forced?.Id;

        var i = 0;
        if (running)
        {
            var rows = st!.Providers;
            for (; i < Math.Min(rows.Count, MaxRelayRows); i++)
            {
                var p = rows[i];
                var lbl = _lblRelayRows[i];
                lbl.Visible = true;
                if (p.Enabled && !p.HasKey)
                {
                    // 引导文案：缺 key 渠道列待完成项（m8：no-key 行不走 RelayRowText，
                    // 被 pin 的 no-key 渠道行无 [强制] 前缀属预期；总览横幅仍可见）
                    lbl.Text = $"  {p.Id}：请在 Provider 池填写 API Key";
                    lbl.ForeColor = Colors.Warning;
                    lbl.Font = RelayRowFont;
                }
                else
                {
                    var (text, color, isCurrent) = RelayRowText(p, st.CurrentId, forcedId);
                    lbl.Text = text;
                    lbl.ForeColor = color;
                    lbl.Font = isCurrent ? RelayRowCurrentFont : RelayRowFont;
                }
            }
            if (rows.Count > MaxRelayRows)
            {
                _lblRelayRows[^1].Visible = true;
                _lblRelayRows[^1].Text = $"  …共 {rows.Count} 个渠道";
                _lblRelayRows[^1].ForeColor = Colors.Muted;
                _lblRelayRows[^1].Font = RelayRowFont;
            }
        }
        // 隐藏多余行（F1 回归修复：补回改前基线循环）——中转停止后渠道 Label 残留旧文本、
        // 渠道减少/池收缩后多余行与 `…共 N 个渠道` 行残留均由该复位消除；
        // Row 高度委托 `Visible ? 20 : 0` 依赖此复位。
        for (; i < MaxRelayRows; i++)
        {
            _lblRelayRows[i].Visible = false;
        }
        Relayout();
    }

    /// <summary>
    /// 渠道行右键 → 上下文菜单：每渠道一项"强制路由到 {id}"（当前 pin 项打勾）+ 分隔 +
    /// "解除强制"（无 pin 时禁用）。选择经 PinRequested 事件外派（AppContext 订阅）。
    /// </summary>
    void ShowRelayRowMenu(Label row, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        if (_delegated) return; // 委托态禁 pin 菜单（pin 是 headless 进程内存态，UI 无 token 不代理写，§2.4）
        var st = _lastRelayStatus;
        if (st is not { Running: true }) return;
        var forcedId = st.Forced?.Id;
        var menu = new ContextMenuStrip();
        foreach (var p in st.Providers)
        {
            var id = p.Id;
            var item = new ToolStripMenuItem($"强制路由到 {id}")
            {
                Checked = id == forcedId,
            };
            item.Click += (_, _) => PinRequested?.Invoke(this, (id, true));
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var unpin = new ToolStripMenuItem("解除强制")
        {
            Enabled = forcedId != null,
        };
        unpin.Click += (_, _) => PinRequested?.Invoke(this, (forcedId!, false));
        menu.Items.Add(unpin);
        menu.Closed += (_, _) => menu.Dispose(); // F2：用后即弃，避免每次右键泄漏一个菜单
        menu.Show(row, e.Location);
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

        _btnPanel.Top = y;
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

    internal static string? Cumulative(ActivityInfo? a)
    {
        if (a?.TotalTokens is not long total) return null;
        var t = $"{ActivityParser.FmtTokens(total)} tokens";
        return a.DurationLabel != null ? $"{t} · {a.DurationLabel}" : t;
    }

    internal static string? TodayLine(ActivityInfo? a)
    {
        if (a?.TodayTokens == null && a?.PeakTokens == null) return null;
        var today = a?.TodayTokens != null ? ActivityParser.FmtTokens(a.TodayTokens.Value) : "?";
        var peak = a?.PeakTokens != null
            ? $" · 峰值 {ActivityParser.FmtTokens(a.PeakTokens.Value)}{(a?.PeakDate != null ? $" @ {a.PeakDate}" : "")}"
            : "";
        return $"{today}{peak}";
    }

    internal static string? StreakLine(ActivityInfo? a)
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

    /// <summary>配额窗口单行摘要（托盘悬停浮窗复用）。</summary>
    internal static string? LimitLine(LimitInfo? lim, string fallbackLabel)
    {
        if (lim == null) return null;
        var t = $"{lim.Label ?? fallbackLabel}  {lim.Remaining?.ToString(CultureInfo.InvariantCulture) ?? "?"}%";
        if (lim.Detail != null) t += $" · {lim.DetailLabel ?? "已用"} {lim.Detail}";
        if (lim.ResetLabel != null) t += $" · ↻ {lim.ResetLabel}";
        return t;
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

    /// <summary>真正退出程序（详情窗"退出"按钮与托盘菜单共用）。</summary>
    public void RequestExit()
    {
        _reallyExit = true;
        Close();
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
