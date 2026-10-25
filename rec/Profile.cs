using System.Text;
using System.Text.Json;

namespace AgentApp.Profiles;

public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "autonomous";
    public string Email { get; set; } = "";
    public string GoogleName { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string GoogleAvatar { get; set; } = "";
    public string AvatarFile { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ProfilesState
{
    public string? ActiveId { get; set; }
    public List<Profile> Profiles { get; set; } = new();
}

public sealed class ProfileStore
{
    public static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentApp");
    public static readonly string AvatarsDir = Path.Combine(RootDir, "avatars");
    public static readonly string ProfilesFile = Path.Combine(RootDir, "profiles.json");

    public ProfilesState State { get; private set; } = new();
    public Profile? Active => State.Profiles.FirstOrDefault(p => p.Id == State.ActiveId);

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        try
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(AvatarsDir);
            if (File.Exists(ProfilesFile))
            {
                var st = JsonSerializer.Deserialize<ProfilesState>(File.ReadAllText(ProfilesFile));
                if (st is not null) store.State = st;
            }
            if (store.State.ActiveId is null && store.State.Profiles.Count > 0)
                store.State.ActiveId = store.State.Profiles[0].Id;
        }
        catch { }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(ProfilesFile,
                JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void SetActive(string id) { State.ActiveId = id; Save(); }

    public void Remove(string id)
    {
        var p = State.Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        State.Profiles.Remove(p);
        try { File.Delete(Path.Combine(AvatarsDir, p.Id + ".png")); } catch { }
        if (State.ActiveId == id) State.ActiveId = State.Profiles.FirstOrDefault()?.Id;
        Save();
    }

    public string? SaveAvatar(Profile p, byte[] pngBytes)
    {
        try
        {
            Directory.CreateDirectory(AvatarsDir);
            string fn = p.Id + ".png";
            File.WriteAllBytes(Path.Combine(AvatarsDir, fn), pngBytes);
            p.AvatarFile = fn; Save(); return fn;
        }
        catch { return null; }
    }

    public string? GetAvatarBase64(Profile p)
    {
        try
        {
            if (string.IsNullOrEmpty(p.AvatarFile)) return null;
            string path = Path.Combine(AvatarsDir, p.AvatarFile);
            if (!File.Exists(path)) return null;
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    public static string SanitizeNickname(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder();
        foreach (char c in raw)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.') sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        return sb.ToString();
    }
}
