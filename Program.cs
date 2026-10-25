using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentApp.Main;

namespace AgentApp;

internal static class Program
{
    private const string MutexName = "AgentApp_SingleInstance_v1";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using var mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew) { BringExistingToFront(); return; }

        // Прицепляемся к родительской консоли (dotnet run), чтобы
        // Console.WriteLine был виден в терминале. WinExe не открывает
        // своё окно консоли — только логирование в терминал запуска.
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            try
            {
                var stdout = Console.OpenStandardOutput();
                var w = new StreamWriter(stdout) { AutoFlush = true };
                Console.SetOut(w);
                var stderr = Console.OpenStandardError();
                var ew = new StreamWriter(stderr) { AutoFlush = true };
                Console.SetError(ew);
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            }
            catch { }
        }

        ApplicationConfiguration.Initialize();
        var ctx = new AppContext();
        App.Context = ctx;
        ctx.Start(new MainForm());
        Application.Run(ctx);
    }

    private static void BringExistingToFront()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); return; }
            }
        }
        catch { }
    }
}

internal static class App
{
    public static AppContext? Context { get; set; }
}

internal sealed class AppContext : ApplicationContext
{
    private readonly HashSet<Form> _open = new();
    public void Start(Form form) => Track(form);
    public void Show(Form form) => Track(form);

    private void Track(Form form)
    {
        _open.Add(form);
        form.FormClosed += OnFormClosed;
        form.Show();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not Form f) return;
        f.FormClosed -= OnFormClosed;
        _open.Remove(f);
        if (_open.Count == 0) ExitThread();
    }
}
