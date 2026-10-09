using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>
/// Health of the clipboard / file-transfer agents (deskflow_clipboard_sync.py) through their local control port — the
/// check the old Linux connection watchdog and the Windows keepalive task used to run: an agent is healthy when it is
/// connected to the clipboard hub and its loop ran recently (or a file transfer is still making progress).
/// </summary>
public static class SideChannelHealth
{
    public const int ClipboardControlPort = 24889;
    public const int FileControlPort = 24891;

    /// <summary>Consecutive failed checks per agent (restart after three).</summary>
    private static readonly Dictionary<string, int> Failures = new();

    public static (bool Healthy, string Detail) Check(int port, bool fileAgent)
    {
        string token;
        try { token = File.ReadAllText(DeskflowPeer.TokenFile).Trim(); }
        catch (Exception ex) { return (false, "token: " + ex.Message); }
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", port).Wait(1500) || !client.Connected) return (false, "control port closed");
            using var stream = client.GetStream();
            stream.ReadTimeout = 1500;
            var request = Encoding.UTF8.GetBytes($"{{\"token\":\"{token}\",\"action\":\"health\"}}\n");
            stream.Write(request, 0, request.Length);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            if (!response.StartsWith("OK ", StringComparison.Ordinal) || !response.Contains("\"connected\":true")) return (false, response.Length > 120 ? response[..120] : response);
            var loop = Regex.Match(response, "\"loop_age_ms\":(\\d+)");
            if (loop.Success && long.Parse(loop.Groups[1].Value) <= 5000) return (true, "ok");
            if (fileAgent && response.Contains("\"transfer_active\":true"))
            {
                var progress = Regex.Match(response, "\"transfer_progress_age_ms\":(\\d+)");
                if (progress.Success && long.Parse(progress.Groups[1].Value) <= 15000) return (true, "transferring");
            }
            return (false, "loop stalled: " + (response.Length > 120 ? response[..120] : response));
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>Records a check result; true when the agent failed three times in a row and should be restarted.</summary>
    public static bool NeedsRestart(string name, bool healthy)
    {
        lock (Failures)
        {
            var count = healthy ? 0 : Failures.GetValueOrDefault(name) + 1;
            Failures[name] = count >= 3 ? 0 : count;
            return count >= 3;
        }
    }
}
