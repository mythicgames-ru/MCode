using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentApp.Editor;
using AgentApp.Profiles;
using AgentApp.Projects;
using AgentApp.Window;

namespace AgentApp.Main;

public sealed class MainForm : AppWindow
{
    private const string AuthUrl = "https://mythicgames.ru/auth/mcode.php";

    private readonly ProfileStore _store = ProfileStore.Load();
    private Profile? _pendingProfile;

    public MainForm() : base(startPage: "Main/index.html",
                             title: "MCode Menu", width: 1280, height: 720)
    {
        Icon = LoadIconFromResources("app_icon.ico") ?? Icon;
    }

    protected override void OnWebMessage(string message)
    {
        if (message.Length > 0 && message[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("action", out var act))
                { HandleJsonAction(act.GetString(), doc.RootElement); return; }
            }
            catch { }
        }

        switch (message)
        {
            case "main-ready":     SendState();    break;
            case "open-last":      OpenLastProject(); break;
            case "open":           SendProjectsList(); break;
            case "get-defaults":   SendDefaults();    break;
            case "browse-folder":  BrowseFolder();    break;
            case "exit":           Close();           break;
            case "login-google":   _ = StartGoogleAuthAsync(); break;
            case "login-telegram": SendToPage("Заглушка: вход через Telegram"); break;
            case "add-autonomous": AddAutonomous();   break;
            case "logout":         Logout();          break;
        }
    }

    private void HandleJsonAction(string? action, JsonElement root)
    {
        switch (action)
        {
            case "create-project":
                StartProjectCreation(
                    root.GetProperty("name").GetString() ?? "",
                    root.GetProperty("folder").GetString() ?? "");
                break;
            case "select-profile":
                SelectProfile(root.GetProperty("id").GetString() ?? "");
                break;
            case "save-profile":
                SavePendingProfile(
                    root.GetProperty("nickname").GetString() ?? "",
                    root.TryGetProperty("avatarData", out var av) ? (av.GetString() ?? "") : "");
                break;
            case "open-project":
                OpenProjectByFolder(root.GetProperty("folder").GetString() ?? "");
                break;
        }
    }

    private void OpenLastProject()
    {
        var st = ProjectsStore.Load();
        if (string.IsNullOrEmpty(st.LastProject))
        { SendToPage("Пока нет последних проектов"); return; }
        if (!ProjectsStore.IsValidFolder(st.LastProject))
        { SendToPage("Последний проект был удалён"); return; }
        OpenProjectByFolder(st.LastProject);
    }

    private void SendProjectsList()
    {
        var st = ProjectsStore.Load();
        var list = st.Projects.Where(p => ProjectsStore.IsValid(p))
            .OrderByDescending(p => p.LastOpened)
            .Select(p => new { name = p.Name, folder = p.Folder })
            .ToArray();
        SendToPage(JsonSerializer.Serialize(new { type = "projects", projects = list }));
    }

    private void OpenProjectByFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!ProjectsStore.IsValidFolder(folder))
        { SendToPage("Проект не найден"); return; }
        string name = ProjectsStore.ReadProjectName(folder) ?? Path.GetFileName(folder);
        ProjectsStore.Register(name, folder);
        BeginInvoke(() =>
        {
            App.Context!.Show(new Prepare(name, folder));
            Close();
        });
    }

    private object SerializeProfile(Profile p)
    {
        string avatar = _store.GetAvatarBase64(p) ?? p.GoogleAvatar ?? "";
        return new
        {
            id = p.Id, type = p.Type, email = p.Email, nickname = p.Nickname,
            googleName = p.GoogleName, avatar,
            hasCustomAvatar = !string.IsNullOrEmpty(p.AvatarFile)
        };
    }

    private void SendState()
    {
        if (_store.State.ActiveId is null && _store.State.Profiles.Count > 0)
            _store.State.ActiveId = _store.State.Profiles[0].Id;
        object? active = null;
        if (_store.Active is { } a) active = SerializeProfile(a);
        SendToPage(JsonSerializer.Serialize(new
        {
            type = "state",
            activeId = _store.State.ActiveId,
            active,
            profiles = _store.State.Profiles.Select(SerializeProfile).ToArray()
        }));
    }

    private void SendSetup()
    {
        if (_pendingProfile is null) return;
        SendToPage(JsonSerializer.Serialize(new
        { type = "setup", profile = SerializeProfile(_pendingProfile) }));
    }

    private void SelectProfile(string id)
    {
        if (!_store.State.Profiles.Any(p => p.Id == id)) return;
        _store.SetActive(id); SendState();
    }

    private void Logout()
    {
        if (_store.State.ActiveId is not { } id) return;
        _store.Remove(id); SendState();
    }

    private void AddAutonomous()
    {
        _pendingProfile = new Profile
        { Id = Guid.NewGuid().ToString("N"), Type = "autonomous", CreatedAt = DateTime.UtcNow };
        SendSetup();
    }

    private void SavePendingProfile(string nickname, string avatarData)
    {
        if (_pendingProfile is null) return;
        var p = _pendingProfile;
        p.Nickname = ProfileStore.SanitizeNickname(nickname);
        if (string.IsNullOrEmpty(p.Nickname)) p.Nickname = "user";
        if (!string.IsNullOrEmpty(avatarData))
        {
            byte[]? bytes = DecodeDataUrl(avatarData);
            if (bytes is not null) _store.SaveAvatar(p, bytes);
        }
        if (!_store.State.Profiles.Any(x => x.Id == p.Id)) _store.State.Profiles.Add(p);
        _store.SetActive(p.Id);
        _pendingProfile = null;
        SendState();
    }

    private static byte[]? DecodeDataUrl(string dataUrl)
    {
        try
        {
            int comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            return Convert.FromBase64String(dataUrl.Substring(comma + 1));
        }
        catch { return null; }
    }

    private async Task StartGoogleAuthAsync()
    {
        HttpListener? listener = null;
        int port = 0;
        for (int p = 49152; p < 49200; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{p}/");
            try { l.Start(); listener = l; port = p; break; }
            catch { try { l.Close(); } catch { } }
        }
        if (listener is null) { SendToPage("Не удалось занять порт"); return; }

        try
        {
            string url = $"{AuthUrl}?desktop=1&port={port}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            var ctxTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5)));
            if (completed != ctxTask) { SendToPage("Таймаут авторизации"); return; }
            var ctx = await ctxTask;
            string? data = ctx.Request.QueryString["d"];

            string body =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Готово</title></head>"
              + "<body style=\"margin:0;height:100vh;background:#0e0e0e;color:#f0f0f0;"
              + "font-family:system-ui,'Segoe UI',sans-serif;display:flex;align-items:center;"
              + "justify-content:center\"><div style=\"text-align:center\">"
              + "<div style=\"font-size:18px;font-weight:500\">Готово</div>"
              + "<div style=\"color:#8a8a8a;margin-top:8px;font-size:13px\">"
              + "Можно закрыть эту вкладку</div></div>"
              + "<script>setTimeout(function(){window.close()},500)</script></body></html>";
            byte[] buf = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();

            if (string.IsNullOrEmpty(data)) { SendToPage("Сервер не вернул данные"); return; }

            string padded = data.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";

            if (type == "auth-success" && root.TryGetProperty("user", out var u))
            {
                string email = u.TryGetProperty("email", out var em) ? (em.GetString() ?? "") : "";
                string name  = u.TryGetProperty("username", out var un) ? (un.GetString() ?? "") : "";
                string pic   = u.TryGetProperty("avatar", out var av) ? (av.GetString() ?? "") : "";

                var existing = _store.State.Profiles.FirstOrDefault(p =>
                    p.Type == "authorized" &&
                    string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));

                if (existing is not null) { _store.SetActive(existing.Id); SendState(); }
                else
                {
                    _pendingProfile = new Profile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Type = "authorized",
                        Email = email,
                        GoogleName = name,
                        Nickname = ProfileStore.SanitizeNickname(name),
                        GoogleAvatar = pic,
                        CreatedAt = DateTime.UtcNow
                    };
                    SendSetup();
                }
            }
            else
            {
                string err = root.TryGetProperty("error", out var ee)
                    ? (ee.GetString() ?? "ошибка авторизации") : "ошибка авторизации";
                SendToPage(err);
            }
        }
        catch (Exception ex) { SendToPage("Ошибка авторизации: " + ex.Message); }
        finally { try { listener.Stop(); listener.Close(); } catch { } }
    }

    private void StartProjectCreation(string name, string folder)
    {
        if (string.IsNullOrWhiteSpace(name)) { SendToPage("Введи название проекта"); return; }
        if (string.IsNullOrWhiteSpace(folder)) { SendToPage("Выбери папку проекта"); return; }
        BeginInvoke(() =>
        {
            App.Context!.Show(new Prepare(name, folder));
            Close();
        });
    }

    private void SendDefaults()
    {
        string baseDir = GetDefaultProjectsDir();
        SendToPage(JsonSerializer.Serialize(new { type = "defaults", baseDir }));
    }

    private static string GetDefaultProjectsDir()
    {
        string? secondary = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => d.RootDirectory.FullName)
            .Where(p => p.Length >= 1 && p[0] > 'C' && p[0] <= 'Z')
            .OrderBy(p => p).FirstOrDefault();
        if (secondary is not null) return Path.Combine(secondary, "Projects");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Выбери папку для проекта" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SendToPage(JsonSerializer.Serialize(new { type = "folder-picked", path = dlg.SelectedPath }));
    }
}
