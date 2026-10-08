using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Notipet.Tray;

internal enum TrayIconState
{
    Idle,
    Muted,
    QuietHours,
    Alarming,
    SoundUnavailable
}

// Draws the tray icon at runtime rather than shipping five .ico files, so the
// state is always in sync with the app rather than with a build step.
//
// The icon carries exactly one signal - the current state, as a colour plus a
// small glyph. Anything more detailed belongs in the tooltip, which has room
// for words.
[SupportedOSPlatform("windows")]
internal static class TrayIconRenderer
{
    public static Icon Create(TrayIconState state, int size)
    {
        try
        {
            using var bitmap = RenderTray(state, size);
            return FromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    // The application icon: a white bell on a rounded accent tile, the shape
    // Windows 11 app icons take. Used for the exe, the title bars and the
    // taskbar; the tray keeps the bare, state-coloured bell.
    public static Icon CreateApp(int size)
    {
        try
        {
            using var bitmap = RenderApp(size);
            return FromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    public static Bitmap RenderTray(TrayIconState state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        DrawBell(g, size, state);
        return bitmap;
    }

    public static Bitmap RenderApp(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Tile, inset a little so the icon has air around it at every size.
        var inset = size * 0.06f;
        var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var path = RoundedRect(tile, size * 0.22f))
        using (var brush = new LinearGradientBrush(tile, Color.FromArgb(76, 160, 255), Color.FromArgb(38, 99, 235), LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(brush, path);
        }

        // Bell at 62% of the tile, centred.
        var bellSize = tile.Width * 0.62f;
        var origin = new PointF(tile.X + (tile.Width - bellSize) / 2, tile.Y + (tile.Height - bellSize) / 2 + bellSize * 0.02f);
        using (var bell = BellPath(origin, bellSize))
        using (var white = new SolidBrush(Color.White))
        {
            g.FillPath(white, bell);
        }
        using (var clapper = new SolidBrush(Color.White))
        {
            var r = bellSize * 0.075f;
            g.FillEllipse(clapper, origin.X + bellSize * 0.5f - r, origin.Y + bellSize * 0.86f - r, 2 * r, 2 * r);
        }
        return bitmap;
    }

    // DPI-native icon size; the shell no longer downscales a fixed 32x32 render.
    public static int NativeSize()
    {
        try
        {
            var size = GetSystemMetrics(SM_CXSMICON);
            return size > 0 ? size : 16;
        }
        catch
        {
            return 16;
        }
    }

    private static (Color Fill, Color Accent) Palette(TrayIconState state) => state switch
    {
        TrayIconState.Alarming => (Color.FromArgb(255, 96, 92), Color.FromArgb(255, 210, 208)),
        TrayIconState.Muted => (Color.FromArgb(128, 132, 140), Color.FromArgb(200, 204, 210)),
        TrayIconState.QuietHours => (Color.FromArgb(124, 140, 200), Color.FromArgb(210, 218, 245)),
        TrayIconState.SoundUnavailable => (Color.FromArgb(226, 168, 62), Color.FromArgb(252, 230, 190)),
        _ => (Color.FromArgb(73, 169, 255), Color.FromArgb(205, 230, 255))
    };

    // A bell outline in a unit square, scaled to `size` at `origin`: a
    // shouldered dome that flares into a rim, with a knob on top. Curves rather
    // than stacked rectangles, so it still reads as a bell at 256 px.
    private static GraphicsPath BellPath(PointF origin, float size)
    {
        PointF P(float x, float y) => new(origin.X + x * size, origin.Y + y * size);

        var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(P(0.50f, 0.16f), P(0.75f, 0.16f), P(0.78f, 0.34f), P(0.78f, 0.50f));
        path.AddLine(P(0.78f, 0.50f), P(0.78f, 0.60f));
        path.AddBezier(P(0.78f, 0.60f), P(0.78f, 0.66f), P(0.88f, 0.70f), P(0.90f, 0.76f));
        path.AddLine(P(0.90f, 0.76f), P(0.10f, 0.76f));
        path.AddBezier(P(0.10f, 0.76f), P(0.12f, 0.70f), P(0.22f, 0.66f), P(0.22f, 0.60f));
        path.AddLine(P(0.22f, 0.60f), P(0.22f, 0.50f));
        path.AddBezier(P(0.22f, 0.50f), P(0.22f, 0.34f), P(0.25f, 0.16f), P(0.50f, 0.16f));
        path.CloseFigure();

        // Knob.
        var k = 0.06f * size;
        path.AddEllipse(origin.X + 0.5f * size - k, origin.Y + 0.11f * size - k, 2 * k, 2 * k);
        return path;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawBell(Graphics g, int size, TrayIconState state)
    {
        var (fill, _) = Palette(state);
        var scale = size / 16f;

        using var body = new SolidBrush(fill);
        using (var bell = BellPath(new PointF(0, 0), size))
        {
            g.FillPath(body, bell);
        }
        // Clapper.
        var r = size * 0.085f;
        g.FillEllipse(body, size * 0.5f - r, size * 0.87f - r, 2 * r, 2 * r);

        // Marks are knocked *out* of the bell (painted fully transparent) rather
        // than drawn in white: white only reads on a dark taskbar, a cut-out
        // reads on both, and it is how Fluent draws "off" variants.
        if (state is TrayIconState.Muted or TrayIconState.SoundUnavailable)
        {
            var previous = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            if (state == TrayIconState.Muted)
            {
                // A slash, the universal "off", with a coloured stroke beside the
                // gap so it still reads as a line at 16 px.
                using var gap = new Pen(Color.Transparent, Math.Max(2.2f, 2.6f * scale));
                g.DrawLine(gap, 1.5f * scale, 14.5f * scale, 14.5f * scale, 1.5f * scale);
                g.CompositingMode = previous;
                using var line = new Pen(fill, Math.Max(1.1f, 1.3f * scale));
                g.DrawLine(line, 2.6f * scale, 15.2f * scale, 15.2f * scale, 2.6f * scale);
            }
            else
            {
                using var gap = new Pen(Color.Transparent, Math.Max(1.3f, 1.6f * scale));
                g.DrawLine(gap, 8f * scale, 4.6f * scale, 8f * scale, 8.6f * scale);
                g.DrawLine(gap, 8f * scale, 10f * scale, 8f * scale, 10.8f * scale);
            }
            g.CompositingMode = previous;
        }
    }

    // Icon.FromHandle does not own the handle, so the bitmap's HICON has to be
    // cloned into a managed icon and the native one destroyed, or every redraw
    // leaks a GDI handle.
    private static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var unowned = Icon.FromHandle(handle);
            return (Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
