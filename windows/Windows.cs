// The app's two windows: the desktop widget and the gallery. Both host the shared web pages.
using System.Runtime.InteropServices;
using System.Text.Json;
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

/// Small borderless window that sits on the desktop, below every other window.
sealed class WidgetForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 4000 };
    Dictionary<string, string>? _theme;
    string _wallpaperKey = "";
    bool _themeBusy;
    public Action OnOpenGallery = () => { };
    public Action OnHidden = () => { };
    public Action<Point> OnMoved = _ => { };

    public WidgetForm(Point? location)
    {
        Text = "Screenshot Vault Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(255, 248, 246);
        Icon = Web.AppIcon();
        var scale = DeviceDpi / 96.0;
        ClientSize = new Size((int)(316 * scale), (int)(366 * scale));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var pos = location ?? new Point(wa.Right - Width - (int)(24 * scale), wa.Top + (int)(24 * scale));
        // Don't restore onto a monitor that's no longer there.
        Location = Screen.AllScreens.Any(s => s.WorkingArea.Contains(pos)) ? pos : new Point(wa.Right - Width - 24, wa.Top + 24);
        Controls.Add(_web);

        _menu.Items.Add("Reload", null, (_, _) => _web.Reload());
        _menu.Items.Add("Open Gallery", null, (_, _) => OnOpenGallery());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Hide Widget", null, (_, _) => { Hide(); OnHidden(); });

        Load += async (_, _) => await Init();
        Move += (_, _) => { if (Visible) OnMoved(Location); };
        _themeTimer.Tick += (_, _) => RefreshTheme();
    }

    // A tool window: no taskbar button, not in Alt+Tab.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    // Keep the widget behind other windows even when it's clicked.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_WINDOWPOSCHANGING)
        {
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
            wp.hwndInsertAfter = Native.HWND_BOTTOM;
            wp.flags &= ~0x4u; // SWP_NOZORDER
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    async Task Init()
    {
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
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
            var msg = e.TryGetWebMessageAsString();
            if (msg == "drag")
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            }
            else if (msg == "menu") _menu.Show(Cursor.Position);
        };
        _web.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) ApplyTheme();
            else Task.Delay(3000).ContinueWith(_ => BeginInvoke(Reload)); // server still starting
        };
        Reload();
        RefreshTheme();
        _themeTimer.Start();
    }

    void Reload() => _web.CoreWebView2?.Navigate(Server.BaseUrl + "widget");

    void RefreshTheme()
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
                _theme = th.vars;
                var border = th.ink.R | (th.ink.G << 8) | (th.ink.B << 16);
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
                ApplyTheme();
            });
        });
    }

    void ApplyTheme()
    {
        if (_theme is null || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.applyTheme && applyTheme({JsonSerializer.Serialize(_theme)})");
    }
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
        BackColor = Color.FromArgb(248, 244, 251);
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
}
