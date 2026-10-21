// The app's two windows: the desktop widget and the gallery. Both host the shared web pages,
// styled with the Windows theme chosen in the tray menu.
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreenshotVault;

static class Web
{
    static Task<CoreWebView2Environment>? _env;

    /// One shared browser engine, with its data kept in the vault folder rather than next to the .exe.
    public static Task<CoreWebView2Environment> Env() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Root, "WebView2"));

    public static async Task Setup(WebView2 web)
    {
        await web.EnsureCoreWebView2Async(await Env());
        var s = web.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
    }

    public static Icon AppIcon()
    {
        using var s = typeof(Web).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(s);
    }
}

/// Small borderless window that sits on the desktop: above the wallpaper, below your apps.
/// Drag it by its title bar; drag the bottom-right grip to resize it.
sealed class WidgetForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly ContextMenuStrip _menu = new();
    // Y2K edition: colours follow the wallpaper, checked every few seconds.
    readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 4000 };
    Dictionary<string, string>? _wallpaperColours;
    Color _ink = Color.FromArgb(0x5A, 0x11, 0x24);
    string _wallpaperKey = "";
    bool _themeBusy;
    // Resizing from the grip.
    readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 15 };
    Point _resizeFrom;
    Size _sizeFrom;

    public Action OnOpenGallery = () => { };
    public Action OnHidden = () => { };
    public Action<Point> OnMoved = _ => { };
    public Action<Size> OnResized = _ => { };

    public WidgetForm(Point? location, Size? size)
    {
        Text = "Screenshot Vault Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Icon = Web.AppIcon();
        var scale = DeviceDpi / 96.0;
        // The XP edition's widget is 50% bigger (its theme also uses larger text and cards).
        var k = Edition.IsXP ? 1.5 : 1.0;
        MinimumSize = new Size((int)(250 * k * scale), (int)(290 * k * scale));
        // A saved size smaller than the minimum (e.g. from before the XP widget grew) falls back to the default.
        ClientSize = size is { } sz && sz.Width >= MinimumSize.Width && sz.Height >= MinimumSize.Height
            ? sz : new Size((int)(316 * k * scale), (int)(366 * k * scale));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var pos = location ?? new Point(wa.Right - Width - (int)(24 * scale), wa.Top + (int)(24 * scale));
        // Don't restore onto a monitor that's no longer there.
        Location = Screen.AllScreens.Any(s => s.WorkingArea.Contains(pos)) ? pos : new Point(wa.Right - Width - 24, wa.Top + 24);
        Controls.Add(_web);
        SetLookColours(Server.CurrentTheme());

        _menu.Items.Add("Reload", null, (_, _) => _web.Reload());
        _menu.Items.Add("Open Gallery", null, (_, _) => OnOpenGallery());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Hide Widget", null, (_, _) => { Hide(); OnHidden(); });

        Load += async (_, _) => await Init();
        Move += (_, _) => { if (Visible) OnMoved(Location); };
        _themeTimer.Tick += (_, _) => RefreshWallpaperColours();
        _resizeTimer.Tick += (_, _) => ResizeStep();
    }

    // A tool window: no taskbar button, not in Alt+Tab.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    // Appearing on the desktop shouldn't steal focus from what you're doing.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Belong to the desktop window: owned windows always sit just above their owner, so the
        // widget stays on top of the wallpaper but under your apps (and survives Win+D).
        var desktop = Native.GetShellWindow();
        if (desktop != IntPtr.Zero) Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, desktop);
        Vault.Log($"widget window created, desktop owner {(desktop != IntPtr.Zero ? "set" : "not found")}");
    }

    // Clicking the widget would normally bring it in front of other windows; keep it where it is.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_WINDOWPOSCHANGING && Visible)
        {
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
            wp.flags |= Native.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Vault.Log($"widget shown at {Bounds}, visible {Visible}");
    }

    async Task Init()
    {
        SetFrame(Server.CurrentTheme());
        try { await Web.Setup(_web); }
        catch (Exception e)
        {
            Vault.Log($"widget webview failed: {e.Message}");
            return;
        }
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.DefaultBackgroundColor = BackColor;
        _web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "drag":
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                    break;
                case "resize":
                    _resizeFrom = Cursor.Position;
                    _sizeFrom = Size;
                    _resizeTimer.Start();
                    break;
                case "menu":
                    _menu.Show(Cursor.Position);
                    break;
            }
        };
        _web.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) ApplyWallpaperColours();
            else Task.Delay(3000).ContinueWith(_ => BeginInvoke(Reload)); // server still starting
        };
        Reload();
        if (Server.CurrentTheme() == "y2k")
        {
            RefreshWallpaperColours();
            _themeTimer.Start();
        }
    }

    // ---------- resizing ----------

    // Follows the mouse until the button is let go (the page's grip started it).
    void ResizeStep()
    {
        if (!Native.LeftButtonDown)
        {
            _resizeTimer.Stop();
            OnResized(ClientSize);
            Vault.Log($"widget resized to {ClientSize}");
            return;
        }
        var p = Cursor.Position;
        var w = Math.Clamp(_sizeFrom.Width + p.X - _resizeFrom.X, MinimumSize.Width, 1600);
        var h = Math.Clamp(_sizeFrom.Height + p.Y - _resizeFrom.Y, MinimumSize.Height, 1300);
        if (w != Width || h != Height) Size = new Size(w, h);
    }

    // ---------- look ----------

    void SetLookColours(string theme)
    {
        BackColor = theme switch
        {
            "y2k" => Color.FromArgb(0xFF, 0xF8, 0xF6),
            "bw" => Color.White,
            _ => Color.FromArgb(236, 233, 216),
        };
        if (_web.CoreWebView2 != null) _web.DefaultBackgroundColor = BackColor;
    }

    /// The window frame (Windows 11; Windows 10 ignores this): rounded with a border in the theme's
    /// colour — the wallpaper's darkest colour (Y2K), Luna blue (XP) — or square and black (B&W).
    void SetFrame(string theme)
    {
        if (!IsHandleCreated) return;
        var corners = theme == "bw" ? 1 : Native.DWMWCP_ROUND; // 1 = DWMWCP_DONOTROUND
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        var c = theme switch { "y2k" => _ink, "bw" => Color.Black, _ => Color.FromArgb(0x08, 0x31, 0xD9) };
        var border = c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        SetLookColours(theme);
    }

    /// Called when the theme is changed from the tray menu (XP edition).
    public void ApplyLook(string theme)
    {
        if (!IsHandleCreated) return;
        SetFrame(theme);
        Reload();
    }

    // Y2K edition: re-colour from the wallpaper whenever it changes.
    void RefreshWallpaperColours()
    {
        if (_themeBusy) return;
        _themeBusy = true;
        var last = _wallpaperKey;
        Task.Run(() =>
        {
            var path = Theme.WallpaperPath();
            var key = path is null ? "" : $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
            var t = key == last || path is null ? null : Theme.FromImage(path);
            BeginInvoke(() =>
            {
                _themeBusy = false;
                if (key == last) return;
                _wallpaperKey = key;
                if (t is not { } th) return; // unreadable wallpaper: keep current colours
                _wallpaperColours = th.vars;
                _ink = th.ink;
                SetFrame("y2k");
                ApplyWallpaperColours();
            });
        });
    }

    void ApplyWallpaperColours()
    {
        if (_wallpaperColours is null || _web.CoreWebView2 is null || Server.CurrentTheme() != "y2k") return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(
            $"window.applyTheme && applyTheme({System.Text.Json.JsonSerializer.Serialize(_wallpaperColours)})");
    }

    void Reload() => _web.CoreWebView2?.Navigate(Server.BaseUrl + "widget");
}

/// The main gallery window.
sealed class GalleryForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    public bool Quitting;

    public GalleryForm()
    {
        Text = "Screenshot Vault";
        Icon = Web.AppIcon();
        var scale = DeviceDpi / 96.0;
        Size = new Size((int)(1180 * scale), (int)(800 * scale));
        MinimumSize = new Size((int)(420 * scale), (int)(420 * scale));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Server.CurrentTheme() switch
        {
            "y2k" => Color.FromArgb(0xF8, 0xF4, 0xFB), "bw" => Color.White, _ => Color.FromArgb(236, 233, 216),
        };
        Controls.Add(_web);
        Load += async (_, _) =>
        {
            try
            {
                await Web.Setup(_web);
                _web.CoreWebView2.Navigate(Server.BaseUrl);
            }
            catch (Exception e)
            {
                Vault.Log($"gallery webview failed: {e.Message}");
                MessageBox.Show("Screenshot Vault needs Microsoft Edge WebView2, which is part of Windows 10/11. " +
                                "Install it from go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.",
                                "Screenshot Vault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        // Closing just hides it, so it reopens instantly.
        FormClosing += (_, e) => { if (!Quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void ShowAndFocus()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _web.CoreWebView2?.Reload();
    }

    /// Called when the theme is changed from the tray menu.
    public void ApplyLook(string theme)
    {
        BackColor = theme == "bw" ? Color.White : Color.FromArgb(236, 233, 216);
        _web.CoreWebView2?.Reload();
    }
}
