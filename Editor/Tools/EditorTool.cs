namespace AgentApp.Editor.Tools;

public abstract class EditorTool
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string PagePath { get; }
    public virtual bool DefaultOpen => true;
    public virtual object? GetInitData() => null;
}
