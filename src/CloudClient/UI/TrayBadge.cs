using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace CloudClient.UI;

/// <summary>What the tray icon's badge says about syncing.</summary>
public enum TrayState
{
    /// <summary>No badge: connecting, or not signed in.</summary>
    None,
    Synced,
    Uploading,
    Downloading,
    /// <summary>Going both ways at once.</summary>
    Syncing,
    Error,
}

/// <summary>
/// Draws the tray icon: the brand's logo with a status badge in the
/// bottom-right corner, as Durazzno App does. Uploading and downloading badges
/// move, one frame at a time.
/// </summary>
public static class TrayBadge
{
    private static readonly Color Green = ColorTranslator.FromHtml("#22C55E");
    private static readonly Color Blue = ColorTranslator.FromHtml("#3B82F6");
    private static readonly Color Purple = ColorTranslator.FromHtml("#A855F7");
    private static readonly Color Red = ColorTranslator.FromHtml("#EF4444");

    /// <summary>Frames of the moving badges; the caller steps through them on a timer.</summary>
    public const int Frames = 12;

    public static bool IsAnimated(TrayState state) => state is TrayState.Uploading or TrayState.Downloading or TrayState.Syncing;

    public static Icon Draw(Bitmap logo, TrayState state, int frame)
    {
        var size = Forms.SystemInformation.SmallIconSize.Width;
        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(logo, new Rectangle(0, 0, size, size));
            if (state != TrayState.None) DrawBadge(g, size, state, frame % Frames);
        }
        return ToIcon(canvas);
    }

    private static void DrawBadge(Graphics g, int size, TrayState state, int frame)
    {
        // Big enough to read its glyph at 16 px, small enough to leave the logo recognisable.
        var d = Math.Max(9f, size * 0.6f);
        var outer = new RectangleF(size - d, size - d, d, d);
        var inner = RectangleF.Inflate(outer, -1, -1);
        var color = state switch
        {
            TrayState.Synced => Green,
            TrayState.Uploading => Purple,
            TrayState.Downloading => Blue,
            // Both ways: the two colours take turns, once per round of the arrows.
            TrayState.Syncing => frame < Frames / 2 ? Purple : Blue,
            _ => Red,
        };
        using (var ring = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20))) g.FillEllipse(ring, outer);
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, inner);

        var r = inner.Width / 2;
        var cx = inner.X + r;
        var cy = inner.Y + r;
        using var pen = new Pen(Color.White, Math.Max(1.3f, d / 7.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var clip = new GraphicsPath();
        clip.AddEllipse(inner);
        g.SetClip(clip);

        switch (state)
        {
            case TrayState.Synced:
                g.DrawLines(pen, [new PointF(cx - r * 0.45f, cy + r * 0.02f), new PointF(cx - r * 0.12f, cy + r * 0.35f), new PointF(cx + r * 0.45f, cy - r * 0.32f)]);
                break;
            case TrayState.Uploading:
            case TrayState.Downloading:
            {
                // The arrow nudges the way it points and drops back, staying whole so it reads at 16 px.
                var up = state == TrayState.Uploading;
                var travel = (frame / (float)(Frames - 1) - 0.5f) * r * 0.6f;
                DrawArrow(g, pen, cx, cy + (up ? -travel : travel), r, up);
                break;
            }
            case TrayState.Syncing:
                var state0 = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(frame * 360f / Frames);
                var a = r * 0.55f;
                g.DrawArc(pen, -a, -a, a * 2, a * 2, 200, 120);
                g.DrawArc(pen, -a, -a, a * 2, a * 2, 20, 120);
                // Arrowheads at the end of each arc.
                DrawHead(g, pen, a, 320);
                DrawHead(g, pen, a, 140);
                g.Restore(state0);
                break;
            default:
                g.DrawLine(pen, cx, cy - r * 0.5f, cx, cy + r * 0.08f);
                using (var dot = new SolidBrush(Color.White))
                {
                    var w = pen.Width * 1.1f;
                    g.FillEllipse(dot, cx - w / 2, cy + r * 0.38f - w / 2, w, w);
                }
                break;
        }
        g.ResetClip();
    }

    private static void DrawArrow(Graphics g, Pen pen, float cx, float cy, float r, bool up)
    {
        var tip = up ? -r * 0.5f : r * 0.5f;
        var back = up ? r * 0.5f : -r * 0.5f;
        var wing = up ? -r * 0.1f : r * 0.1f;
        g.DrawLine(pen, cx, cy + back, cx, cy + tip);
        g.DrawLines(pen, [new PointF(cx - r * 0.38f, cy + wing), new PointF(cx, cy + tip), new PointF(cx + r * 0.38f, cy + wing)]);
    }

    /// <summary>A small arrowhead at angle <paramref name="degrees"/> on a circle of radius <paramref name="a"/>, pointing along it.</summary>
    private static void DrawHead(Graphics g, Pen pen, float a, float degrees)
    {
        var t = degrees * MathF.PI / 180;
        var x = a * MathF.Cos(t);
        var y = a * MathF.Sin(t);
        // Tangent direction (clockwise drawing) and the outward normal.
        var tx = -MathF.Sin(t);
        var ty = MathF.Cos(t);
        var h = a * 0.55f;
        g.DrawLines(pen, [
            new PointF(x - tx * h + x / a * h * 0.6f, y - ty * h + y / a * h * 0.6f),
            new PointF(x, y),
            new PointF(x - tx * h - x / a * h * 0.6f, y - ty * h - y / a * h * 0.6f),
        ]);
    }

    /// <summary>A PNG in a one-image ICO container, which Windows reads natively.</summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)1);
            w.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            w.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write((int)png.Length);
            w.Write(6 + 16);
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico);
    }
}
