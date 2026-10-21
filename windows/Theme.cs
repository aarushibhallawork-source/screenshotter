// Picks the widget's colours from the current wallpaper (same method as the Mac app):
// darkest colour → frame and text, lightest → panels, most vivid → accent.
using System.Drawing;
using System.Globalization;
using System.Text;

namespace ScreenshotVault;

static class Theme
{
    public record struct Hsl(double H, double S, double L);

    public static string? WallpaperPath()
    {
        var sb = new StringBuilder(520);
        if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)) {
            var p = sb.ToString();
            if (p.Length > 0 && File.Exists(p)) return p;
        }
        // Slideshow / Spotlight wallpapers: Windows keeps a copy of the current one here.
        var t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(t) ? t : null;
    }

    static Hsl ToHsl(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2;
        if (mx == mn) return new(0, 0, l);
        double d = mx - mn, s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn), h;
        if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return new(h * 60, s, l);
    }

    static string Css(double h, double s, double l) => string.Format(CultureInfo.InvariantCulture,
        "hsl({0:0} {1:0}% {2:0}%)", h, Math.Clamp(s, 0, 1) * 100, Math.Clamp(l, 0, 1) * 100);

    public static Color ToColor(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    /// Returns the CSS variables for the widget, plus the frame colour for the window border.
    public static (Dictionary<string, string> vars, Color ink)? FromImage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = Image.FromStream(fs);
            const int W = 48, H = 48;
            using var bmp = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, W, H);

            // Bucket into 8 levels per channel, then merge buckets that look alike.
            var sums = new Dictionary<int, (double r, double g, double b, int n)>();
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
                    var key = ((int)(r * 7.99) << 6) | ((int)(gg * 7.99) << 3) | (int)(b * 7.99);
                    var s = sums.GetValueOrDefault(key);
                    sums[key] = (s.r + r, s.g + gg, s.b + b, s.n + 1);
                }
            var clusters = new List<(double r, double g, double b, int n)>();
            foreach (var s in sums.Values.OrderByDescending(v => v.n))
            {
                (double r, double g, double b) c = (s.r / s.n, s.g / s.n, s.b / s.n);
                var i = clusters.FindIndex(k => Math.Pow(k.r - c.r, 2) + Math.Pow(k.g - c.g, 2) + Math.Pow(k.b - c.b, 2) < 0.03);
                if (i >= 0)
                {
                    var k = clusters[i]; var n = k.n + s.n;
                    clusters[i] = ((k.r * k.n + s.r) / n, (k.g * k.n + s.g) / n, (k.b * k.n + s.b) / n, n);
                }
                else clusters.Add((c.r, c.g, c.b, s.n));
            }
            var sig = clusters.Where(c => c.n / (double)(W * H) >= 0.04).ToList();
            if (sig.Count == 0) sig = clusters.Take(3).ToList();
            var cols = sig.Select(c => ToHsl(c.r, c.g, c.b)).ToList();
            var dark = cols.MinBy(c => c.L);
            var light = cols.MaxBy(c => c.L);
            var vivid = cols.Where(c => c.S > 0.25 && c.L > 0.15 && c.L < 0.85).ToList();
            var accent = vivid.Count > 0 ? vivid.MaxBy(c => c.S) : dark;

            var ls = Math.Min(light.S, 0.65);
            double inkS = Math.Min(dark.S, 0.7), inkL = Math.Clamp(dark.L, 0.10, 0.22);
            var vars = new Dictionary<string, string>
            {
                ["ink"] = Css(dark.H, inkS, inkL),
                ["pink"] = Css(light.H, ls, Math.Clamp(light.L, 0.78, 0.86)),
                ["pink-soft"] = Css(light.H, ls, 0.89),
                ["pink-wash"] = Css(light.H, ls, 0.94),
                ["paper"] = Css(light.H, Math.Min(ls, 0.5), 0.975),
                ["cream"] = Css(light.H, Math.Min(ls, 0.5), 0.975),
                ["rose"] = Css(accent.H, Math.Clamp(accent.S, 0.5, 0.75), 0.45),
                ["muted"] = Css(dark.H, dark.S * 0.45, 0.45),
            };
            return (vars, ToColor(dark.H, inkS, inkL));
        }
        catch (Exception e) { Vault.Log($"theme failed: {e.Message}"); return null; }
    }
}
