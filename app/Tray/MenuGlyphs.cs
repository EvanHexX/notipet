using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Notipet.Tray;

// Icons for the Win32 tray menu, drawn from the same Segoe Fluent Icons glyphs
// the WinUI windows use, so the menu and the windows speak one visual language.
//
// Win32 menus take an HBITMAP per item. For the icon to have soft edges on any
// menu background it has to be a 32-bit DIB with *premultiplied* alpha -
// Bitmap.GetHbitmap() flattens alpha onto a solid colour, which is why it is
// not used here.
[SupportedOSPlatform("windows")]
internal static class MenuGlyphs
{
    private static bool _darkModeRequested;

    // Opts this process's menus into following the system light/dark setting.
    // Undocumented (uxtheme ordinals 135/136, stable since Windows 10 1903),
    // which is exactly why every failure is swallowed: worst case the menu stays
    // light, which is what it was before.
    public static void EnableSystemThemeMenus()
    {
        if (_darkModeRequested) return;
        _darkModeRequested = true;
        try
        {
            SetPreferredAppMode(AllowDark);
            FlushMenuThemes();
        }
        catch
        {
        }
    }

    // Settings' theme for the tray menu: System follows Windows, Light and
    // Dark force it (uxtheme modes 3 and 2). Same undocumented call, same
    // rule - a failure leaves the menu as it was.
    private static string _theme = "System";

    public static void SetMenuTheme(string? theme)
    {
        _theme = theme is "Light" or "Dark" ? theme : "System";
        if (!_darkModeRequested) return;
        try
        {
            SetPreferredAppMode(_theme switch { "Dark" => ForceDark, "Light" => ForceLight, _ => AllowDark });
            FlushMenuThemes();
        }
        catch
        {
        }
    }

    // Whether menus are currently drawn dark, so icons can be drawn in a colour
    // that reads on them.
    public static bool MenusAreDark()
    {
        if (!_darkModeRequested) return false;
        if (_theme == "Dark") return true;
        if (_theme == "Light") return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static IntPtr Render(string glyph, int size, bool dark)
    {
        var colour = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(32, 32, 32);

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(FontFamilyName.Value, size * 0.78f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(colour);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }

        var info = new BITMAPINFO
        {
            biSize = 40, // sizeof(BITMAPINFOHEADER), not the struct with its colour table
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0 // BI_RGB
        };

        var hbitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (hbitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size * 4, size * 4);
            }

            // Premultiply: the menu composites with AlphaBlend, which expects it.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return hbitmap;
    }

    public static void Delete(IntPtr hbitmap)
    {
        if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
    }

    // GDI only - no window, no menu - so it is safe to run headless.
    public static bool RunSelfTest()
    {
        foreach (var dark in new[] { false, true })
        {
            foreach (var size in new[] { 16, 24, 32 })
            {
                var bitmap = Render("", size, dark);
                if (bitmap == IntPtr.Zero) return false;
                Delete(bitmap);
            }
        }
        return !string.IsNullOrEmpty(FontFamilyName.Value);
    }

    // Enumerating installed fonts is slow; the answer never changes.
    private static readonly Lazy<string> FontFamilyName = new(IconFontFamily);

    private static string IconFontFamily()
    {
        // Segoe Fluent Icons on Windows 11, MDL2 Assets on Windows 10. Both
        // share the code points used here.
        using var fonts = new InstalledFontCollection();
        foreach (var family in fonts.Families)
        {
            if (family.Name == "Segoe Fluent Icons") return family.Name;
        }
        return "Segoe MDL2 Assets";
    }

    private const int AllowDark = 1;
    private const int ForceDark = 2;
    private const int ForceLight = 3;

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
