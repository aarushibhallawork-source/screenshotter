// Entry point and the system-tray app that ties everything together.
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace ScreenshotVault;

sealed class Settings
{
    public bool SetupDone { get; set; }
    public bool WidgetOn { get; set; } = true;
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }

    static string FilePath => Path.Combine(Paths.Root, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Paths.Root); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { }
    }
}

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ScreenshotVault";

    readonly Settings _settings = Settings.Load();
    readonly Server _server = new();
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    WidgetForm? _widget;
    GalleryForm? _gallery;

    public TrayApp()
    {
        Vault.Shared.Ui = SynchronizationContext.Current;
        Vault.Shared.Prepare();
        try { _server.Start(); }
        catch (Exception e)
        {
            Vault.Log($"server failed: {e.Message}");
            MessageBox.Show($"Screenshot Vault couldn't start: something else is using port {Server.Port}.\n\nRestart your PC and open Screenshot Vault again.",
                            "Screenshot Vault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.Exit(1);
        }
        _server.OnOpenGallery = () => Vault.Shared.Ui?.Post(_ => ShowGallery(), null);

        _tray = new NotifyIcon { Icon = Web.AppIcon(), Text = "Screenshot Vault", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowGallery(); };
        _menu.Opening += (_, _) => BuildMenu();

        if (_settings.SetupDone)
        {
            Vault.Shared.StartWatching();
            if (_settings.WidgetOn) ShowWidget();
        }
        else
        {
            Vault.Shared.Ui?.Post(_ => Welcome(), null);
        }
    }

    // ---------- first run ----------

    void Welcome()
    {
        var r = MessageBox.Show(
            "Welcome to Screenshot Vault ♡\n\n" +
            "From now on, every screenshot you take is copied to your clipboard right away and filed by date in your vault, " +
            "instead of piling up in Pictures\\Screenshots.\n\n" +
            "Use Win+Shift+S or Win+PrtScn. Find them any time in the gallery (the ♡ icon by the clock) or in the widget on your desktop.\n\n" +
            "Turn it on?",
            "Screenshot Vault", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes) { Quit(); return; }

        _settings.SetupDone = true;
        _settings.WidgetOn = true;
        _settings.Save();
        SetStartWithWindows(true);
        Vault.Shared.StartWatching();

        var n = Vault.Shared.CountExisting();
        if (n > 0 && MessageBox.Show(
                $"There {(n == 1 ? "is 1 screenshot" : $"are {n} screenshots")} in your Screenshots folder already. Move {(n == 1 ? "it" : "them")} into the vault?",
                "Screenshot Vault", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Vault.Shared.ImportExisting();

        ShowWidget();
        ShowGallery();
        _tray.ShowBalloonTip(4000, "Screenshot Vault is on ♡",
            "Tip: in Snipping Tool's settings, turn on \"Automatically save screenshots\" so Win+Shift+S snips are filed too.", ToolTipIcon.Info);
    }

    // ---------- start with Windows ----------

    static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; }
    }

    static void SetStartWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }

    // ---------- windows ----------

    void ShowGallery()
    {
        if (_gallery is null || _gallery.IsDisposed) _gallery = new GalleryForm();
        _gallery.ShowAndFocus();
    }

    void ShowWidget()
    {
        if (_widget is null || _widget.IsDisposed)
        {
            Point? pos = _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
            _widget = new WidgetForm(pos)
            {
                OnOpenGallery = ShowGallery,
                OnHidden = () => { _settings.WidgetOn = false; _settings.Save(); },
                OnMoved = p => { _settings.WidgetX = p.X; _settings.WidgetY = p.Y; _settings.Save(); },
            };
        }
        _widget.Show();
        _settings.WidgetOn = true;
        _settings.Save();
    }

    // ---------- tray menu ----------

    void BuildMenu()
    {
        _menu.Items.Clear();
        if (!_settings.SetupDone)
        {
            _menu.Items.Add("Turn On Screenshot Vault…", null, (_, _) => Welcome());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Quit Screenshot Vault", null, (_, _) => Quit());
            return;
        }
        var open = _menu.Items.Add("Open Gallery", null, (_, _) => ShowGallery());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(new ToolStripMenuItem("Show Desktop Widget", null, (_, _) =>
        {
            if (_widget is { Visible: true }) { _widget.Hide(); _settings.WidgetOn = false; _settings.Save(); }
            else ShowWidget();
        }) { Checked = _widget is { Visible: true } });
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartWithWindows(!StartsWithWindows))
            { Checked = StartsWithWindows });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Move Screenshots Folder into Vault", null, (_, _) =>
        {
            var n = Vault.Shared.ImportExisting();
            _tray.ShowBalloonTip(3000, "Screenshot Vault",
                n == 0 ? "Your Screenshots folder is already empty." : $"Moved {n} screenshot{(n == 1 ? "" : "s")} into the vault ♡", ToolTipIcon.Info);
        });
        _menu.Items.Add("Open Vault Folder", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Paths.Archive}\""));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Turn Off…", null, (_, _) => TurnOff());
        _menu.Items.Add("Quit Screenshot Vault", null, (_, _) => Quit());
    }

    void TurnOff()
    {
        var r = MessageBox.Show(
            "Turn off Screenshot Vault?\n\nScreenshots will stay in Pictures\\Screenshots as normal, and the app won't start with Windows any more.\n\n" +
            $"Your vault stays where it is ({Paths.Archive}). To remove the app completely, use Settings → Apps → Installed apps → Screenshot Vault → Uninstall.",
            "Screenshot Vault", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return;
        SetStartWithWindows(false);
        _settings.SetupDone = false;
        _settings.Save();
        Quit();
    }

    void Quit()
    {
        _tray.Visible = false;
        _server.Stop();
        if (_gallery != null) { _gallery.Quitting = true; _gallery.Close(); }
        _widget?.Close();
        ExitThread();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);

        using var mutex = new Mutex(true, "ScreenshotVault.SingleInstance", out var first);
        if (!first)
        {
            // Already running: ask that copy to open its gallery.
            try
            {
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                c.PostAsync(Server.BaseUrl + "api/openvault", new StringContent("{}")).Wait();
            }
            catch { }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
        GC.KeepAlive(mutex);
        return 0;
    }
}
