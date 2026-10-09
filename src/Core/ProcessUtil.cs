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
        if (!OperatingSystem.IsWindows()) return false;
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

    /// <summary>Starts an elevated copy of this executable and returns immediately. False when the UAC prompt was cancelled.</summary>
    public static bool StartSelfElevatedDetached(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.ExePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Paths.ExeDir,
            };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application, System.Text.StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    /// <summary>
    /// Starts a program with the desktop shell's (unelevated) token, so helpers launched by the elevated hub run with the
    /// same rights as when the user starts them. Falls back to a plain start when the hub itself is not elevated.
    /// </summary>
    public static bool StartAsShellUser(string exe, string arguments, bool hidden) => StartAsShellUserPid(exe, arguments, hidden) > 0;

    /// <summary>Same as <see cref="StartAsShellUser"/> but returns the process id (0 on failure).</summary>
    public static int StartAsShellUserPid(string exe, string arguments, bool hidden)
    {
        if (!OperatingSystem.IsWindows() || !IsElevated())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = hidden, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty });
                return p?.Id ?? 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"启动 {exe} 失败: {ex.Message}");
                return 0;
            }
        }

        IntPtr process = IntPtr.Zero, token = IntPtr.Zero, primary = IntPtr.Zero;
        try
        {
            var shell = GetShellWindow();
            if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out var shellPid) == 0) throw new InvalidOperationException("找不到桌面外壳进程");
            process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, shellPid);
            if (process == IntPtr.Zero || !OpenProcessToken(process, 0x0002 /* TOKEN_DUPLICATE */, out token)) throw new System.ComponentModel.Win32Exception();
            // TOKEN_QUERY | ASSIGN_PRIMARY | DUPLICATE | ADJUST_DEFAULT | ADJUST_SESSIONID, SecurityImpersonation, TokenPrimary
            if (!DuplicateTokenEx(token, 0x018B, IntPtr.Zero, 2, 1, out primary)) throw new System.ComponentModel.Win32Exception();
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (hidden)
            {
                si.dwFlags = 0x1; // STARTF_USESHOWWINDOW
                si.wShowWindow = 0; // SW_HIDE
            }
            var cmd = new System.Text.StringBuilder($"\"{exe}\" {arguments}".TrimEnd());
            const uint CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            if (!CreateProcessWithTokenW(primary, 0, exe, cmd, (hidden ? CREATE_NO_WINDOW : 0) | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
            {
                throw new System.ComponentModel.Win32Exception();
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        catch (Exception ex)
        {
            Log.Warn($"以普通用户身份启动 {Path.GetFileName(exe)} 失败: {ex.Message}");
            return 0;
        }
        finally
        {
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }
}
