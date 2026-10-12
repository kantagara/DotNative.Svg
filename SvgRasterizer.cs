using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotNative;

namespace DotNative.Svg;

internal static class SvgRasterizer
{
    internal static byte[] Png(
        ReadOnlySpan<byte> document,
        float width,
        float height,
        Color? color
    )
    {
        var bitmap = Rasterize(document, width, height);
        if (color is { } tint)
            Tint(bitmap, tint);
        return EncodePng(bitmap);
    }

    internal static Bitmap Rasterize(ReadOnlySpan<byte> document, float width, float height)
    {
        if (document.IsEmpty || document.Length > 1024 * 1024)
            throw new ArgumentException("SVG document must be between 1 byte and 1 MiB.", nameof(document));
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0 || width > 1024 || height > 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "SVG size must be between 0 and 1024 points.");
        var root = XDocument.Parse(Encoding.UTF8.GetString(document)).Root
            ?? throw new InvalidDataException("SVG document is empty.");
        if (root.Name.LocalName != "svg")
            throw new InvalidDataException("SVG document must start with an svg element.");
        var (minX, minY, viewWidth, viewHeight) = ViewBox(root);
        int pixelWidth = Math.Clamp((int)MathF.Ceiling(width * 4), 1, 2048);
        int pixelHeight = Math.Clamp((int)MathF.Ceiling(height * 4), 1, 2048);
        var bitmap = new Bitmap(pixelWidth, pixelHeight);
        var rootMatrix = Matrix
            .Scale(pixelWidth / viewWidth, pixelHeight / viewHeight)
            .Multiply(Matrix.Translate(-minX, -minY));
        var clips = new Dictionary<string, List<Shape>>(StringComparer.Ordinal);
        CollectClips(root, clips);
        DrawChildren(root, Style.Initial.Apply(root), rootMatrix, null, clips, bitmap);
        return bitmap;
    }

    private static void Tint(Bitmap bitmap, Color color)
    {
        var pixels = bitmap.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
                continue;
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = (byte)(pixels[i + 3] * color.A / 255);
        }
    }

    private static (float MinX, float MinY, float Width, float Height) ViewBox(XElement svg)
    {
        var box = Attr(svg, "viewBox");
        if (box is not null)
        {
            var parts = box.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
                throw new InvalidDataException("SVG viewBox must contain four numbers.");
            return (Num(parts[0]), Num(parts[1]), Positive(Num(parts[2])), Positive(Num(parts[3])));
        }
        var width = Length(Attr(svg, "width"));
        var height = Length(Attr(svg, "height"));
        if (width is null || height is null)
            throw new InvalidDataException("SVG requires a viewBox or a width and height.");
        return (0, 0, Positive(width.Value), Positive(height.Value));
    }

    private static void CollectClips(XElement element, Dictionary<string, List<Shape>> clips)
    {
        if (element.Name.LocalName == "clipPath" && Attr(element, "id") is { } id)
        {
            var shapes = new List<Shape>();
            foreach (var child in element.Elements())
                CollectGeometry(child, Matrix.Identity, shapes);
            clips[id] = shapes;
            return;
        }
        foreach (var child in element.Elements())
            CollectClips(child, clips);
    }

    private static void CollectGeometry(XElement element, Matrix parent, List<Shape> shapes)
    {
        var matrix = parent.Multiply(Transform(Attr(element, "transform")));
        if (element.Name.LocalName == "g")
        {
            foreach (var child in element.Elements())
                CollectGeometry(child, matrix, shapes);
            return;
        }
        var contours = Geometry(element);
        if (contours.Count == 0)
            return;
        shapes.Add(new Shape(contours, matrix));
    }

    private static void DrawChildren(
        XElement parent,
        Style style,
        Matrix matrix,
        byte[]? clip,
        Dictionary<string, List<Shape>> clips,
        Bitmap bitmap
    )
    {
        foreach (var child in parent.Elements())
            Draw(child, style, matrix, clip, clips, bitmap);
    }

    private static void Draw(
        XElement element,
        Style inherited,
        Matrix parent,
        byte[]? clip,
        Dictionary<string, List<Shape>> clips,
        Bitmap bitmap
    )
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "clipPath" or "title" or "desc" or "metadata")
            return;
        var style = inherited.Apply(element);
        var matrix = parent.Multiply(Transform(Attr(element, "transform")));
        if (ClipId(Attr(element, "clip-path")) is { } id)
        {
            if (!clips.TryGetValue(id, out var shapes))
                throw new InvalidDataException("SVG clip path is missing: " + id);
            clip = bitmap.Intersect(clip, Mask(bitmap, shapes, matrix));
        }
        if (name is "g" or "a" or "svg")
        {
            DrawChildren(element, style, matrix, clip, clips, bitmap);
            return;
        }
        var contours = Geometry(element);
        if (contours.Count == 0)
            return;
        var mapped = Map(contours, matrix);
        if (style.Fill is { } fill)
            bitmap.Fill(mapped, fill.Scale(style.Opacity), style.EvenOdd, clip);
        if (style.Stroke is { } stroke && style.StrokeWidth > 0)
            bitmap.Stroke(
                mapped,
                stroke.Scale(style.Opacity),
                PixelWidth(matrix, style.StrokeWidth),
                clip
            );
    }

    private static byte[] Mask(Bitmap bitmap, List<Shape> shapes, Matrix parent)
    {
        var mask = new byte[bitmap.Width * bitmap.Height];
        foreach (var shape in shapes)
        {
            var mapped = Map(shape.Contours, parent.Multiply(shape.Matrix));
            bitmap.Cover(mapped, mask);
        }
        return mask;
    }

    private static List<Pt[]> Map(List<Contour> contours, Matrix matrix)
    {
        var mapped = new List<Pt[]>(contours.Count);
        foreach (var contour in contours)
        {
            var points = new Pt[contour.Points.Count];
            for (var i = 0; i < points.Length; i++)
                points[i] = matrix.Apply(contour.Points[i]);
            mapped.Add(points);
        }
        return mapped;
    }

    private static float PixelWidth(Matrix matrix, float width)
    {
        var sx = Hypot(matrix.A, matrix.B);
        var sy = Hypot(matrix.C, matrix.D);
        return width * (sx + sy) / 2;
    }

    private static List<Contour> Geometry(XElement element) =>
        element.Name.LocalName switch
        {
            "path" => PathOf(Attr(element, "d") ?? ""),
            "circle" => [Ellipse(Num(Attr(element, "cx")), Num(Attr(element, "cy")), Length(Attr(element, "r")) ?? 0, Length(Attr(element, "r")) ?? 0)],
            "ellipse" => [Ellipse(Num(Attr(element, "cx")), Num(Attr(element, "cy")), Length(Attr(element, "rx")) ?? 0, Length(Attr(element, "ry")) ?? 0)],
            "rect" => [Rectangle(Num(Attr(element, "x")), Num(Attr(element, "y")), Length(Attr(element, "width")) ?? 0, Length(Attr(element, "height")) ?? 0)],
            "line" => [Line(Num(Attr(element, "x1")), Num(Attr(element, "y1")), Num(Attr(element, "x2")), Num(Attr(element, "y2")))],
            "polyline" => [Poly(Attr(element, "points"), false)],
            "polygon" => [Poly(Attr(element, "points"), true)],
            _ => [],
        };

    private static Contour Ellipse(float cx, float cy, float rx, float ry)
    {
        var contour = new Contour();
        if (rx <= 0 || ry <= 0)
            return contour;
        const int steps = 64;
        for (var i = 0; i <= steps; i++)
        {
            var angle = i * MathF.Tau / steps;
            contour.Points.Add(new Pt(cx + rx * MathF.Cos(angle), cy + ry * MathF.Sin(angle)));
        }
        return contour;
    }

    private static Contour Rectangle(float x, float y, float width, float height)
    {
        var contour = new Contour();
        if (width <= 0 || height <= 0)
            return contour;
        contour.Points.Add(new Pt(x, y));
        contour.Points.Add(new Pt(x + width, y));
        contour.Points.Add(new Pt(x + width, y + height));
        contour.Points.Add(new Pt(x, y + height));
        contour.Points.Add(new Pt(x, y));
        return contour;
    }

    private static Contour Line(float x1, float y1, float x2, float y2)
    {
        var contour = new Contour();
        contour.Points.Add(new Pt(x1, y1));
        contour.Points.Add(new Pt(x2, y2));
        return contour;
    }

    private static Contour Poly(string? points, bool close)
    {
        var contour = new Contour();
        if (string.IsNullOrWhiteSpace(points))
            return contour;
        var scanner = new Scanner(points.Replace(',', ' '));
        while (scanner.HasNumber())
            contour.Points.Add(new Pt(scanner.Number(), scanner.Number()));
        if (close && contour.Points.Count > 0)
            contour.Points.Add(contour.Points[0]);
        return contour;
    }

    private static List<Contour> PathOf(string data)
    {
        var contours = new List<Contour>();
        var scanner = new Scanner(data);
        Contour? current = null;
        var command = '\0';
        float cx = 0, cy = 0, sx = 0, sy = 0, controlX = 0, controlY = 0;
        var steps = 0;
        while (scanner.HasMore())
        {
            if (++steps > data.Length)
                throw new InvalidDataException("SVG path did not terminate.");
            if (scanner.IsCommand())
                command = scanner.Command();
            else if (command is 'M')
                command = 'L';
            else if (command is 'm')
                command = 'l';
            else if (command is 'Z' or 'z' or '\0')
                throw new InvalidDataException("SVG path is missing a command.");
            switch (command)
            {
                case 'M':
                    cx = scanner.Number();
                    cy = scanner.Number();
                    current = Start(contours, cx, cy);
                    sx = cx;
                    sy = cy;
                    command = 'M';
                    break;
                case 'm':
                    cx += scanner.Number();
                    cy += scanner.Number();
                    current = Start(contours, cx, cy);
                    sx = cx;
                    sy = cy;
                    command = 'm';
                    break;
                case 'L':
                    cx = scanner.Number();
                    cy = scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'l':
                    cx += scanner.Number();
                    cy += scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'H':
                    cx = scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'h':
                    cx += scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'V':
                    cy = scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'v':
                    cy += scanner.Number();
                    current!.Points.Add(new Pt(cx, cy));
                    break;
                case 'C':
                    Curve(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, false);
                    break;
                case 'c':
                    Curve(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, true);
                    break;
                case 'S':
                    Smooth(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, false, command);
                    break;
                case 's':
                    Smooth(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, true, command);
                    break;
                case 'Q':
                    Quadratic(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, false);
                    break;
                case 'q':
                    Quadratic(current!, ref scanner, ref cx, ref cy, ref controlX, ref controlY, true);
                    break;
                case 'T':
                    SmoothQuadratic(
                        current!,
                        ref scanner,
                        ref cx,
                        ref cy,
                        ref controlX,
                        ref controlY,
                        false,
                        command
                    );
                    break;
                case 't':
                    SmoothQuadratic(
                        current!,
                        ref scanner,
                        ref cx,
                        ref cy,
                        ref controlX,
                        ref controlY,
                        true,
                        command
                    );
                    break;
                case 'A':
                    Arc(current!, ref scanner, ref cx, ref cy, false);
                    break;
                case 'a':
                    Arc(current!, ref scanner, ref cx, ref cy, true);
                    break;
                case 'Z':
                case 'z':
                    if (current is { Points.Count: > 0 })
                        current.Points.Add(new Pt(sx, sy));
                    cx = sx;
                    cy = sy;
                    current = null;
                    break;
                default:
                    throw new InvalidDataException("Unsupported SVG path command.");
            }
            if (command is not ('C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't'))
            {
                controlX = cx;
                controlY = cy;
            }
        }
        return contours;
    }

    private static Contour Start(List<Contour> contours, float x, float y)
    {
        var contour = new Contour();
        contour.Points.Add(new Pt(x, y));
        contours.Add(contour);
        return contour;
    }

    private static void Curve(
        Contour contour,
        ref Scanner scanner,
        ref float cx,
        ref float cy,
        ref float controlX,
        ref float controlY,
        bool relative
    )
    {
        var x1 = scanner.Number();
        var y1 = scanner.Number();
        var x2 = scanner.Number();
        var y2 = scanner.Number();
        var x = scanner.Number();
        var y = scanner.Number();
        if (relative)
        {
            x1 += cx;
            y1 += cy;
            x2 += cx;
            y2 += cy;
            x += cx;
            y += cy;
        }
        Cubic(contour, cx, cy, x1, y1, x2, y2, x, y);
        cx = x;
        cy = y;
        controlX = x2;
        controlY = y2;
    }

    private static void Smooth(
        Contour contour,
        ref Scanner scanner,
        ref float cx,
        ref float cy,
        ref float controlX,
        ref float controlY,
        bool relative,
        char previous
    )
    {
        var x1 = previous is 'C' or 'c' or 'S' or 's' ? 2 * cx - controlX : cx;
        var y1 = previous is 'C' or 'c' or 'S' or 's' ? 2 * cy - controlY : cy;
        var x2 = scanner.Number();
        var y2 = scanner.Number();
        var x = scanner.Number();
        var y = scanner.Number();
        if (relative)
        {
            x2 += cx;
            y2 += cy;
            x += cx;
            y += cy;
        }
        Cubic(contour, cx, cy, x1, y1, x2, y2, x, y);
        cx = x;
        cy = y;
        controlX = x2;
        controlY = y2;
    }

    private static void Quadratic(
        Contour contour,
        ref Scanner scanner,
        ref float cx,
        ref float cy,
        ref float controlX,
        ref float controlY,
        bool relative
    )
    {
        var x1 = scanner.Number();
        var y1 = scanner.Number();
        var x = scanner.Number();
        var y = scanner.Number();
        if (relative)
        {
            x1 += cx;
            y1 += cy;
            x += cx;
            y += cy;
        }
        Cubic(contour, cx, cy, cx + 2 * (x1 - cx) / 3, cy + 2 * (y1 - cy) / 3, x + 2 * (x1 - x) / 3, y + 2 * (y1 - y) / 3, x, y);
        cx = x;
        cy = y;
        controlX = x1;
        controlY = y1;
    }

    private static void SmoothQuadratic(
        Contour contour,
        ref Scanner scanner,
        ref float cx,
        ref float cy,
        ref float controlX,
        ref float controlY,
        bool relative,
        char previous
    )
    {
        var x1 = previous is 'Q' or 'q' or 'T' or 't' ? 2 * cx - controlX : cx;
        var y1 = previous is 'Q' or 'q' or 'T' or 't' ? 2 * cy - controlY : cy;
        var x = scanner.Number();
        var y = scanner.Number();
        if (relative)
        {
            x += cx;
            y += cy;
        }
        Cubic(contour, cx, cy, cx + 2 * (x1 - cx) / 3, cy + 2 * (y1 - cy) / 3, x + 2 * (x1 - x) / 3, y + 2 * (y1 - y) / 3, x, y);
        cx = x;
        cy = y;
        controlX = x1;
        controlY = y1;
    }

    private static void Arc(
        Contour contour,
        ref Scanner scanner,
        ref float cx,
        ref float cy,
        bool relative
    )
    {
        var rx = MathF.Abs(scanner.Number());
        var ry = MathF.Abs(scanner.Number());
        var rotation = scanner.Number() * MathF.PI / 180;
        var large = scanner.Number() != 0;
        var sweep = scanner.Number() != 0;
        var x = scanner.Number();
        var y = scanner.Number();
        if (relative)
        {
            x += cx;
            y += cy;
        }
        if (rx == 0 || ry == 0)
        {
            contour.Points.Add(new Pt(x, y));
            cx = x;
            cy = y;
            return;
        }
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        var dx = (cx - x) / 2;
        var dy = (cy - y) / 2;
        var x1 = cos * dx + sin * dy;
        var y1 = -sin * dx + cos * dy;
        var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            var scale = MathF.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }
        var sign = large == sweep ? -1 : 1;
        var coef = sign * MathF.Sqrt(MathF.Max(0, (rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1) / (rx * rx * y1 * y1 + ry * ry * x1 * x1)));
        var centerX = coef * rx * y1 / ry;
        var centerY = coef * -ry * x1 / rx;
        var joinedX = cos * centerX - sin * centerY + (cx + x) / 2;
        var joinedY = sin * centerX + cos * centerY + (cy + y) / 2;
        var start = Angle(1, 0, (x1 - centerX) / rx, (y1 - centerY) / ry);
        var delta = Angle((x1 - centerX) / rx, (y1 - centerY) / ry, (-x1 - centerX) / rx, (-y1 - centerY) / ry);
        if (!sweep && delta > 0)
            delta -= MathF.Tau;
        if (sweep && delta < 0)
            delta += MathF.Tau;
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 18)));
        for (var i = 1; i <= steps; i++)
        {
            var theta = start + delta * i / steps;
            var localX = rx * MathF.Cos(theta);
            var localY = ry * MathF.Sin(theta);
            contour.Points.Add(new Pt(cos * localX - sin * localY + joinedX, sin * localX + cos * localY + joinedY));
        }
        cx = x;
        cy = y;
    }

    private static float Angle(float ux, float uy, float vx, float vy) =>
        MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

    private static void Cubic(
        Contour contour,
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        float x3,
        float y3,
        int depth = 0
    )
    {
        if (contour.Points.Count > 200_000)
            throw new InvalidDataException("SVG path is too detailed.");
        if (depth > 16 || Flat(x0, y0, x1, y1, x2, y2, x3, y3))
        {
            contour.Points.Add(new Pt(x3, y3));
            return;
        }
        var x01 = (x0 + x1) / 2;
        var y01 = (y0 + y1) / 2;
        var x12 = (x1 + x2) / 2;
        var y12 = (y1 + y2) / 2;
        var x23 = (x2 + x3) / 2;
        var y23 = (y2 + y3) / 2;
        var x012 = (x01 + x12) / 2;
        var y012 = (y01 + y12) / 2;
        var x123 = (x12 + x23) / 2;
        var y123 = (y12 + y23) / 2;
        var x0123 = (x012 + x123) / 2;
        var y0123 = (y012 + y123) / 2;
        Cubic(contour, x0, y0, x01, y01, x012, y012, x0123, y0123, depth + 1);
        Cubic(contour, x0123, y0123, x123, y123, x23, y23, x3, y3, depth + 1);
    }

    private static bool Flat(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        var dx = x3 - x0;
        var dy = y3 - y0;
        var length = Hypot(dx, dy);
        if (length < 0.01f)
            return true;
        var d1 = MathF.Abs((x1 - x3) * dy - (y1 - y3) * dx) / length;
        var d2 = MathF.Abs((x2 - x3) * dy - (y2 - y3) * dx) / length;
        return MathF.Max(d1, d2) <= 0.25f;
    }

    private static Matrix Transform(string? text)
    {
        var matrix = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
            return matrix;
        var scanner = new Scanner(text);
        while (scanner.HasMore())
        {
            if (!scanner.IsCommand())
                throw new InvalidDataException("SVG transform is missing a function.");
            var name = scanner.Word();
            scanner.Expect('(');
            var next = name switch
            {
                "matrix" => new Matrix(
                    scanner.Number(),
                    scanner.Number(),
                    scanner.Number(),
                    scanner.Number(),
                    scanner.Number(),
                    scanner.Number()
                ),
                "translate" => Translate(ref scanner),
                "scale" => Scale(ref scanner),
                "rotate" => Rotate(ref scanner),
                _ => throw new InvalidDataException("Unsupported SVG transform."),
            };
            scanner.Expect(')');
            matrix = matrix.Multiply(next);
        }
        return matrix;
    }

    private static Matrix Translate(ref Scanner scanner)
    {
        var x = scanner.Number();
        var y = scanner.HasNumber() ? scanner.Number() : 0;
        return Matrix.Translate(x, y);
    }

    private static Matrix Scale(ref Scanner scanner)
    {
        var x = scanner.Number();
        var y = scanner.HasNumber() ? scanner.Number() : x;
        return Matrix.Scale(x, y);
    }

    private static Matrix Rotate(ref Scanner scanner)
    {
        var angle = scanner.Number();
        if (!scanner.HasNumber())
            return Matrix.Rotate(angle, 0, 0);
        var x = scanner.Number();
        var y = scanner.Number();
        return Matrix.Rotate(angle, x, y);
    }

    internal static PaintChoice? Paint(XElement element, string name)
    {
        var text = Attr(element, name);
        if (text is null)
            return null;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return new PaintChoice(null);
        return new PaintChoice(ColorOf(text));
    }

    internal static float? Optional(XElement element, string name)
    {
        var text = Attr(element, name);
        return text is null ? null : Num(text);
    }

    private static Rgba ColorOf(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text is "black")
            return new Rgba(0, 0, 0, 255);
        if (text is "white")
            return new Rgba(255, 255, 255, 255);
        if (text is "transparent")
            return new Rgba(0, 0, 0, 0);
        if (text.StartsWith('#') && text.Length is 4 or 5 or 7 or 9)
        {
            if (text.Length is 4 or 5)
                text = "#"
                    + new string(text[1], 2)
                    + new string(text[2], 2)
                    + new string(text[3], 2)
                    + (text.Length == 5 ? new string(text[4], 2) : "ff");
            else if (text.Length == 7)
                text += "ff";
            return new Rgba(
                Convert.ToByte(text[1..3], 16),
                Convert.ToByte(text[3..5], 16),
                Convert.ToByte(text[5..7], 16),
                Convert.ToByte(text[7..9], 16)
            );
        }
        throw new InvalidDataException("SVG color is invalid.");
    }

    private static string? Attr(XElement element, string name)
    {
        var direct = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
        if (direct is not null)
            return direct;
        var style = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "style")?.Value;
        if (style is null)
            return null;
        foreach (var part in style.Split(';'))
        {
            var pair = part.Split(':', 2);
            if (pair.Length == 2 && pair[0].Trim() == name)
                return pair[1].Trim();
        }
        return null;
    }

    private static string? ClipId(string? value)
    {
        if (value is null)
            return null;
        value = value.Trim();
        if (!value.StartsWith("url(", StringComparison.Ordinal) || !value.EndsWith(')'))
            return null;
        var inner = value[4..^1].Trim().Trim('\'', '"');
        return inner.StartsWith('#') ? inner[1..] : null;
    }

    private static float Num(string? text) =>
        text is null ? 0 : Parse(text.Trim());

    private static float Parse(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException("SVG number is invalid.");

    private static float? Length(string? text)
    {
        if (text is null)
            return null;
        text = text.Trim();
        if (text.EndsWith("px", StringComparison.Ordinal))
            text = text[..^2];
        return Num(text);
    }

    private static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);

    private static float Positive(float value) =>
        value > 0 ? value : throw new InvalidDataException("SVG size must be positive.");

    private static byte[] EncodePng(Bitmap bitmap)
    {
        var raw = new byte[(bitmap.Width * 4 + 1) * bitmap.Height];
        var stride = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var destination = y * (stride + 1) + 1;
            bitmap.Pixels.AsSpan(y * stride, stride).CopyTo(raw.AsSpan(destination));
        }
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x01);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw);
        var adler = Adler(raw);
        compressed.WriteByte((byte)(adler >> 24));
        compressed.WriteByte((byte)(adler >> 16));
        compressed.WriteByte((byte)(adler >> 8));
        compressed.WriteByte((byte)adler);
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk(png, "IHDR", Header(bitmap.Width, bitmap.Height));
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Header(int width, int height)
    {
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        var name = Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        var crcSource = new byte[name.Length + data.Length];
        name.CopyTo(crcSource, 0);
        data.CopyTo(crcSource, name.Length);
        Span<byte> crc = stackalloc byte[4];
        WriteInt(crc, 0, (int)Crc(crcSource));
        stream.Write(crc);
    }

    private static void WriteInt(Span<byte> destination, int offset, int value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    private static uint Adler(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}

internal sealed class Bitmap(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = new byte[width * height * 4];

    public void Fill(List<Pt[]> contours, Rgba color, bool evenOdd, byte[]? clip) =>
        Scan(contours, color, evenOdd, clip, null);

    public void Cover(List<Pt[]> contours, byte[] mask) =>
        Scan(contours, new Rgba(0, 0, 0, 255), false, null, mask);

    public void Stroke(List<Pt[]> contours, Rgba color, float width, byte[]? clip)
    {
        var radius = width / 2;
        if (radius <= 0)
            return;
        foreach (var contour in contours)
        {
            for (var i = 0; i < contour.Length; i++)
                Disc(contour[i], radius, color, clip);
            for (var i = 1; i < contour.Length; i++)
                Segment(contour[i - 1], contour[i], radius, color, clip);
        }
    }

    public byte[] Intersect(byte[]? parent, byte[] child)
    {
        if (parent is null)
            return child;
        for (var i = 0; i < child.Length; i++)
            if (parent[i] == 0)
                child[i] = 0;
        return child;
    }

    private void Segment(Pt from, Pt to, float radius, Rgba color, byte[]? clip)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
            return;
        var nx = -dy / length * radius;
        var ny = dx / length * radius;
        Fill(
            [
                [
                    new Pt(from.X + nx, from.Y + ny),
                    new Pt(to.X + nx, to.Y + ny),
                    new Pt(to.X - nx, to.Y - ny),
                    new Pt(from.X - nx, from.Y - ny),
                    new Pt(from.X + nx, from.Y + ny),
                ],
            ],
            color,
            false,
            clip
        );
    }

    private void Disc(Pt center, float radius, Rgba color, byte[]? clip)
    {
        var x0 = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var x1 = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));
        var y0 = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        var y1 = Math.Min(Height - 1, (int)MathF.Ceiling(center.Y + radius));
        var squared = radius * radius;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5f - center.X;
                var dy = y + 0.5f - center.Y;
                if (dx * dx + dy * dy <= squared)
                    Blend(x, y, color, clip);
            }
        }
    }

    private void Scan(List<Pt[]> contours, Rgba color, bool evenOdd, byte[]? clip, byte[]? mask)
    {
        var edges = new List<Edge>();
        foreach (var contour in contours)
        {
            for (var i = 1; i < contour.Length; i++)
            {
                var from = contour[i - 1];
                var to = contour[i];
                if (MathF.Abs(from.Y - to.Y) < 0.0001f)
                    continue;
                edges.Add(new Edge(from.X, from.Y, to.X, to.Y));
            }
        }
        if (edges.Count == 0)
            return;
        var minY = edges.Min(edge => MathF.Min(edge.Y0, edge.Y1));
        var maxY = edges.Max(edge => MathF.Max(edge.Y0, edge.Y1));
        var y0 = Math.Clamp((int)MathF.Floor(minY), 0, Height);
        var y1 = Math.Clamp((int)MathF.Ceiling(maxY), 0, Height);
        var hits = new List<(float X, int Direction)>();
        for (var y = y0; y < y1; y++)
        {
            var scan = y + 0.5f;
            hits.Clear();
            foreach (var edge in edges)
            {
                if (!((edge.Y0 <= scan && edge.Y1 > scan) || (edge.Y1 <= scan && edge.Y0 > scan)))
                    continue;
                var t = (scan - edge.Y0) / (edge.Y1 - edge.Y0);
                hits.Add((edge.X0 + t * (edge.X1 - edge.X0), edge.Y1 > edge.Y0 ? 1 : -1));
            }
            hits.Sort((left, right) => left.X.CompareTo(right.X));
            if (evenOdd)
            {
                for (var i = 0; i + 1 < hits.Count; i += 2)
                    Paint(y, hits[i].X, hits[i + 1].X, color, clip, mask);
            }
            else
            {
                var winding = 0;
                float? start = null;
                foreach (var hit in hits)
                {
                    var previous = winding;
                    winding += hit.Direction;
                    if (previous == 0 && winding != 0)
                        start = hit.X;
                    else if (previous != 0 && winding == 0 && start is { } from)
                    {
                        Paint(y, from, hit.X, color, clip, mask);
                        start = null;
                    }
                }
            }
        }
    }

    private void Paint(int y, float x0, float x1, Rgba color, byte[]? clip, byte[]? mask)
    {
        if (x1 < x0)
            (x0, x1) = (x1, x0);
        var start = Math.Max(0, (int)MathF.Floor(x0));
        var end = Math.Min(Width - 1, (int)MathF.Floor(x1));
        for (var x = start; x <= end; x++)
        {
            var left = MathF.Max(x0, x);
            var right = MathF.Min(x1, x + 1);
            if (right <= left)
                continue;
            if (mask is not null)
            {
                mask[y * Width + x] = 1;
                continue;
            }
            var coverage = right - left;
            Blend(x, y, color with { A = (byte)(color.A * coverage) }, clip);
        }
    }

    private void Blend(int x, int y, Rgba color, byte[]? clip)
    {
        if (clip is not null && clip[y * Width + x] == 0)
            return;
        var index = (y * Width + x) * 4;
        var source = color.A / 255f;
        var destination = Pixels[index + 3] / 255f;
        var alpha = source + destination * (1 - source);
        if (alpha <= 0)
            return;
        Pixels[index] = (byte)((color.R * source + Pixels[index] * destination * (1 - source)) / alpha);
        Pixels[index + 1] = (byte)((color.G * source + Pixels[index + 1] * destination * (1 - source)) / alpha);
        Pixels[index + 2] = (byte)((color.B * source + Pixels[index + 2] * destination * (1 - source)) / alpha);
        Pixels[index + 3] = (byte)(alpha * 255);
    }

    private readonly record struct Edge(float X0, float Y0, float X1, float Y1);
}

internal sealed class Contour
{
    public List<Pt> Points { get; } = [];
}

internal sealed record Shape(List<Contour> Contours, Matrix Matrix);

internal readonly record struct Pt(float X, float Y);

internal readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public Rgba Scale(float opacity) => this with { A = (byte)(A * opacity) };
}

internal readonly record struct Matrix(float A, float B, float C, float D, float E, float F)
{
    public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translate(float x, float y) => new(1, 0, 0, 1, x, y);

    public static Matrix Scale(float x, float y) => new(x, 0, 0, y, 0, 0);

    public static Matrix Rotate(float degrees, float cx, float cy)
    {
        var radians = degrees * MathF.PI / 180;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var rotation = new Matrix(cos, sin, -sin, cos, 0, 0);
        return Translate(cx, cy).Multiply(rotation).Multiply(Translate(-cx, -cy));
    }

    public Matrix Multiply(Matrix local) =>
        new(
            A * local.A + C * local.B,
            B * local.A + D * local.B,
            A * local.C + C * local.D,
            B * local.C + D * local.D,
            A * local.E + C * local.F + E,
            B * local.E + D * local.F + F
        );

    public Pt Apply(Pt point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);
}

internal readonly record struct Style(Rgba? Fill, Rgba? Stroke, float StrokeWidth, float Opacity, bool EvenOdd)
{
    public static Style Initial { get; } = new(new Rgba(0, 0, 0, 255), null, 1, 1, false);

    public Style Apply(XElement element)
    {
        var style = this;
        if (SvgRasterizer.Paint(element, "fill") is { } fill)
            style = style with { Fill = fill.Color };
        if (SvgRasterizer.Paint(element, "stroke") is { } stroke)
            style = style with { Stroke = stroke.Color };
        if (SvgRasterizer.Optional(element, "stroke-width") is { } width)
            style = style with { StrokeWidth = width };
        if (SvgRasterizer.Optional(element, "opacity") is { } opacity)
            style = style with { Opacity = Opacity * Math.Clamp(opacity, 0, 1) };
        if (SvgRasterizer.Optional(element, "fill-opacity") is { } fillOpacity && style.Fill is { } painted)
            style = style with { Fill = painted with { A = (byte)(painted.A * Math.Clamp(fillOpacity, 0, 1)) } };
        var rule = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "fill-rule")?.Value;
        if (rule == "evenodd")
            style = style with { EvenOdd = true };
        if (rule == "nonzero")
            style = style with { EvenOdd = false };
        return style;
    }
}

internal readonly record struct PaintChoice(Rgba? Color);

internal ref struct Scanner(string text)
{
    private int index;

    public bool HasMore()
    {
        Skip();
        return index < text.Length;
    }

    public bool IsCommand()
    {
        Skip();
        return index < text.Length && char.IsAsciiLetter(text[index]);
    }

    public bool HasNumber()
    {
        Skip();
        if (index >= text.Length)
            return false;
        var next = text[index];
        return next is '+' or '-' or '.' or (>= '0' and <= '9');
    }

    public char Command()
    {
        Skip();
        return text[index++];
    }

    public string Word()
    {
        Skip();
        var start = index;
        while (index < text.Length && char.IsAsciiLetter(text[index]))
            index++;
        return text[start..index];
    }

    public void Expect(char token)
    {
        Skip();
        if (index >= text.Length || text[index] != token)
            throw new InvalidDataException("SVG transform syntax is invalid.");
        index++;
    }

    public float Number()
    {
        Skip();
        var start = index;
        if (index < text.Length && text[index] is '+' or '-')
            index++;
        var dot = false;
        var digits = false;
        while (index < text.Length)
        {
            var current = text[index];
            if (current is >= '0' and <= '9')
            {
                digits = true;
                index++;
            }
            else if (current == '.' && !dot)
            {
                dot = true;
                index++;
            }
            else if (current is 'e' or 'E' && digits)
            {
                index++;
                if (index < text.Length && text[index] is '+' or '-')
                    index++;
                while (index < text.Length && text[index] is >= '0' and <= '9')
                    index++;
                break;
            }
            else
                break;
        }
        if (!digits || !float.TryParse(text.AsSpan(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("SVG number is invalid.");
        return value;
    }

    private void Skip()
    {
        while (index < text.Length && text[index] is ' ' or '\t' or '\n' or '\r' or ',')
            index++;
    }
}
