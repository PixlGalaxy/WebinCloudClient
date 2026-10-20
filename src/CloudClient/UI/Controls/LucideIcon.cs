using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace CloudClient.UI.Controls;

/// <summary>
/// A Lucide icon, drawn as thin strokes in Foreground. The SVGs are embedded
/// from Assets/Lucide as "Lucide.&lt;name&gt;"; to add one, drop the official
/// SVG there under its exact Lucide name. A missing icon draws nothing.
/// </summary>
public class LucideIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(LucideIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Shared with TextElement.Foreground, so an icon inside a button takes the button's text colour.</summary>
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private static readonly Dictionary<string, Geometry?> Cache = new();

    public LucideIcon()
    {
        Width = 16;
        Height = 16;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geometry = Load(Icon);
        if (geometry is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    public static Geometry? Load(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            Geometry? geometry = null;
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lucide.{name}");
                if (stream is not null) geometry = Parse(XDocument.Load(stream));
            }
            catch
            {
                geometry = null;
            }
            Cache[name] = geometry;
            return geometry;
        }
    }

    private static double N(XElement e, string attribute) =>
        double.Parse((string?)e.Attribute(attribute) ?? "0", CultureInfo.InvariantCulture);

    private static Geometry Parse(XDocument svg)
    {
        var group = new GeometryGroup();
        foreach (var e in svg.Descendants())
        {
            switch (e.Name.LocalName)
            {
                case "path":
                    group.Children.Add(Geometry.Parse(NormalizePath((string?)e.Attribute("d") ?? "")));
                    break;
                case "circle":
                    group.Children.Add(new EllipseGeometry(new Point(N(e, "cx"), N(e, "cy")), N(e, "r"), N(e, "r")));
                    break;
                case "ellipse":
                    group.Children.Add(new EllipseGeometry(new Point(N(e, "cx"), N(e, "cy")), N(e, "rx"), N(e, "ry")));
                    break;
                case "line":
                    group.Children.Add(new LineGeometry(new Point(N(e, "x1"), N(e, "y1")), new Point(N(e, "x2"), N(e, "y2"))));
                    break;
                case "rect":
                    var r = e.Attribute("rx") is null ? 0 : N(e, "rx");
                    group.Children.Add(new RectangleGeometry(new Rect(N(e, "x"), N(e, "y"), N(e, "width"), N(e, "height")), r, r));
                    break;
                case "polyline":
                case "polygon":
                    var points = Regex.Matches((string?)e.Attribute("points") ?? "", @"-?\d*\.?\d+").Select(m => m.Value).ToList();
                    if (points.Count >= 4)
                    {
                        var d = new StringBuilder($"M{points[0]},{points[1]}");
                        for (var i = 2; i + 1 < points.Count; i += 2) d.Append($" L{points[i]},{points[i + 1]}");
                        if (e.Name.LocalName == "polygon") d.Append(" Z");
                        group.Children.Add(Geometry.Parse(d.ToString()));
                    }
                    break;
            }
        }
        group.Freeze();
        return group;
    }

    private static readonly Regex Number = new(@"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites SVG path data so WPF's parser reads it: SVG allows "1.5.5" for
    /// two numbers and arc flags without separators ("a1 1 0 011 1").
    /// </summary>
    private static string NormalizePath(string d)
    {
        var output = new StringBuilder();
        var i = 0;
        var command = ' ';
        var argIndex = 0;
        while (i < d.Length)
        {
            var c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c) && c != 'e' && c != 'E')
            {
                command = c;
                argIndex = 0;
                output.Append(' ').Append(c);
                i++;
                continue;
            }
            // Arcs take 7 numbers; the 4th and 5th are single-digit flags.
            var arg = argIndex % 7;
            if ((command == 'a' || command == 'A') && (arg == 3 || arg == 4) && (c == '0' || c == '1'))
            {
                output.Append(' ').Append(c);
                i++;
                argIndex++;
                continue;
            }
            var match = Number.Match(d, i);
            if (!match.Success || match.Index != i) { i++; continue; }
            output.Append(' ').Append(match.Value);
            i += match.Length;
            argIndex++;
        }
        return output.ToString().Trim();
    }
}
