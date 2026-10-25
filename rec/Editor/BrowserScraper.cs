using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentApp.Editor;

/// <summary>
/// Скрытый WebView2 для парсинга страниц как настоящий браузер.
/// Окно видимо для Windows (но за пределами экрана, не minimized,
/// без Opacity=0) — иначе WebView2 не рендерит JS и анти-бот детектит его.
/// </summary>
internal sealed class BrowserScraper : IDisposable
{
    private static readonly object _lock = new();
    private static BrowserScraper? _shared;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Form _form;
    private readonly WebView2 _web;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private BrowserScraper()
    {
        // Ключевые моменты:
        // - Size 1280x900 (типичный ноутбучный) — Yandex смотрит на viewport
        // - Location = (10000, 10000) — за пределами любого экрана, но окно
        //   "видимо" для ОС, поэтому DWM его рендерит и JS работает как надо
        // - НЕ minimized, НЕ Opacity=0 (оба состояния WebView2 плохо рендерит
        //   и анти-бот это палит)
        // - ShowInTaskbar = false — не светится в панели задач
        _form = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Normal,
            Size = new Size(1280, 900),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(10000, 10000),
            FormBorderStyle = FormBorderStyle.None,
            Text = "",
            Opacity = 1.0,
        };
        _web = new WebView2 { Dock = DockStyle.Fill };
        _form.Controls.Add(_web);
        _form.Load += async (_, _) =>
        {
            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AgentApp", "browser-scraper");
                Directory.CreateDirectory(userData);

                var opts = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-features=msWebOOUI,msPdfOOUI "
                      + "--disable-blink-features=AutomationControlled "
                      + "--disable-infobars --no-default-browser-check "
                      + "--lang=ru-RU,ru"
                };
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userData,
                    options: opts);

                await _web.EnsureCoreWebView2Async(env);

                var s = _web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                try
                {
                    s.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                                + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                }
                catch { }

                // Убираем navigator.webdriver (главный маркер автоматизации)
                await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "Object.defineProperty(navigator,'webdriver',{get:()=>undefined});"
                  + "Object.defineProperty(navigator,'languages',{get:()=>['ru-RU','ru','en-US','en']});"
                  + "Object.defineProperty(navigator,'plugins',{get:()=>[1,2,3,4,5]});"
                );

                _ready.TrySetResult();
            }
            catch (Exception ex) { _ready.TrySetException(ex); }
        };
    }

    public static async Task<string> FetchAsync(Form owner, string url, int timeoutMs = 30000)
    {
        await _gate.WaitAsync();
        try
        {
            var inst = GetOrCreate(owner);
            return await inst.FetchCoreAsync(url, timeoutMs);
        }
        finally { _gate.Release(); }
    }

    private static BrowserScraper GetOrCreate(Form owner)
    {
        lock (_lock)
        {
            if (_shared is not null && !_shared._disposed) return _shared;
            BrowserScraper? created = null;
            void Make()
            {
                created = new BrowserScraper();
                created._form.Show();
            }
            if (owner.InvokeRequired) owner.Invoke((Action)Make);
            else Make();
            _shared = created!;
            return _shared;
        }
    }

    private async Task<string> FetchCoreAsync(string url, int timeoutMs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserScraper));

        var outerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _form.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _ready.Task;

                var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _web.CoreWebView2.NavigationCompleted -= OnCompleted;
                    navTcs.TrySetResult(true);
                }
                _web.CoreWebView2.NavigationCompleted += OnCompleted;
                try { _web.CoreWebView2.Navigate(url); }
                catch (Exception ex) { outerTcs.TrySetException(ex); return; }

                var navDone = await Task.WhenAny(navTcs.Task, Task.Delay(timeoutMs - 2000));
                if (navDone != navTcs.Task)
                {
                    try { _web.CoreWebView2.Stop(); } catch { }
                }

                // Poll до появления маркеров результатов, максимум 8 секунд.
                // Это ловит SPA-подгрузку (Yandex, Google) и даёт шанс, что
                // результаты реально появятся, а не заглушка "Включите JS".
                string[] markers =
                {
                    "#search-result", ".serp-item", "li.serp-item",  // yandex
                    "#b_results", "li.b_algo",                       // bing
                    "#search", "#rso", "div.g",                      // google
                    ".result__body", "a.result__a"                   // ddg
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 8000)
                {
                    bool found = false;
                    try
                    {
                        string js =
                            "(()=>{const sels=" + System.Text.Json.JsonSerializer.Serialize(markers) + ";"
                          + "for(const s of sels){if(document.querySelector(s))return '1';}"
                          + "const t=(document.title||'').toLowerCase();"
                          + "if(t.includes('капч')||t.includes('captcha')||t.includes('enable javascript')"
                          + "||t.includes('включите javascript')||t.includes('проверка браузера'))return 'x';"
                          + "return '';})()";
                        string r = await _web.CoreWebView2.ExecuteScriptAsync(js);
                        // r — json-строка: "\"1\"" / "\"x\"" / "\"\""
                        if (r != null && r.Contains("\"1\"")) { found = true; }
                        else if (r != null && r.Contains("\"x\""))
                        {
                            // капча/заглушка — не ждём дальше, отдаём как есть
                            break;
                        }
                    }
                    catch { }
                    if (found) break;
                    await Task.Delay(250);
                }

                // Дополнительная пауза для догрузки
                await Task.Delay(400);

                string raw = await _web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                string html = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                outerTcs.TrySetResult(html);
            }
            catch (Exception ex) { outerTcs.TrySetException(ex); }
        }));

        return await outerTcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _form.Close(); _form.Dispose(); } catch { }
    }
}
