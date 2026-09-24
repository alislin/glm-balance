using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GlmBalanceTaskbar;

/// <summary>状态色与面板主题色（阈值与插件 colorFor 一致：绿 &gt;40 / 黄 15–40 / 红 &lt;15）。</summary>
public static class Colors
{
    public static readonly Color Success = Color.FromArgb(34, 197, 94);
    public static readonly Color Warning = Color.FromArgb(234, 179, 8);
    public static readonly Color Error = Color.FromArgb(239, 68, 68);

    // 详情面板浅色主题
    public static readonly Color Bg = Color.White;
    public static readonly Color Text = Color.FromArgb(32, 33, 36);
    public static readonly Color Muted = Color.FromArgb(110, 114, 122);
    public static readonly Color Track = Color.FromArgb(233, 236, 240);
    /// <summary>柱状图历史天（浅绿）</summary>
    public static readonly Color BarPast = Color.FromArgb(155, 216, 177);
    /// <summary>柱状图悬停加深</summary>
    public static readonly Color SuccessDark = Color.FromArgb(21, 128, 61);
    public static readonly Color BarPastHover = Color.FromArgb(110, 191, 148);

    public static Color ForRemaining(int? remaining) =>
        remaining == null ? Warning
        : remaining > 40 ? Success
        : remaining > 15 ? Warning
        : Error;
}

/// <summary>
/// 把短文本（如 "36"）渲染成多分辨率 Icon：对 16/20/24/32/48 每个尺寸独立渲染文字帧
/// （自适应字号 + 按尺寸缩放的黑描边），避免单帧图标被系统最近邻缩放吞掉笔画。
/// </summary>
public static class IconRenderer
{
    /// <summary>ICO 各帧尺寸：标题栏 16、任务栏 24/32、Alt-Tab/大图标 48。</summary>
    static readonly int[] FrameSizes = { 16, 20, 24, 32, 48 };

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>渲染多分辨率文字图标。</summary>
    public static Icon RenderTextIcon(string text, Color color)
    {
        byte[] ico;
        using (var ms = new MemoryStream())
        {
            using var w = new BinaryWriter(ms);
            w.Write((ushort)0);                 // reserved
            w.Write((ushort)1);                 // type: icon
            w.Write((ushort)FrameSizes.Length); // frame count

            int offset = 6 + 16 * FrameSizes.Length;
            foreach (var size in FrameSizes)
            {
                var png = RenderPng(text, color, size);
                w.Write((byte)(size >= 256 ? 0 : size)); // width
                w.Write((byte)(size >= 256 ? 0 : size)); // height
                w.Write((byte)0);   // palette
                w.Write((byte)0);   // reserved
                w.Write((ushort)1); // planes
                w.Write((ushort)32);// bpp
                w.Write((uint)png.Length);
                w.Write((uint)offset);
                offset += png.Length;
            }
            foreach (var size in FrameSizes)
            {
                w.Write(RenderPng(text, color, size));
            }
            w.Flush();
            ico = ms.ToArray();
        }
        return new Icon(new MemoryStream(ico));
    }

    /// <summary>诊断：把每个尺寸的渲染帧 PNG 直接写到目录（不经 ICO 打包，用于排查渲染内容）。</summary>
    public static void DumpFrames(string text, Color color, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var size in FrameSizes)
        {
            var png = RenderPng(text, color, size);
            File.WriteAllBytes(Path.Combine(dir, $"glmbalance-icon-{size}.png"), png);
        }
    }

    /// <summary>渲染单尺寸帧并输出 PNG 字节（透明底 + 黑描边彩色粗体文字，字号自适应）。
    /// 用 DrawString 而非 GraphicsPath.AddString：后者在本机对多字符只生成首字符路径（实测 bug）。</summary>
    static byte[] RenderPng(string text, Color color, int size)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var family = new FontFamily("Segoe UI");
        var fmt = (StringFormat)StringFormat.GenericDefault.Clone();
        fmt.Alignment = StringAlignment.Center;
        fmt.LineAlignment = StringAlignment.Center;

        // 字号自适应：从大往小缩，直到 MeasureString 完全放进画布
        var em = size * 0.80f;
        var font = new Font(family, em, FontStyle.Bold, GraphicsUnit.Pixel);
        while (true)
        {
            var measured = g.MeasureString(text, font, int.MaxValue, fmt);
            if ((measured.Width <= size - 2 && measured.Height <= size - 2) || em <= size * 0.30f) break;
            em -= 1f;
            font.Dispose();
            font = new Font(family, em, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        // 描边随尺寸缩放：小尺寸用细描边，避免吞掉笔画
        var outline = Math.Min(3f, Math.Max(1.5f, size / 12f));
        var rect = new RectangleF(0, 0, size, size);
        using (var black = new SolidBrush(Color.Black))
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    g.DrawString(text, font, black, new RectangleF(dx * outline * 0.5f, dy * outline * 0.5f, size, size), fmt);
                }
            }
        }
        using (var brush = new SolidBrush(color))
        {
            g.DrawString(text, font, brush, rect, fmt);
        }
        font.Dispose();

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}
