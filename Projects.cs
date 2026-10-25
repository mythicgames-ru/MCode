using System.Text.Json;

namespace AgentApp.Projects;

public sealed class ProjectEntry
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public DateTime LastOpened { get; set; } = DateTime.UtcNow;
}

public sealed class ProjectsState
{
    public string? LastProject { get; set; }
    public List<ProjectEntry> Projects { get; set; } = new();
}

public static class ProjectsStore
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentApp", "settings.json");

    public static ProjectsState Load()
    {
        var st = new ProjectsState();
        try
        {
            if (!File.Exists(FilePath)) return st;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("lastProject", out var lp) && lp.ValueKind == JsonValueKind.String)
                st.LastProject = lp.GetString();
            if (root.TryGetProperty("projects", out var pr) && pr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pr.EnumerateArray())
                {
                    var e = new ProjectEntry();
                    if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        e.Name = n.GetString() ?? "";
                    if (item.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String)
                        e.Folder = f.GetString() ?? "";
                    if (item.TryGetProperty("lastOpened", out var lo) && lo.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(lo.GetString(), out var d)) e.LastOpened = d;
                    if (!string.IsNullOrEmpty(e.Folder)) st.Projects.Add(e);
                }
            }
        }
        catch { }
        return st;
    }

    public static void Save(ProjectsState st)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                lastProject = st.LastProject,
                projects = st.Projects.Select(p => new
                { name = p.Name, folder = p.Folder, lastOpened = p.LastOpened.ToString("o") }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool IsValid(ProjectEntry p) => IsValidFolder(p.Folder);

    public static bool IsValidFolder(string folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                && File.Exists(Path.Combine(folder, "project.json"));
        }
        catch { return false; }
    }

    public static void Register(string name, string folder)
    {
        var st = Load();
        var ex = st.Projects.FirstOrDefault(x => string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (ex is not null) { ex.Name = name; ex.LastOpened = DateTime.UtcNow; }
        else st.Projects.Add(new ProjectEntry { Name = name, Folder = folder, LastOpened = DateTime.UtcNow });
        st.LastProject = folder;
        Save(st);
    }

    public static string? ReadProjectName(string folder)
    {
        try
        {
            string pf = Path.Combine(folder, "project.json");
            if (!File.Exists(pf)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(pf));
            if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                return n.GetString();
        }
        catch { }
        return null;
    }
}
