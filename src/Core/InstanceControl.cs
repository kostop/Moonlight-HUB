using System.IO;
using System.Net.Sockets;
using System.Text;

namespace MoonlightHub.Core;

/// <summary>
/// Talks to the running UI instance: "show the window" (second launch) and "exit for replace" (hot update).
/// Windows uses the named events of the original hub (so an update can still replace an older version); Linux has no
/// named events in .NET, so a Unix socket in $XDG_RUNTIME_DIR carries the same two commands.
/// </summary>
public static class InstanceControl
{
    public const string MutexName = @"Local\MoonlightHub.SingleInstance";
    private const string ShowEventName = @"Local\MoonlightHub.ShowWindow";
    public const string ExitEventName = @"Local\MoonlightHub.ExitForReplace";

    /// <summary>
    /// Local\ objects only exist in their own session, so a CLI run over SSH (session 0, whose Local\ is the global
    /// namespace) could not reach the hub on the desktop (session 1). The elevated hub therefore also listens on Global\
    /// copies of its events (creating Global\ objects needs administrator rights; without them only Local\ is used).
    /// </summary>
    private static string GlobalName(string localName) => @"Global\" + localName[(localName.IndexOf('\\') + 1)..];

    private static string SocketPath => Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath(), "moonlight-hub.sock");

    /// <summary>True when no other hub instance holds the single-instance mutex (the probe releases it again immediately).</summary>
    public static bool IsFirstInstance()
    {
        try
        {
            using var probe = new Mutex(false, MutexName);
            bool got;
            try { got = probe.WaitOne(0); }
            catch (AbandonedMutexException) { got = true; }
            if (got) probe.ReleaseMutex();
            return got;
        }
        catch
        {
            return true;
        }
    }

    public static bool SignalShow() => Signal(ShowEventName, "show");

    /// <summary>Asks the running instance to leave without touching displays or instances. False when none is running.</summary>
    public static bool SignalExitForReplace() => Signal(ExitEventName, "exit-replace");

    private static bool Signal(string eventName, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { eventName, GlobalName(eventName) })
            {
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(name);
                    evt.Set();
                    return true;
                }
                catch
                {
                    // not running in this session (or no access to the global copy): try the next name
                }
            }
            return false;
        }
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(command + "\n"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts the background listeners of the UI instance.</summary>
    public static void Listen(Action onShow, Action onExitForReplace)
    {
        if (OperatingSystem.IsWindows())
        {
            var exited = 0;
            void ExitOnce() { if (Interlocked.Exchange(ref exited, 1) == 0) onExitForReplace(); }
            StartEventListener(ShowEventName, onShow, repeat: true);
            StartEventListener(ExitEventName, ExitOnce, repeat: false);
            if (ProcessUtil.IsElevated())
            {
                try
                {
                    StartEventListener(GlobalName(ShowEventName), onShow, repeat: true);
                    StartEventListener(GlobalName(ExitEventName), ExitOnce, repeat: false);
                }
                catch (Exception ex)
                {
                    Log.Warn("无法创建跨会话的实例控制事件（SSH 里的 --cli shutdown 将找不到本实例）: " + ex.Message);
                }
            }
            return;
        }
        var thread = new Thread(() =>
        {
            try
            {
                if (File.Exists(SocketPath)) File.Delete(SocketPath);
                using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                server.Bind(new UnixDomainSocketEndPoint(SocketPath));
                server.Listen(4);
                while (true)
                {
                    using var client = server.Accept();
                    var buffer = new byte[64];
                    var n = client.Receive(buffer);
                    var command = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                    if (command == "show") onShow();
                    else if (command == "exit-replace") { onExitForReplace(); return; }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("实例控制套接字停止: " + ex.Message);
            }
        }) { IsBackground = true, Name = "InstanceControl" };
        thread.Start();
    }

    private static void StartEventListener(string name, Action action, bool repeat)
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var thread = new Thread(() =>
        {
            do
            {
                evt.WaitOne();
                try { action(); } catch (Exception ex) { Log.Warn("实例控制回调失败: " + ex.Message); }
            } while (repeat);
        }) { IsBackground = true, Name = "InstanceControl:" + name };
        thread.Start();
    }
}
