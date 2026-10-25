namespace AgentApp.Editor.Tools;

public sealed class CodePreview : EditorTool
{
    public override string Id => "codepreview";
    public override string Title => "Code Preview";
    public override string PagePath => "Editor/Tools/CodePreview.html";
    public override bool DefaultOpen => true;
}
