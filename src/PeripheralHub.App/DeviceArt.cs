using System.Globalization;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;

namespace PeripheralHub.App;

/// <summary>
/// Device pictures: the app's own art only (owner, 2026-10-29: no vendor pictures, one "Silver + brand" look, no icon
/// pack choice). Filled silver bodies with the purple-to-blue brand on the details, the same in both themes. Monitor,
/// keyboard, mouse, headset and earbuds are the drawings in GenericPictures.xaml; controllers are the CC0 Gamepad Line
/// Art Pack's drawings (Art\README.md) filled the same way (<see cref="Silver"/>).
/// <para>
/// Only the SVG subset the pack uses is read: &lt;g&gt;, &lt;path&gt;, &lt;circle&gt;, &lt;ellipse&gt;, &lt;rect&gt;;
/// translate/scale transforms; fill (currentColor or none) and stroke-width.
/// </para>
/// </summary>
public static class DeviceArt
{
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    /// <summary>The picture for a device: its model's when there is one (controllers), else its kind's.</summary>
    public static ImageSource? For(DeviceSnapshot s) => s.Model switch
    {
        "dualsense" => Silver("dualsense"),
        "dualsense-edge" => Silver("dualsense-edge"),
        "dualshock4" => Silver("dualshock-4"),
        "xbox" => Silver("xbox-series"),
        _ => s.Kind switch
        {
            DeviceKind.Monitor => Filled("Generic.Monitor"),
            DeviceKind.Keyboard => Filled("Generic.Keyboard"),
            DeviceKind.Mouse => Filled("Generic.Mouse"),
            DeviceKind.Audio => Filled(DevicePictures.IsEarbuds(s.Name, s.Parts) ? "Generic.Earbuds" : "Generic.Audio"),
            DeviceKind.Controller => Silver("xbox-series"),
            _ => null,
        },
    };

    private static System.Windows.ResourceDictionary? _filled;

    /// <summary>A drawing from GenericPictures.xaml.</summary>
    private static ImageSource? Filled(string key)
    {
        try
        {
            _filled ??= new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/GenericPictures.xaml") };
            return _filled[key] as ImageSource;
        }
        catch (Exception ex) { AppLog.Write($"Picture {key} couldn't be loaded: {ex.Message}"); return null; }
    }

    /// <summary>Every picture, for the preview: a GenericPictures.xaml key or a controller from Art\.</summary>
    private static readonly string[] Previewed =
        ["Generic.Monitor", "Generic.Keyboard", "Generic.Mouse", "Generic.Audio", "Generic.Earbuds", "dualsense", "dualsense-edge", "dualshock-4", "xbox-series"];

    /// <summary>
    /// Developer helper (--write-art &lt;png&gt;): every picture on a dark and a light tile, large and at the panel's
    /// size, drawn by the real renderer, to check art without installing.
    /// </summary>
    public static void WritePreview(string path)
    {
        const int tileW = 300, tileH = 160;
        static ImageSource? Get(string n) => n.StartsWith("Generic.", StringComparison.Ordinal) ? Filled(n) : Silver(n);
        var panel = new System.Windows.Controls.Primitives.UniformGrid { Columns = Previewed.Length, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) };
        foreach (bool dark in new[] { true, false })
            foreach (var n in Previewed)
            {
                var cell = new System.Windows.Controls.StackPanel { Margin = new Thickness(6) };
                var bg = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
                cell.Children.Add(new System.Windows.Controls.Border
                {
                    Background = bg, Width = tileW, Height = tileH, CornerRadius = new CornerRadius(8),
                    Child = new System.Windows.Controls.Image { Source = Get(n), Margin = new Thickness(20), Stretch = Stretch.Uniform },
                });
                cell.Children.Add(new System.Windows.Controls.Border
                {
                    Background = bg, Width = 70, Height = 50, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new System.Windows.Controls.Image { Source = Get(n), Width = 54, Height = 38, Stretch = Stretch.Uniform },
                });
                panel.Children.Add(cell);
            }
        var size = new Size(Previewed.Length * (tileW + 12), 2 * (tileH + 68));
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(panel);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var file = System.IO.File.Create(path);
        png.Save(file);
    }

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private sealed record Part(Geometry Geometry, bool Filled, double StrokeWidth);

    /// <summary>
    /// A controller from the pack, filled in the "Silver + brand" style (owner, 2026-10-29: take the line art's
    /// structure and fill it like the other silver drawings). The outline's outer edge becomes a silver body; the
    /// touchpad gets a light fill, the sticks a dark one, face and home buttons and the D-pad the brand gradient; the
    /// original lines go on top in slate (light on the sticks, white on brand parts, brand for the small details).
    /// </summary>
    public static ImageSource? Silver(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            ImageSource? image = null;
            try
            {
                using var stream = typeof(DeviceArt).Assembly.GetManifestResourceStream($"PeripheralHub.App.Art.{name}.svg");
                if (stream is not null)
                {
                    var (parts, frame) = Read(XDocument.Load(stream));
                    image = FillFromLines(parts, frame);
                }
            }
            catch (Exception ex) { AppLog.Write($"Picture {name} couldn't be drawn: {ex.Message}"); }
            return Cache[name] = image;
        }
    }
    private static Rect BoundsOf(PathFigure f) => new PathGeometry([f.Clone()]).Bounds;

    private static DrawingImage FillFromLines(List<Part> parts, Rect frame)
    {
        // Round = fills most of its box like a circle (0.785); a D-pad cross or a symbol fills much less.
        var figures = parts.SelectMany(p => ((PathGeometry)p.Geometry).Figures.Select(f =>
        {
            var g = new PathGeometry([f.Clone()]);
            var b = g.Bounds;
            return (Figure: f, Bounds: b, Round: g.GetArea() / Math.Max(1, b.Width * b.Height) > 0.7);
        })).ToList();
        var body = figures.MaxBy(f => f.Bounds.Width * f.Bounds.Height);
        var s = body.Bounds;
        double cx = s.X + s.Width / 2;
        static double Aspect(Rect r) => r.Width / Math.Max(1, r.Height);
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

        // The biggest figure at each spot (rings have an outer and an inner edge; nested circles several).
        List<Rect> Distinct(IEnumerable<Rect> rects) => rects.OrderByDescending(r => r.Width * r.Height)
            .Aggregate(new List<Rect>(), (kept, r) =>
            {
                if (!kept.Any(k => (Center(k) - Center(r)).Length < s.Width * 0.03)) kept.Add(r);
                return kept;
            });

        var pad = figures.Where(f => f.Bounds.Width > s.Width * 0.16 && f.Bounds.Width < s.Width * 0.34 &&
                                     f.Bounds.Height > s.Height * 0.12 && f.Bounds.Height < s.Height * 0.4 &&
                                     Math.Abs(Center(f.Bounds).X - cx) < s.Width * 0.06 && Center(f.Bounds).Y < s.Y + s.Height * 0.45)
                         .Select(f => f.Bounds).OrderByDescending(r => r.Width * r.Height).Take(1).ToList();
        // Sticks: round, stick-sized, with several rings around the same centre (cap, rim, base). A D-pad's round base
        // (Xbox) or a ring around the face buttons (DualShock 4) is just one ring.
        int RingsAround(Rect r) => figures.Count(f => f.Round && (Center(f.Bounds) - Center(r)).Length < s.Width * 0.02 &&
                                                       f.Bounds.Width > s.Width * 0.05);
        var sticks = Distinct(figures.Where(f => f.Round && Aspect(f.Bounds) is > 0.85 and < 1.18 &&
                                                 f.Bounds.Width > s.Width * 0.07 && f.Bounds.Width < s.Width * 0.17 &&
                                                 RingsAround(f.Bounds) >= 4).Select(f => f.Bounds)).Take(2).ToList();
        // Face buttons (right side) and the home button (PS / Xbox guide, in the middle; the Xbox one is bigger).
        var buttons = Distinct(figures.Where(f => f.Round && Aspect(f.Bounds) is > 0.8 and < 1.25 && f.Bounds.Width > s.Width * 0.028 &&
                                                  ((f.Bounds.Width < s.Width * 0.075 && Center(f.Bounds).X > cx + s.Width * 0.18 &&
                                                    Center(f.Bounds).Y < s.Y + s.Height * 0.6) ||
                                                   (f.Bounds.Width < s.Width * 0.1 && Math.Abs(Center(f.Bounds).X - cx) < s.Width * 0.04)))
                                      .Select(f => f.Bounds)
                                      .Where(r => !sticks.Any(k => k.Contains(Center(r))))).ToList();
        // D-pad (owner, 2026-10-29: brand-filled like the face buttons): PlayStation's four arrow keys up-left of the
        // sticks, or the Xbox's one cross (not round, lower left). Filled with their own outline, not a circle.
        var dpad = figures.Where(f => (Aspect(f.Bounds) is > 0.7 and < 1.4 && f.Bounds.Width > s.Width * 0.035 && f.Bounds.Width < s.Width * 0.08 &&
                                       Center(f.Bounds).X < cx - s.Width * 0.18 && Center(f.Bounds).Y < s.Y + s.Height * 0.6) ||
                                      (!f.Round && Aspect(f.Bounds) is > 0.85 and < 1.18 && f.Bounds.Width > s.Width * 0.09 &&
                                       f.Bounds.Width < s.Width * 0.15 && Center(f.Bounds).X < cx - s.Width * 0.05))
                          .Where(f => !sticks.Any(k => k.Contains(Center(f.Bounds))))
                          .OrderByDescending(f => f.Bounds.Width * f.Bounds.Height)
                          .Aggregate(new List<(PathFigure Figure, Rect Bounds, bool Round)>(), (kept, f) =>
                          {
                              if (!kept.Any(k => (Center(k.Bounds) - Center(f.Bounds)).Length < s.Width * 0.03)) kept.Add(f);
                              return kept;
                          });

        var silver = new LinearGradientBrush(Color.FromRgb(0xF4, 0xF6, 0xFA), Color.FromRgb(0xA9, 0xB0, 0xBD), s.TopLeft, s.BottomLeft)
            { MappingMode = BrushMappingMode.Absolute };
        var padFill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD2, 0xD7, 0xE0), 90);
        var darkFill = new RadialGradientBrush(Color.FromRgb(0x3A, 0x3F, 0x4B), Color.FromRgb(0x14, 0x16, 0x1B)) { GradientOrigin = new Point(0.4, 0.35) };
        var brand = new LinearGradientBrush(Color.FromRgb(0xA4, 0x7B, 0xFF), Color.FromRgb(0x3F, 0x8C, 0xFF), frame.TopLeft, frame.BottomRight)
            { MappingMode = BrushMappingMode.Absolute };
        var slate = new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x5C));
        var onDark = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAD));

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(frame)));
        group.Children.Add(new GeometryDrawing(silver, null, new PathGeometry([body.Figure.Clone()])));
        var regions = new List<(Rect Area, Brush Line)>();
        void FillAt(Rect r, Brush fill, Brush line, bool round)
        {
            Geometry g = round ? new EllipseGeometry(r) : new RectangleGeometry(r, r.Height * 0.12, r.Height * 0.12);
            group.Children.Add(new GeometryDrawing(fill, null, g));
            regions.Add((r, line));
        }
        foreach (var r in pad) FillAt(r, padFill, slate, round: false);
        foreach (var r in sticks) FillAt(r, darkFill, onDark, round: true);
        foreach (var r in buttons) FillAt(r, brand, Brushes.White, round: true);
        if (dpad.Count > 0)
        {
            // Its outlines and arrows go white (a separate brush, so the "brand rim" rule for round buttons doesn't apply):
            // the pieces stay apart instead of merging into one purple blob.
            var onDpad = new SolidColorBrush(Colors.White);
            foreach (var d in dpad)
            {
                group.Children.Add(new GeometryDrawing(brand, null, new PathGeometry([d.Figure.Clone()])));
                regions.Add((d.Bounds, onDpad));
            }
            var zone = dpad.Select(d => d.Bounds).Aggregate(Rect.Union);
            regions.Add((zone, onDpad));
        }

        double small = frame.Width * frame.Height * 0.012;
        foreach (var p in parts)
        {
            var b = p.Geometry.Bounds;
            var region = regions.Where(x => Rect.Inflate(x.Area, x.Area.Width * 0.08, x.Area.Height * 0.08).Contains(b))
                                .OrderBy(x => x.Area.Width * x.Area.Height).Select(x => ((Rect, Brush)?)x).FirstOrDefault();
            // A brand button's own rim stays brand (a solid disc at tile size); only its symbol turns white.
            Brush? inside = region is var (area, onRegion) && !(onRegion == Brushes.White && b.Width > area.Width * 0.8) ? onRegion
                          : region is not null ? brand : null;
            Brush brush = inside ?? (b.Width * b.Height < small ? brand : slate);
            var pen = new Pen(brush, p.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            group.Children.Add(new GeometryDrawing(p.Filled ? brush : null, pen, p.Geometry));
        }
        group.ClipGeometry = new RectangleGeometry(frame);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static (List<Part> Parts, Rect Frame) Read(XDocument doc)
    {
        var root = doc.Root!;
        var vb = Numbers(root.Attribute("viewBox")!.Value);
        var parts = new List<Part>();
        Walk(root, Matrix.Identity, filled: false, strokeWidth: 1, parts);
        return (parts, new Rect(vb[0], vb[1], vb[2], vb[3]));
    }

    private static void Walk(XElement element, Matrix parent, bool filled, double strokeWidth, List<Part> parts)
    {
        foreach (var e in element.Elements())
        {
            var m = Transform(e.Attribute("transform")?.Value) * parent;
            bool fill = e.Attribute("fill")?.Value is { } f ? f != "none" : filled;
            double sw = e.Attribute("stroke-width")?.Value is { } w ? double.Parse(w, CultureInfo.InvariantCulture) : strokeWidth;
            if (e.Name == Svg + "g") { Walk(e, m, fill, sw, parts); continue; }

            Geometry? g = e.Name.LocalName switch
            {
                "path" => Geometry.Parse("F1 " + e.Attribute("d")!.Value).Clone(),
                "circle" => new EllipseGeometry(new Point(N(e, "cx"), N(e, "cy")), N(e, "r"), N(e, "r")),
                "ellipse" => new EllipseGeometry(new Point(N(e, "cx"), N(e, "cy")), N(e, "rx"), N(e, "ry")),
                "rect" => new RectangleGeometry(new Rect(N(e, "x"), N(e, "y"), N(e, "width"), N(e, "height")), N(e, "rx"), N(e, "rx")),
                _ => null,
            };
            if (g is null) continue;
            g.Transform = new MatrixTransform(m);
            // The pen draws in picture coordinates, so its width takes the element's scale.
            double scale = Math.Sqrt(Math.Abs(m.Determinant));
            var flat = g.GetFlattenedPathGeometry(); // fixes the transform in, so bounds and pens behave
            parts.Add(new Part(flat, fill, sw * scale));
        }
    }

    private static double N(XElement e, string name) =>
        e.Attribute(name)?.Value is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : 0;

    private static double[] Numbers(string s) =>
        s.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>"translate(10 20) scale(-1 1)" → one matrix (applied right to left, as in SVG).</summary>
    private static Matrix Transform(string? value)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(value)) return result;
        var steps = System.Text.RegularExpressions.Regex.Matches(value, @"(\w+)\s*\(([^)]*)\)").ToList();
        steps.Reverse();
        foreach (var step in steps)
        {
            var a = Numbers(step.Groups[2].Value);
            var m = Matrix.Identity;
            switch (step.Groups[1].Value)
            {
                case "translate": m.Translate(a[0], a.Length > 1 ? a[1] : 0); break;
                case "scale": m.Scale(a[0], a.Length > 1 ? a[1] : a[0]); break;
            }
            result *= m;
        }
        return result;
    }
}
