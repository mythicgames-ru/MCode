using System.Text.Json;
using AgentApp.Projects;

namespace AgentApp.Editor;

public sealed class Prepare : Form
{
    private static readonly Color Bg = Color.FromArgb(0x0E, 0x0E, 0x0E);
    private static readonly Color Fg = Color.FromArgb(0xE8, 0xEA, 0xED);

    private readonly string _name, _folder;

    public Prepare(string name, string folder)
    {
        _name = name; _folder = folder;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 140);
        BackColor = Bg; ShowInTaskbar = false; TopMost = true;
        DoubleBuffered = true; Opacity = 0;
        Controls.Add(new Label
        {
            Text = "Открывается проект", ForeColor = Fg, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 13F), TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        });
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await FadeInAsync();
            await Task.Run(PrepareProject);
            await OpenEditorAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка подготовки проекта",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void PrepareProject()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "project.json"),
            JsonSerializer.Serialize(new { name = _name, created = DateTime.Now },
                new JsonSerializerOptions { WriteIndented = true }));
        ProjectsStore.Register(_name, _folder);
    }

    private async Task OpenEditorAsync()
    {
        var editor = new EditorForm(_name, _folder);
        App.Context!.Show(editor);
        await editor.Ready;
        await FadeOutAsync();
        Close();
        editor.Activate();
    }

    private async Task FadeInAsync()
    {
        for (int i = 1; i <= 14; i++) { Opacity = i / 14.0; await Task.Delay(12); }
        Opacity = 1;
    }
    private async Task FadeOutAsync()
    {
        for (int i = 13; i >= 0; i--) { Opacity = i / 14.0; await Task.Delay(12); }
        Opacity = 0;
    }
}
