// The vault: picks up new screenshots from Windows' Screenshots folder, copies each to the
// clipboard, files it under archive\YYYY-MM\YYYY-MM-DD with a readable name, and manages the archive.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace ScreenshotVault;

static class Paths
{
    // SCREENSHOTVAULT_ROOT lets the automated GitHub check run the app against a test vault.
    public static string Root = Environment.GetEnvironmentVariable("SCREENSHOTVAULT_ROOT") is { Length: > 0 } r ? r
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotVault");
    static readonly string Legacy = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotVault", "archive");
    static string? _archive;

    /// Where screenshots are kept: a normal folder on the Desktop, so there's always a copy you can see.
    /// (App data like thumbnails and settings stays hidden in Root.) Test runs keep everything inside Root.
    public static string Archive
    {
        get => _archive ??= Environment.GetEnvironmentVariable("SCREENSHOTVAULT_ROOT") is { Length: > 0 }
            ? Path.Combine(Root, "archive")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screenshot Vault");
        set => _archive = value;
    }

    /// Earlier versions kept screenshots hidden in %LOCALAPPDATA%; move them to the Desktop folder.
    public static void MoveLegacyArchive()
    {
        if (!Directory.Exists(Legacy) || string.Equals(Path.GetFullPath(Legacy), Path.GetFullPath(Archive), StringComparison.OrdinalIgnoreCase)) return;
        var moved = 0;
        foreach (var f in Directory.EnumerateFiles(Legacy, "*", System.IO.SearchOption.AllDirectories).ToList())
        {
            var dest = Path.Combine(Archive, Path.GetRelativePath(Legacy, f));
            if (File.Exists(dest)) continue;
            try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Move(f, dest); moved++; }
            catch (Exception e) { Vault.Log($"couldn't move {f}: {e.Message}"); }
        }
        foreach (var d in Directory.EnumerateDirectories(Legacy, "*", System.IO.SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        if (!Directory.EnumerateFileSystemEntries(Legacy).Any()) Directory.Delete(Legacy);
        if (moved > 0) Vault.Log($"moved {moved} screenshots to {Archive}");
    }
    public static string Thumbs => Path.Combine(Root, "thumbs");
    public static string Config => Path.Combine(Root, "config.json");

    /// Where Windows saves screenshots (Win+PrtScn, and Snipping Tool's auto-save). Follows
    /// the folder if it's been moved, e.g. into OneDrive.
    public static string Screenshots
    {
        get
        {
            var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"); // FOLDERID_Screenshots
            if (Native.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                var path = Marshal.PtrToStringUni(p)!;
                Marshal.FreeCoTaskMem(p);
                return path;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }
}

sealed class Vault
{
    public static readonly Vault Shared = new();
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" };
    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
    static bool IsMedia(string p) { var e = Path.GetExtension(p); return ImageExt.Contains(e) || VideoExt.Contains(e); }

    // Copying to the clipboard has to happen on the UI thread.
    public SynchronizationContext? Ui;
    int _version;
    public int Version => Volatile.Read(ref _version);
    void Bump() => Interlocked.Increment(ref _version);
    readonly object _ingestLock = new();
    System.Threading.Timer? _poll, _retention;

    public void Prepare()
    {
        try { Paths.MoveLegacyArchive(); } catch (Exception e) { Log($"legacy move failed: {e.Message}"); }
        Directory.CreateDirectory(Paths.Archive);
        Directory.CreateDirectory(Paths.Thumbs);
    }

    // ---------- config ----------

    public JsonObject Config()
    {
        try { return JsonNode.Parse(File.ReadAllText(Paths.Config)) as JsonObject ?? Default(); }
        catch { return Default(); }
        static JsonObject Default() => new() { ["retention_days"] = 0 };
    }

    public JsonObject SetRetention(int days)
    {
        var c = Config();
        c["retention_days"] = Math.Max(0, days);
        File.WriteAllText(Paths.Config, c.ToJsonString());
        return c;
    }

    // ---------- naming ----------

    static readonly Regex VaultName = new(@"^(\d{4}-\d{2}-\d{2}) at (\d{2})\.(\d{2})\.(\d{2})(?: - (.*?))?(?: \(\d+\))?$");
    // Snipping Tool: "Screenshot 2026-10-19 143205"
    static readonly Regex SnipName = new(@"(\d{4}-\d{2}-\d{2})[ _](\d{2})(\d{2})(\d{2})");

    static DateTime CaptureTime(string file)
    {
        var m = SnipName.Match(Path.GetFileNameWithoutExtension(file));
        if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}:{m.Groups[3].Value}:{m.Groups[4].Value}",
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        var c = File.GetCreationTime(file);
        var w = File.GetLastWriteTime(file);
        return c < w ? c : w;
    }

    static string CleanLabel(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(ch => bad.Contains(ch) ? '-' : ch).ToArray()).Trim(' ', '.', '-');
        return clean.Length > 60 ? clean[..60] : clean;
    }

    static string ArchivePath(DateTime when, string label, string ext)
    {
        var dir = Path.Combine(Paths.Archive, when.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                               when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var name = when.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);
        if (label.Length > 0) name += " - " + label;
        var dest = Path.Combine(dir, name + ext);
        for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(dir, $"{name} ({n}){ext}");
        return dest;
    }

    // ---------- paths ----------

    /// Archive-relative path with forward slashes, as the web pages use.
    public static string Rel(string full) => Path.GetRelativePath(Paths.Archive, full).Replace('\\', '/');

    /// Resolves a relative path inside `root`, refusing anything that escapes it.
    public static string? Safe(string rel, string? root = null)
    {
        root = Path.GetFullPath(root ?? Paths.Archive);
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static string ThumbPath(string rel) => Path.Combine(Paths.Thumbs, rel.Replace('/', Path.DirectorySeparatorChar) + ".jpg");

    // ---------- capture handling ----------

    static void MakeThumb(string file)
    {
        if (!ImageExt.Contains(Path.GetExtension(file))) return;
        try
        {
            using var src = LoadImage(file);
            var scale = Math.Min(1.0, 640.0 / Math.Max(src.Width, src.Height));
            using var bmp = new Bitmap(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            }
            var outPath = ThumbPath(Rel(file));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
            bmp.Save(outPath, jpeg, p);
        }
        catch (Exception e) { Log($"thumb failed: {e.Message}"); }
    }

    /// Loads an image without keeping the file locked.
    static Image LoadImage(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var tmp = Image.FromStream(fs);
        return new Bitmap(tmp);
    }

    public bool CopyToClipboard(string file)
    {
        var ok = false;
        void Work()
        {
            try
            {
                if (ImageExt.Contains(Path.GetExtension(file)))
                {
                    using var img = LoadImage(file);
                    var data = new DataObject();
                    data.SetImage(img);
                    // Also offer PNG, which keeps transparency and is preferred by many apps.
                    var png = new MemoryStream();
                    img.Save(png, ImageFormat.Png);
                    data.SetData("PNG", false, png);
                    Clipboard.SetDataObject(data, true, 5, 100);
                }
                else
                {
                    // Recordings: copy as a file so it pastes into Explorer, chat apps, email…
                    Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
                }
                ok = true;
            }
            catch (Exception e) { Log($"clipboard failed: {e.Message}"); }
        }
        if (Ui is null || SynchronizationContext.Current == Ui) Work(); else Ui.Send(_ => Work(), null);
        return ok;
    }

    static bool IsReady(string file)
    {
        try
        {
            var a = new FileInfo(file).Length;
            Thread.Sleep(200);
            if (a == 0 || new FileInfo(file).Length != a) return false;
            using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); // still being written?
            return true;
        }
        catch { return false; }
    }

    public string? Ingest(string file, string label, bool toClipboard)
    {
        try
        {
            var dest = ArchivePath(CaptureTime(file), CleanLabel(label), Path.GetExtension(file).ToLowerInvariant());
            File.Move(file, dest);
            if (toClipboard) CopyToClipboard(dest);
            MakeThumb(dest);
            return dest;
        }
        catch (Exception e) { Log($"ingest failed for {file}: {e.Message}"); return null; }
    }

    // Snipping Tool / Explorer are often in front at the moment a capture is saved; they say nothing useful.
    static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
        { "ScreenClippingHost", "SnippingTool", "ScreenSketch", "explorer", "ShellExperienceHost", "ScreenshotVault", "SearchHost", "StartMenuExperienceHost" };

    static string ForegroundApp()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            if (NotApps.Contains(p.ProcessName)) return "";
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc!;
        }
        catch { return ""; }
    }

    // ---------- watching ----------

    FileSystemWatcher? _watcher;
    public string? WatchedFolder { get; private set; }

    public void StartWatching()
    {
        var folder = Paths.Screenshots;
        Directory.CreateDirectory(folder);
        WatchedFolder = folder;
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _watcher.Created += (_, _) => Task.Run(Sweep);
        _watcher.Renamed += (_, _) => Task.Run(Sweep);
        // Belt and braces: the watcher can miss events (e.g. OneDrive folders), so also sweep every few seconds.
        _poll = new System.Threading.Timer(_ => Sweep(), null, 1000, 3000);
        _retention = new System.Threading.Timer(_ => ApplyRetention(), null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
    }

    void Sweep()
    {
        if (WatchedFolder is null || !Monitor.TryEnter(_ingestLock)) return;
        try
        {
            var files = Directory.EnumerateFiles(WatchedFolder)
                .Where(f => IsMedia(f) && !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(File.GetLastWriteTimeUtc).ToList();
            if (files.Count == 0) return;
            var app = ForegroundApp();
            for (var i = 0; i < files.Count; i++)
            {
                if (!IsReady(files[i])) continue;
                // Only the newest goes to the clipboard, and only if it's fresh (not a backlog from earlier).
                var fresh = DateTime.Now - File.GetLastWriteTime(files[i]) < TimeSpan.FromSeconds(30);
                var dest = Ingest(files[i], fresh ? app : "", i == files.Count - 1 && fresh);
                if (dest != null) Log($"filed {dest}");
            }
            Bump();
        }
        catch (Exception e) { Log($"sweep failed: {e.Message}"); }
        finally { Monitor.Exit(_ingestLock); }
    }

    void ApplyRetention()
    {
        var days = Config()["retention_days"]?.GetValue<int>() ?? 0;
        if (days <= 0) return;
        var cutoff = DateTimeOffset.Now.AddDays(-days).ToUnixTimeSeconds();
        var old = ListItems().Where(i => (double)i["ts"]! < cutoff).Select(i => (string)i["path"]!).ToList();
        if (old.Count > 0) { Trash(old); Log($"retention removed {old.Count}"); }
    }

    /// Files whatever is already sitting in the Screenshots folder.
    public int ImportExisting()
    {
        if (WatchedFolder is null) return 0;
        var n = 0;
        lock (_ingestLock)
        {
            foreach (var f in Directory.EnumerateFiles(WatchedFolder).Where(IsMedia).ToList())
                if (Ingest(f, "", false) != null) n++;
        }
        Bump();
        return n;
    }

    public int CountExisting() =>
        WatchedFolder is null || !Directory.Exists(WatchedFolder) ? 0 : Directory.EnumerateFiles(WatchedFolder).Count(IsMedia);

    // ---------- library ----------

    public List<JsonObject> ListItems()
    {
        var items = new List<JsonObject>();
        if (!Directory.Exists(Paths.Archive)) return items;
        foreach (var f in Directory.EnumerateFiles(Paths.Archive, "*", System.IO.SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            if (!IsMedia(f) && !ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(f);
            var rel = Rel(f);
            double ts = new DateTimeOffset(info.LastWriteTime).ToUnixTimeSeconds();
            var app = "";
            var m = VaultName.Match(Path.GetFileNameWithoutExtension(f));
            if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}:{m.Groups[3].Value}:{m.Groups[4].Value}",
                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            {
                ts = new DateTimeOffset(d).ToUnixTimeSeconds();
                app = m.Groups[5].Value;
            }
            items.Add(new JsonObject
            {
                ["path"] = rel, ["name"] = info.Name, ["app"] = app, ["ts"] = ts,
                ["kind"] = ImageExt.Contains(ext) ? "image" : VideoExt.Contains(ext) ? "video" : "file",
                ["size"] = info.Length, ["thumb"] = File.Exists(ThumbPath(rel)),
            });
        }
        return items.OrderByDescending(i => (double)i["ts"]!).ToList();
    }

    /// Moves files to the Recycle Bin (so they can be restored) and tidies empty folders.
    public void Trash(IEnumerable<string> rels)
    {
        foreach (var rel in rels)
        {
            var full = Safe(rel);
            if (full is null || !File.Exists(full)) continue;
            try { FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
            catch (Exception e) { Log($"trash failed: {e.Message}"); continue; }
            try { File.Delete(ThumbPath(rel)); } catch { }
            var dir = Path.GetDirectoryName(full);
            while (dir != null && dir.Length > Paths.Archive.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        Bump();
    }

    // ---------- log ----------

    static readonly object LogLock = new();
    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Paths.Root);
                File.AppendAllText(Path.Combine(Paths.Root, "vault.log"), $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
