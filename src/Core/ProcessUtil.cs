using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MoonlightHub.Core;

/// <summary>Process helpers: graceful console Ctrl+C, process-by-path lookup, elevation.</summary>
public static class ProcessUtil
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    private const uint CTRL_C_EVENT = 0;

    private static readonly object ConsoleGate = new();

    /// <summary>Sends Ctrl+C to a console process (Sunshine handles SIGINT and shuts down cleanly).</summary>
    public static bool SendCtrlC(int pid)
    {
        lock (ConsoleGate)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid))
            {
                return false;
            }
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                return GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
            }
            finally
            {
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    /// <summary>
    /// Sends Ctrl+C through a short-lived helper process ("MoonlightHub.exe --send-ctrlc pid"). Attaching the GUI process
    /// itself to the target's console would deliver the Ctrl+C to the GUI as well and terminate it.
    /// </summary>
    public static bool SendCtrlCViaHelper(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.ExePath, $"--send-ctrlc {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var helper = Process.Start(psi);
            if (helper == null) return false;
            helper.WaitForExit(10000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("启动 Ctrl+C 辅助进程失败: " + ex.Message);
            return false;
        }
    }

    public static bool StopGracefully(int pid, TimeSpan wait)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            if (SendCtrlCViaHelper(pid) && p.WaitForExit((int)wait.TotalMilliseconds))
            {
                return true;
            }
            if (p.HasExited) return true;
            p.Kill(true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex)
        {
            Log.Warn($"结束进程 {pid} 失败: {ex.Message}");
            return false;
        }
    }

    public static List<Process> FindByPath(string exePath)
    {
        var list = new List<Process>();
        var name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(p);
                    continue;
                }
            }
            catch
            {
                // access denied (elevated process) → still return by name as a weaker match
                list.Add(p);
                continue;
            }
            p.Dispose();
        }
        return list;
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Re-launches this executable elevated with the given arguments and waits for it. Returns the exit code, or -1 if the UAC prompt was cancelled.</summary>
    public static int RunSelfElevated(string arguments, int timeoutMs = 120000)
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.ExePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (!p.WaitForExit(timeoutMs)) return -2;
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1; // cancelled
        }
    }

    public static (int ExitCode, string Output) Run(string file, string arguments, int timeoutMs = 60000, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "timeout");
        }
        return (p.ExitCode, (stdout.Result + Environment.NewLine + stderr.Result).Trim());
    }

    public static (int ExitCode, string Output) RunPowerShell(string scriptPath, string arguments = "", int timeoutMs = 120000)
    {
        return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}", timeoutMs, Path.GetDirectoryName(scriptPath));
    }

    public static void OpenInShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开 {target} 失败: {ex.Message}");
        }
    }
}
