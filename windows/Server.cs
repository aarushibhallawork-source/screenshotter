// A tiny HTTP server on localhost:7333 that serves the gallery and widget pages
// and the JSON API they call. Only reachable from this PC.
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotVault;

sealed class Server
{
    public const int Port = 7333;
    public static readonly string BaseUrl = $"http://localhost:{Port}/";
    public Action OnOpenGallery = () => { };
    /// The current look: "y2k" (same as the Mac app), or in the XP edition "xp" / "bw".
    public static Func<string> CurrentTheme = () => Edition.IsXP ? "xp" : "y2k";
    HttpListener? _listener;

    public void Start()
    {
        _listener = new HttpListener();
        // "localhost" is the one prefix Windows lets a normal (non-admin) app listen on.
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    public void Stop() { try { _listener?.Stop(); } catch { } }

    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".pdf"] = "application/pdf",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime",
    };

    void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var (status, body, type, cache) = Route(ctx.Request);
            res.StatusCode = status;
            res.ContentType = type;
            res.Headers["Cache-Control"] = cache ? "max-age=3600" : "no-store";
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body);
        }
        catch (Exception e) { Vault.Log($"request failed: {e.Message}"); try { res.StatusCode = 500; } catch { } }
        finally { try { res.Close(); } catch { } }
    }

    static (int, byte[], string, bool) Json(object obj, int status = 200)
    {
        var json = obj is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(obj);
        return (status, Encoding.UTF8.GetBytes(json), "application/json", false);
    }

    static readonly (int, byte[], string, bool) NotFound = (404, Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"), "application/json", false);

    static (int, byte[], string, bool) FileResponse(string? path)
    {
        if (path is null || !File.Exists(path)) return NotFound;
        var bytes = File.ReadAllBytes(path);
        return (200, bytes, Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"), true);
    }

    static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s is null) return "";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static List<(string from, string to)>? _wording;

    /// The shared pages, dressed for Windows: plain wording, plus the chosen theme's stylesheet.
    public static string ThemedPage(string name, string theme)
    {
        var html = Resource(name + ".html");
        if (html.Length == 0 || theme == "y2k") return html; // the original look and wording
        _wording ??= JsonSerializer.Deserialize<List<List<string>>>(Resource("theme-text.json"))!
            .Where(p => p.Count == 2 && p[0].Length > 0 && !p[0].StartsWith("Swaps "))
            .Select(p => (p[0], p[1])).ToList();
        foreach (var (from, to) in _wording) html = html.Replace(from, to);
        html = ReplaceFirst(html, "<html lang=\"en\"", $"<html lang=\"en\" data-theme=\"{theme}\" data-page=\"{name}\"");
        return ReplaceFirst(html, "</head>", $"<link rel=\"stylesheet\" href=\"/theme.css?t={theme}\"></head>");
    }

    static string ReplaceFirst(string s, string find, string repl)
    {
        var i = s.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + repl + s[(i + find.Length)..];
    }

    static (int, byte[], string, bool) Page(string name)
    {
        var html = ThemedPage(name, CurrentTheme());
        return html.Length == 0 ? NotFound : (200, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", false);
    }

    (int, byte[], string, bool) Route(HttpListenerRequest req)
    {
        var path = Uri.UnescapeDataString(req.Url!.AbsolutePath);
        var vault = Vault.Shared;

        if (req.HttpMethod == "GET")
        {
            switch (path)
            {
                case "/": return Page("gallery");
                case "/widget": return Page("widget");
                case "/theme.css":
                    var css = CurrentTheme() switch { "y2k" => "", "bw" => Resource("theme-bw.css"), _ => Resource("theme-xp.css") };
                    return (200, Encoding.UTF8.GetBytes(css), "text/css; charset=utf-8", false);
                case "/api/version": return Json(new JsonObject { ["version"] = vault.Version });
                case "/api/list":
                    var items = vault.ListItems();
                    var arr = new JsonArray(items.Select(i => (JsonNode)i).ToArray());
                    return Json(new JsonObject
                    {
                        ["items"] = arr, ["total"] = items.Sum(i => (long)i["size"]!),
                        ["version"] = vault.Version, ["config"] = vault.Config(),
                    });
            }
            if (path.StartsWith("/files/")) return FileResponse(Vault.Safe(path[7..]));
            if (path.StartsWith("/thumbs/")) return FileResponse(Vault.Safe(path[8..], Paths.Thumbs));
            return NotFound;
        }

        if (req.HttpMethod != "POST") return NotFound;
        // Only accept requests from our own pages.
        var origin = req.Headers["Origin"];
        if (origin != null && origin != $"http://localhost:{Port}" && origin != $"http://127.0.0.1:{Port}")
            return Json(new { error = "forbidden" }, 403);

        JsonObject body;
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
        {
            try { body = JsonNode.Parse(r.ReadToEnd()) as JsonObject ?? new JsonObject(); }
            catch { body = new JsonObject(); }
        }
        var target = body["path"]?.GetValue<string>() is { } p ? Vault.Safe(p) : null;

        switch (path)
        {
            case "/api/copy":
                if (target is null || !File.Exists(target)) return NotFound;
                return Json(new { ok = vault.CopyToClipboard(target) });
            case "/api/reveal":
                if (target is null) return NotFound;
                Process.Start("explorer.exe", $"/select,\"{target}\"");
                return Json(new { ok = true });
            case "/api/open":
                if (target is null) return NotFound;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return Json(new { ok = true });
            case "/api/openvault":
                OnOpenGallery();
                return Json(new { ok = true });
            case "/api/delete":
                var paths = (body["paths"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? new();
                vault.Trash(paths);
                return Json(new { ok = true });
            case "/api/config":
                var days = 0;
                try { days = body["retention_days"]?.GetValue<int>() ?? 0; } catch { }
                return Json(new JsonObject { ["ok"] = true, ["config"] = vault.SetRetention(days) });
        }
        return NotFound;
    }
}
