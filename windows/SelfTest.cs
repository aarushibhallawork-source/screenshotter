// `ScreenshotVault.exe --selftest <folder>`: exercises the vault, server and theme on a real
// Windows machine without any UI. GitHub runs it on every build; results go to <folder>\selftest.txt.
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text;

namespace ScreenshotVault;

static class SelfTest
{
    public static int Run(string dir)
    {
        var log = new StringBuilder();
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
            if (!ok) failed++;
        }

        try
        {
            Directory.CreateDirectory(dir);
            Paths.Root = Path.Combine(Path.GetFullPath(dir), "vault");
            Vault.Shared.Prepare();

            // A fake Snipping Tool screenshot.
            var src = Path.Combine(dir, "Screenshot 2026-10-19 143205.png");
            using (var bmp = new Bitmap(320, 200))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(92, 18, 37));
                    for (var x = 0; x < 320; x += 40) g.FillRectangle(new SolidBrush(Color.FromArgb(244, 191, 198)), x, 0, 20, 200);
                }
                bmp.Save(src, ImageFormat.Png);
            }

            var dest = Vault.Shared.Ingest(src, "Test: App", false);
            Check("ingest moves the file", dest != null && File.Exists(dest) && !File.Exists(src), dest ?? "null");
            var rel = dest is null ? "" : Vault.Rel(dest);
            Check("readable name, filed by date", rel == "2026-10/2026-10-19/2026-10-19 at 14.32.05 - Test- App.png", rel);
            Check("thumbnail made", File.Exists(Path.Combine(Paths.Thumbs, rel.Replace('/', '\\') + ".jpg")));

            var items = Vault.Shared.ListItems();
            Check("listed with app name", items.Count == 1 && (string)items[0]["app"]! == "Test- App",
                  items.Count > 0 ? (string)items[0]["app"]! : "none");

            Check("path escape blocked", Vault.Safe("../settings.json") is null && Vault.Safe("2026-10/../../x") is null);
            Check("normal path allowed", Vault.Safe(rel) != null);

            var theme = dest is null ? null : Theme.FromImage(dest);
            Check("wallpaper colours extracted", theme is { } t && t.vars.Count == 8,
                  theme is { } t2 ? $"ink {t2.vars["ink"]}, panel {t2.vars["pink"]}" : "null");

            var server = new Server();
            server.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var list = http.GetStringAsync(Server.BaseUrl + "api/list").Result;
            Check("server lists items", list.Contains("Test- App"));
            var page = http.GetAsync(Server.BaseUrl).Result;
            Check("gallery page served", page.IsSuccessStatusCode && page.Content.ReadAsStringAsync().Result.Contains("screenshot"));
            var widget = http.GetAsync(Server.BaseUrl + "widget").Result;
            Check("widget page served", widget.IsSuccessStatusCode);
            var file = http.GetAsync(Server.BaseUrl + "files/" + Uri.EscapeDataString(rel).Replace("%2F", "/")).Result;
            Check("image served", file.IsSuccessStatusCode && file.Content.Headers.ContentType?.MediaType == "image/png",
                  ((int)file.StatusCode).ToString());
            var evil = new HttpRequestMessage(HttpMethod.Post, Server.BaseUrl + "api/delete") { Content = new StringContent("{\"paths\":[]}") };
            evil.Headers.Add("Origin", "https://evil.example");
            Check("other websites refused", (int)http.SendAsync(evil).Result.StatusCode == 403);

            var copied = Vault.Shared.CopyToClipboard(dest!);
            log.AppendLine($"INFO  clipboard copy: {(copied ? "worked" : "not available on this machine")}");

            var del = new HttpRequestMessage(HttpMethod.Post, Server.BaseUrl + "api/delete")
                { Content = new StringContent($"{{\"paths\":[\"{rel}\"]}}") };
            http.SendAsync(del).Wait();
            Check("move to Recycle Bin", !File.Exists(dest!) && Vault.Shared.ListItems().Count == 0);
            server.Stop();
        }
        catch (Exception e)
        {
            Check("no crash", false, e.ToString());
        }

        log.AppendLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
        return failed == 0 ? 0 : 1;
    }
}
