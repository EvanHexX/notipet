using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

namespace Notipet.Tray;

// Writes a multi-resolution .ico with PNG-compressed frames (supported since
// Windows Vista) from TrayIconRenderer.RenderApp, so the exe icon, the title
// bars and the taskbar all show the same bell tile.
[SupportedOSPlatform("windows")]
internal static class IconExport
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    // Developer aid: every tray state at the sizes the shell uses, on a dark
    // and a light strip, to eyeball before shipping a renderer change.
    public static bool WriteTraySheet(string path)
    {
        try
        {
            var states = (TrayIconState[])Enum.GetValues(typeof(TrayIconState));
            var sizes = new[] { 16, 20, 24, 32, 48 };
            var cell = 56;
            using var sheet = new Bitmap(cell * sizes.Length, cell * states.Length * 2);
            using var g = Graphics.FromImage(sheet);
            for (var band = 0; band < 2; band++)
            {
                using var bg = new SolidBrush(band == 0 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238));
                g.FillRectangle(bg, 0, band * cell * states.Length, sheet.Width, cell * states.Length);
                for (var row = 0; row < states.Length; row++)
                {
                    for (var col = 0; col < sizes.Length; col++)
                    {
                        using var icon = TrayIconRenderer.RenderTray(states[row], sizes[col]);
                        g.DrawImage(icon, col * cell + (cell - sizes[col]) / 2, (band * states.Length + row) * cell + (cell - sizes[col]) / 2);
                    }
                }
            }
            sheet.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tray sheet failed: " + ex.Message);
            return false;
        }
    }

    public static bool Write(string path)
    {
        try
        {
            var frames = new List<byte[]>();
            foreach (var size in Sizes)
            {
                // Rendered natively at each size rather than scaled from one,
                // so small frames stay crisp.
                using var bitmap = TrayIconRenderer.RenderApp(size);
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                frames.Add(png.ToArray());
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            writer.Write((ushort)0);            // reserved
            writer.Write((ushort)1);            // type: icon
            writer.Write((ushort)Sizes.Length); // image count

            var offset = 6 + 16 * Sizes.Length;
            for (var i = 0; i < Sizes.Length; i++)
            {
                var size = Sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);   // palette
                writer.Write((byte)0);   // reserved
                writer.Write((ushort)1); // planes
                writer.Write((ushort)32);
                writer.Write(frames[i].Length);
                writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);

            Console.WriteLine($"wrote {path} ({Sizes.Length} sizes)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("export-icon failed: " + ex.Message);
            return false;
        }
    }
}
