using System.Diagnostics;
using System.Text;

namespace AgentApp.Editor;

/// <summary>Управляет одним фоновым процессом PowerShell внутри проекта.</summary>
public sealed class TerminalService : IDisposable
{
    private Process? _proc;
    public event Action<string>? OnOutput;
    private readonly string _workingDir;

    public bool IsRunning => _proc is { HasExited: false };

    public TerminalService(string workingDir) { _workingDir = workingDir; }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -",
                WorkingDirectory = _workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = new Process { StartInfo = psi };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(e.Data); };
            _proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) OnOutput?.Invoke(e.Data); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // Стартовая инициализация: UTF-8 + marker баннера + текущий путь
            WriteRaw("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            WriteRaw("Write-Host '__MYTHIC_BANNER__'");
            EmitCwdMarker();
        }
        catch (Exception ex) { OnOutput?.Invoke("[terminal error] " + ex.Message); }
    }

    private void WriteRaw(string line)
    {
        try
        {
            _proc!.StandardInput.WriteLine(line);
            _proc.StandardInput.Flush();
        }
        catch (Exception ex) { OnOutput?.Invoke("[terminal write error] " + ex.Message); }
    }

    /// <summary>Отправляет команду и сразу вслед за ней — маркер с текущей рабочей директорией.</summary>
    public void Write(string line)
    {
        if (!IsRunning) { Start(); Thread.Sleep(150); }
        try
        {
            _proc!.StandardInput.WriteLine(line);
            _proc.StandardInput.Flush();
            EmitCwdMarker();
        }
        catch (Exception ex) { OnOutput?.Invoke("[terminal write error] " + ex.Message); }
    }

    private void EmitCwdMarker()
    {
        try
        {
            _proc!.StandardInput.WriteLine("Write-Host \"__MYTHIC_CWD__:$($PWD.Path)\"");
            _proc.StandardInput.Flush();
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.StandardInput.WriteLine("exit"); _proc.StandardInput.Flush(); } catch { }
                if (!_proc.WaitForExit(800)) { try { _proc.Kill(true); } catch { } }
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }
}
