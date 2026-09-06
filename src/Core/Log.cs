using System.IO;

namespace MoonlightHub.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => Level.ToString()
    };

    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Well-known paths for the hub's own data.</summary>
public static class Paths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoonlightHub");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MoonlightHub.exe");
    public static string ExeDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>Repository root (…\moonlight) when running from the source/publish tree, otherwise the exe dir.</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(ExeDir);
            for (var i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "README.md")) && Directory.Exists(Path.Combine(dir.FullName, "scripts")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return ExeDir;
        }
    }

    public static void EnsureDataDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}

/// <summary>Rolling file + in-memory log. Thread-safe.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const int MaxRecent = 3000;

    public static List<LogEntry> Recent { get; } = new();
    public static event Action<LogEntry>? EntryAdded;
    public static string CurrentFile => Path.Combine(Paths.LogDir, $"hub-{DateTime.Now:yyyyMMdd}.log");

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            try
            {
                // open-append-close per line: several hub processes (hot replace, CLI) share this file
                Paths.EnsureDataDirs();
                var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.ProcessId}] {message}{Environment.NewLine}";
                using var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // logging must never take the hub down
            }

            Recent.Add(entry);
            if (Recent.Count > MaxRecent)
            {
                Recent.RemoveRange(0, Recent.Count - MaxRecent);
            }
        }

        try { EntryAdded?.Invoke(entry); } catch { }
    }

    public static string TailFile(int maxLines = 400)
    {
        try
        {
            if (!File.Exists(CurrentFile)) return string.Empty;
            var lines = File.ReadAllLines(CurrentFile);
            var start = Math.Max(0, lines.Length - maxLines);
            return string.Join(Environment.NewLine, lines.Skip(start));
        }
        catch (Exception ex)
        {
            return "无法读取日志: " + ex.Message;
        }
    }
}
