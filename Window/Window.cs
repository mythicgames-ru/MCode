using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentApp.Window;

public abstract class AppWindow : Form
{
    protected const string Host = "https://app.local";
    private static readonly Color Chrome = Color.FromArgb(0x14, 0x15, 0x17);
    private static readonly Color ChromeText = Color.FromArgb(0xE8, 0xEA, 0xED);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _startPage;
    private readonly TaskCompletionSource _ready = new();
    public Task Ready => _ready.Task;

    protected AppWindow(string startPage, string title, int width, int height)
    {
        _startPage = startPage;
        Text = title;
        ClientSize = new Size(width, height);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Chrome;
        Controls.Add(_web);
        KeyPreview = true;
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentApp");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _web.EnsureCoreWebView2Async(env);

        try { _web.AllowExternalDrop = true; } catch { }

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;

        try { _web.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; } catch { }
        try
        {
            _web.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        }
        catch { }

        _web.CoreWebView2.AddWebResourceRequestedFilter($"{Host}/*", CoreWebView2WebResourceContext.All);
        _web.CoreWebView2.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                e.State = CoreWebView2PermissionState.Allow;
        };
        _web.CoreWebView2.WebResourceRequested += ServeEmbeddedFile;
        _web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            var m = e.TryGetWebMessageAsString();
            if (m == "__mcode_fullscreen__") { ToggleFullscreen(); return; }
            OnWebMessage(m);
        };
        _web.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _ready.TrySetResult();
            OnNavigated(_web.Source?.ToString() ?? "");
        };

        await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            "document.addEventListener('keydown', function(e){ if (e.key === 'F11') { e.preventDefault(); try { window.chrome.webview.postMessage('__mcode_fullscreen__'); } catch(_) {} } }, true);");
        _web.CoreWebView2.Navigate($"{Host}/{_startPage}");
        ApplyDarkTitleBar();
    }

    protected virtual void OnWebMessage(string message) { }
    protected virtual void OnNavigated(string uri) { }
    protected void SendToPage(string message) => _web.CoreWebView2.PostWebMessageAsString(message);
    protected void Navigate(string url) => _web.CoreWebView2.Navigate(url);

    protected static Icon? LoadIconFromResources(string fileName)
    {
        try
        {
            var asm = typeof(AppWindow).Assembly;
            string suffix = "." + fileName;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resName is null) return null;
            using var s = asm.GetManifestResourceStream(resName);
            if (s is null) return null;
            return new Icon(s);
        }
        catch { return null; }
    }

    private bool _isFullscreen = false;
    private Rectangle _prevBounds = Rectangle.Empty;
    private FormBorderStyle _prevBorder = FormBorderStyle.Sizable;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevBounds = Bounds;
            _prevBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            FormBorderStyle = _prevBorder;
            WindowState = FormWindowState.Normal;
            Bounds = _prevBounds;
            _isFullscreen = false;
        }
    }

    protected double CurrentZoom
    {
        get { try { return _web.ZoomFactor; } catch { return 1.0; } }
        set { try { _web.ZoomFactor = Math.Clamp(value, 0.3, 3.0); } catch { } }
    }

    private void ServeEmbeddedFile(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = _startPage;
        var asm = typeof(AppWindow).Assembly;
        string wanted = $"{asm.GetName().Name}.{path.Replace('/', '.')}";
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            var list = string.Join("", asm.GetManifestResourceNames()
                .Select(n => $"<li><code>{System.Net.WebUtility.HtmlEncode(n)}</code></li>"));
            string html = $"""
                <h2>404 — ресурс не найден</h2>
                <p>Искал: <code>{System.Net.WebUtility.HtmlEncode(wanted)}</code></p>
                <p>Ресурсы:</p><ul>{list}</ul>
                """;
            e.Response = MakeResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 404, "text/html; charset=utf-8");
            return;
        }
        e.Response = MakeResponse(asm.GetManifestResourceStream(name)!, 200, GetMime(path));
    }

    private CoreWebView2WebResourceResponse MakeResponse(Stream body, int status, string ct) =>
        _web.CoreWebView2.Environment.CreateWebResourceResponse(
            body, status, status == 200 ? "OK" : "Not Found",
            $"Content-Type: {ct}\n"
          + "Cache-Control: no-store, no-cache, must-revalidate, max-age=0\n"
          + "Pragma: no-cache\n"
          + "Expires: 0");

    private static string GetMime(string path) => Path.GetExtension(path.ToLowerInvariant()) switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs"   => "text/javascript",
        ".css"            => "text/css",
        ".json" or ".map" => "application/json",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".svg"            => "image/svg+xml",
        ".ico"            => "image/x-icon",
        ".woff"           => "font/woff",
        ".woff2"          => "font/woff2",
        ".ttf"            => "font/ttf",
        ".otf"            => "font/otf",
        _                 => "application/octet-stream"
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void TryDwm(int attr, int value)
    { try { DwmSetWindowAttribute(Handle, attr, ref value, sizeof(int)); } catch { } }

    private void ApplyDarkTitleBar()
    {
        int caption = ColorTranslator.ToWin32(Chrome);
        int text = ColorTranslator.ToWin32(ChromeText);
        TryDwm(35, caption); TryDwm(34, caption); TryDwm(36, text);
        TryDwm(20, 1); TryDwm(19, 1);
    }
}
