using System.IO;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace PeripheralHub.App;

/// <summary>
/// Draws the app/tray icon: a purple→blue rounded badge with a white gamepad and a mouse in front of it.
/// Rendered per size (not scaled from one bitmap) so it stays crisp at 16 px.
/// </summary>
public static class IconFactory
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private static readonly Drawing.Color From = Drawing.Color.FromArgb(124, 77, 255);
    private static readonly Drawing.Color To = Drawing.Color.FromArgb(0, 176, 255);
    private const string IconFontPrimary = "Segoe Fluent Icons", IconFontFallback = "Segoe MDL2 Assets";
    private const char Gamepad = '', Mouse = '';

    /// <summary>A multi-resolution icon; Windows picks the right size for the tray and DPI.</summary>
    public static Drawing.Icon CreateIcon(int preferredSize) =>
        new(new MemoryStream(CreateIcoBytes()), preferredSize, preferredSize);

    public static byte[] CreateIcoBytes()
    {
        var images = Sizes.Select(s => { using var bmp = Render(s); using var ms = new MemoryStream(); bmp.Save(ms, Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);           // ICONDIR
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)                                         // ICONDIRENTRY (PNG-compressed)
        {
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i])); w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        return ico.ToArray();
    }

    /// <summary>Renders the badge at <paramref name="size"/> px. Geometry is defined on a 64-unit canvas.</summary>
    public static Drawing.Bitmap Render(int size)
    {
        var bmp = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 64f, size / 64f);

        var badge = new Drawing.RectangleF(1, 1, 62, 62);
        using var brush = new Drawing2D.LinearGradientBrush(badge, From, To, 45f);
        using (var path = RoundedRect(badge, 14)) g.FillPath(brush, path);

        using var family = FontFamilyOrFallback();
        using var white = new Drawing.SolidBrush(Drawing.Color.White);
        // Gamepad, slightly up and left.
        using (var p = GlyphPath(family, Gamepad, 38, new Drawing.PointF(2, 10))) g.FillPath(white, p);
        // Mouse in front, bottom-right, with a gradient "halo" so it reads separately from the gamepad.
        using (var mouse = GlyphPath(family, Mouse, 28, new Drawing.PointF(32, 27)))
        {
            using var halo = (Drawing2D.GraphicsPath)mouse.Clone();
            using var pen = new Drawing.Pen(brush, 9f) { LineJoin = Drawing2D.LineJoin.Round };
            halo.Widen(pen);
            g.FillPath(brush, halo);
            // Fill the mouse body with the gradient first so the halo covers the gamepad inside it too.
            var bounds = mouse.GetBounds();
            g.FillEllipse(brush, bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
            g.FillPath(white, mouse);
        }
        return bmp;
    }

    private static Drawing2D.GraphicsPath GlyphPath(Drawing.FontFamily family, char glyph, float emSize, Drawing.PointF origin)
    {
        var p = new Drawing2D.GraphicsPath();
        p.AddString(glyph.ToString(), family, (int)Drawing.FontStyle.Regular, emSize, origin, Drawing.StringFormat.GenericTypographic);
        return p;
    }

    private static Drawing.FontFamily FontFamilyOrFallback()
    {
        try { return new Drawing.FontFamily(IconFontPrimary); }
        catch (ArgumentException) { return new Drawing.FontFamily(IconFontFallback); }
    }

    private static Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
    {
        var p = new Drawing2D.GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Writes a preview sheet (all sizes) next to the .ico, for checking the artwork.</summary>
    public static void WriteFiles(string icoPath)
    {
        File.WriteAllBytes(icoPath, CreateIcoBytes());
        using var sheet = new Drawing.Bitmap(Sizes.Where(s => s <= 64).Sum() + 10 * Sizes.Length, 2 * 74);
        using var g = Drawing.Graphics.FromImage(sheet);
        g.FillRectangle(new Drawing.SolidBrush(Drawing.Color.FromArgb(32, 32, 32)), 0, 0, sheet.Width, 74);
        g.FillRectangle(new Drawing.SolidBrush(Drawing.Color.FromArgb(243, 243, 243)), 0, 74, sheet.Width, 74);
        foreach (int row in new[] { 0, 74 })
        {
            int x = 5;
            foreach (int s in Sizes.Where(s => s <= 64)) { using var b = Render(s); g.DrawImage(b, x, row + 5); x += s + 10; }
        }
        sheet.Save(Path.ChangeExtension(icoPath, ".preview.png"));
    }
}
