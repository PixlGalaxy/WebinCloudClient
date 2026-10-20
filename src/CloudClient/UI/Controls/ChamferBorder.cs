using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudClient.UI.Controls;

/// <summary>
/// A border with its bottom-right corner cut off: the shape of every card,
/// button and dialog in the app.
/// </summary>
public class ChamferBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset();
        var available = new Size(
            Math.Max(0, constraint.Width - inset.Left - inset.Right),
            Math.Max(0, constraint.Height - inset.Top - inset.Bottom));
        Child?.Measure(available);
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + inset.Left + inset.Right, desired.Height + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var inset = Inset();
        Child?.Arrange(new Rect(
            inset.Left,
            inset.Top,
            Math.Max(0, arrangeSize.Width - inset.Left - inset.Right),
            Math.Max(0, arrangeSize.Height - inset.Top - inset.Bottom)));
        return arrangeSize;
    }

    private Thickness Inset() => new(
        Padding.Left + BorderThickness,
        Padding.Top + BorderThickness,
        Padding.Right + BorderThickness,
        Padding.Bottom + BorderThickness);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var half = BorderThickness / 2;
        var cut = Math.Min(Chamfer, Math.Min(w, h) / 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(half, half), true, true);
            ctx.LineTo(new Point(w - half, half), true, false);
            ctx.LineTo(new Point(w - half, h - cut), true, false);
            ctx.LineTo(new Point(w - cut, h - half), true, false);
            ctx.LineTo(new Point(half, h - half), true, false);
        }
        geometry.Freeze();

        var pen = BorderBrush is not null && BorderThickness > 0 ? new Pen(BorderBrush, BorderThickness) : null;
        dc.DrawGeometry(Background, pen, geometry);
    }
}
