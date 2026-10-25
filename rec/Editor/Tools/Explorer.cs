namespace AgentApp.Editor.Tools;

public sealed class ExplorerTool : EditorTool
{
    public string RootFolder { get; }
    public ExplorerTool(string rootFolder) => RootFolder = rootFolder;
    public override string Id => "explorer";
    public override string Title => "Explorer";
    public override string PagePath => "Editor/Tools/explorer.html";
    public override object? GetInitData() => new { root = RootFolder };
}
