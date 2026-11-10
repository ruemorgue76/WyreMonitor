using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WyreMonitor.Desktop;

/// <summary>
/// The app's icon, a chip on the board, drawn in the board theme's colours: the window's title
/// bar and taskbar button, the small window and the icon next to the clock follow the theme.
/// The exe's own icon (app.ico, used by Start menu and desktop shortcuts) stays green.
/// </summary>
internal static class AppIcon
{
    private static readonly Dictionary<string, Icon> Cache = new();

    // One icon per theme for the whole run: never disposed, there are at most four.
    public static Icon For(string? theme)
    {
        var key = theme is "black" or "blue" or "white" ? theme : "green";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var icon)) Cache[key] = icon = Build(key);
            return icon;
        }
    }

    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    // A multi-size .ico in memory, so Windows picks a sharp image for each place it shows it.
    private static Icon Build(string theme)
    {
        var images = Sizes.Select(size => (size, png: Png(theme, size))).ToList();
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
            var offset = 6 + 16 * images.Count;
            foreach (var (size, png) in images)
            {
                w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write(png.Length); w.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in images) w.Write(png);
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    // Drawn on a 32-unit grid, edges rounded to whole pixels so the small sizes stay crisp.
    private static byte[] Png(string theme, int size)
    {
        var t = Themes.For(theme);
        var light = theme == "white";
        // The page's colours: gold parts on the dark boards; on the white board the parts are
        // black, as in the page, with gold pins.
        var board = t.Board;
        var chip = light ? ColorTranslator.FromHtml("#1d2224") : ColorTranslator.FromHtml("#d8b45a");
        var pins = light ? ColorTranslator.FromHtml("#9a7318") : chip;

        float u = size / 32f;
        int P(float units) => (int)Math.Round(units * u);
        Rectangle R(float x, float y, float w, float h) => Rectangle.FromLTRB(P(x), P(y), Math.Max(P(x + w), P(x) + 1), Math.Max(P(y + h), P(y) + 1));

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(new RectangleF(0, 0, size, size), 3 * u))
            using (var fill = new SolidBrush(board))
            {
                g.FillPath(fill, path);
                if (light)
                {
                    // A thin outline, or the light board would vanish on a light title bar.
                    var width = Math.Max(1f, u);
                    using var edge = new Pen(t.Edge, width);
                    using var inner = Rounded(new RectangleF(width / 2, width / 2, size - width, size - width), 3 * u - width / 2);
                    g.DrawPath(edge, inner);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(chip)) g.FillRectangle(b, R(9, 9, 14, 14));
            using (var b = new SolidBrush(pins))
            {
                foreach (var c in new[] { 11f, 15f, 19f })
                {
                    g.FillRectangle(b, R(c, 5, 2, 3));    // top
                    g.FillRectangle(b, R(c, 24, 2, 3));   // bottom
                    g.FillRectangle(b, R(5, c, 3, 2));    // left
                    g.FillRectangle(b, R(24, c, 3, 2));   // right
                }
            }
        }
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Max(1f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
