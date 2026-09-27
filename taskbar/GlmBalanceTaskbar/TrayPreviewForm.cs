using System.Drawing;
using System.Windows.Forms;

namespace GlmBalanceTaskbar;

/// <summary>
/// 托盘悬停摘要浮窗：无边框置顶、不抢焦点（WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW），
/// 多行显示 周/5h/活跃度 摘要；鼠标移出浮窗区域后自动隐藏。
/// </summary>
sealed class TrayPreviewForm : Form
{
    readonly Label _lblTitle;
    readonly Label _lblBody;
    readonly System.Windows.Forms.Timer _hideTimer;

    /// <summary>触发显示时的鼠标位置（托盘图标附近），作为浮窗的保持区锚点。</summary>
    Point _anchor;

    /// <summary>鼠标位于浮窗外扩此像素范围内、或锚点附近此像素范围内时，浮窗保持显示。</summary>
    const int KeepAliveMargin = 32;

    public TrayPreviewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Colors.Bg;
        Font = new Font("Segoe UI", 9F);

        _lblTitle = new Label
        {
            Location = new Point(14, 10),
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Colors.Text,
        };
        _lblBody = new Label
        {
            Location = new Point(14, 34),
            AutoSize = true,
            ForeColor = Colors.Text,
        };
        Controls.Add(_lblTitle);
        Controls.Add(_lblBody);

        _hideTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _hideTimer.Tick += (_, _) =>
        {
            // 保持区 = 浮窗外扩 KeepAliveMargin（覆盖图标与浮窗之间的间隙）∪ 锚点附近
            // （锚点 = 鼠标触发悬停时所在的托盘图标位置）
            var m = Cursor.Position;
            var keepZone = new Rectangle(Location - new Size(KeepAliveMargin, KeepAliveMargin), Size + new Size(KeepAliveMargin * 2, KeepAliveMargin * 2));
            var nearAnchor = Math.Abs(m.X - _anchor.X) <= KeepAliveMargin && Math.Abs(m.Y - _anchor.Y) <= KeepAliveMargin;
            if (nearAnchor) _anchor = m; // 鼠标在托盘图标附近移动时跟随锚点
            if (!keepZone.Contains(m) && !nearAnchor)
            {
                Hide();
                _hideTimer.Stop();
            }
        };
    }

    /// <summary>显示时不夺走当前应用焦点。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW（不进 Alt-Tab）
            return cp;
        }
    }

    /// <summary>显示摘要浮窗：anchor 为鼠标位置（托盘图标附近），自动防出屏；已显示时仅刷新内容。</summary>
    public void ShowPreview(string title, string body, Point anchor)
    {
        var reposition = !Visible;
        _anchor = anchor;
        _lblTitle.Text = title;
        _lblBody.Text = body;
        var bodySize = TextRenderer.MeasureText(body, _lblBody.Font);
        ClientSize = new Size(Math.Max(280, bodySize.Width + 28), _lblBody.Top + bodySize.Height + 12);

        if (!reposition)
        {
            _hideTimer.Start();
            return;
        }
        var wa = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X - Width / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 8));
        var y = anchor.Y - Height - 12;
        if (y < wa.Top + 8) y = anchor.Y + 24;
        Location = new Point(x, y);

        _hideTimer.Start();
        Show();
    }
}
