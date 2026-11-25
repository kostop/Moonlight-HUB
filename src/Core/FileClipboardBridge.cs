using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>A share of the sending computer that contains a copied item, and the item's path inside it ("" = share root).</summary>
public sealed class ShareLocation
{
    public string Share { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

/// <summary>One copied file or folder: every share that contains it, most specific first (the receiver uses the first it can open).</summary>
public sealed class SharedFileRef
{
    public string Name { get; set; } = string.Empty;
    public List<ShareLocation> Locations { get; set; } = new();
}

public sealed class FileClipboardPayload
{
    public string Screen { get; set; } = string.Empty;
    public List<SharedFileRef> Items { get; set; } = new();
}

/// <summary>
/// 文件快传（Windows ↔ Windows）：本机复制文件时，把文件在本机 SMB 共享里的位置发给对端 Hub；对端把 \\本机地址\共享\路径
/// 放到剪贴板，粘贴时资源管理器直接从本机拷贝。取代旁路 file-agent 先把整份文件推到对端下载文件夹、再放剪贴板的做法
/// （2026-11-23：225 MB 的压缩包按 12 Mbps 限速要传 3 分钟才能粘贴，同一个文件还重复排队了 4 次）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileClipboardBridge : IDisposable
{
    private const string ClassName = "MoonlightHubFileClipboard";
    private const uint WM_CLOSE_BRIDGE = 0x8001; // WM_APP + 1
    private const uint WM_RUN = 0x8002;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_HDROP = 15;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex ShareName = new(@"^[^\\/:*?""<>|\x00-\x1f]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex DownloadDirArg = new(@"--download-dir\s+(""[^""]+""|\S+)", RegexOptions.Compiled);

    private readonly DeskflowManager _deskflow;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _wndProc;
    private uint _preferredDropEffect;
    private uint _ownSequence;
    private string _lastSignature = string.Empty;
    private DateTime _lastSignatureUtc;
    private List<(string Name, string Path)>? _shares;
    private DateTime _sharesUtc;

    /// <summary>Last thing the bridge did (shown on the Deskflow page).</summary>
    public string LastStatus { get; private set; } = "还没有复制过文件";
    /// <summary>Problems the user should see right away (message, warning).</summary>
    public event Action<string, bool>? Notice;

    /// <param name="deskflow">Deskflow 管理器：提供对端地址、对端 Hub 状态与本机屏幕名</param>
    public FileClipboardBridge(DeskflowManager deskflow) => _deskflow = deskflow;

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "file-clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE_BRIDGE, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ clipboard window

    /// <param name="ready">窗口与剪贴板监听建立（或失败）后置位，Start 才返回</param>
    private void Run(ManualResetEventSlim ready)
    {
        var listening = false;
        try
        {
            _wndProc = WindowProc;
            var module = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = module,
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _hwnd = CreateWindowEx(0, ClassName, "Moonlight Hub file clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero);
            _preferredDropEffect = RegisterClipboardFormat("Preferred DropEffect");
            // the hub runs elevated; the notification comes from the system, but allow it explicitly anyway
            if (_hwnd != IntPtr.Zero) ChangeWindowMessageFilterEx(_hwnd, WM_CLIPBOARDUPDATE, 1, IntPtr.Zero);
            listening = _hwnd != IntPtr.Zero && AddClipboardFormatListener(_hwnd);
            if (!listening) Log.Warn($"文件快传: 无法监听剪贴板（错误 {Marshal.GetLastWin32Error()}）");
        }
        catch (Exception ex)
        {
            Log.Warn("文件快传: 创建剪贴板窗口失败: " + ex.Message);
        }
        finally
        {
            ready.Set();
        }
        if (!listening) return;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                try { OnClipboardUpdate(); }
                catch (Exception ex) { Log.Warn("文件快传: 读取剪贴板失败: " + ex.Message); }
                return IntPtr.Zero;
            case WM_RUN:
                while (_work.TryDequeue(out var action)) action();
                return IntPtr.Zero;
            case WM_CLOSE_BRIDGE:
                RemoveClipboardFormatListener(hwnd);
                DestroyWindow(hwnd);
                _hwnd = IntPtr.Zero;
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Runs <paramref name="action"/> on the clipboard window's thread (the clipboard must be opened by its owner window).</summary>
    /// <param name="action">在剪贴板窗口线程上执行的操作</param>
    private Task<T> OnWindowThread<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { done.TrySetResult(action()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        if (_hwnd == IntPtr.Zero || !PostMessage(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero))
            done.TrySetException(new InvalidOperationException("剪贴板窗口没有运行"));
        return done.Task;
    }

    // ------------------------------------------------------------------ sending side

    private void OnClipboardUpdate()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _ownSequence || !_deskflow.FileClipboardViaSmb || !IsClipboardFormatAvailable(CF_HDROP)) return;
        var paths = ReadFileDrop();
        if (paths.Count > 0) _ = Task.Run(() => SendAsync(paths));
    }

    private List<string> ReadFileDrop()
    {
        // the copying program may still hold the clipboard for a moment
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(_hwnd))
            {
                Thread.Sleep(20);
                continue;
            }
            try
            {
                var drop = GetClipboardData(CF_HDROP);
                var list = new List<string>();
                if (drop == IntPtr.Zero) return list;
                var count = DragQueryFile(drop, uint.MaxValue, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var buffer = new StringBuilder((int)DragQueryFile(drop, i, null, 0) + 1);
                    DragQueryFile(drop, i, buffer, (uint)buffer.Capacity);
                    if (buffer.Length > 0) list.Add(buffer.ToString());
                }
                return list;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return new List<string>();
    }

    /// <param name="paths">本机剪贴板里被复制的文件 / 文件夹路径</param>
    private async Task SendAsync(List<string> paths)
    {
        var s = _deskflow.Settings;
        var peer = _deskflow.PeerHub?.Screen ?? s.RemoteHost;
        var signature = string.Join("\n", paths);
        lock (_gate)
        {
            // Explorer and some programs write the same file list several times in a row
            if (signature == _lastSignature && DateTime.UtcNow - _lastSignatureUtc < TimeSpan.FromSeconds(2)) return;
            _lastSignature = signature;
            _lastSignatureUtc = DateTime.UtcNow;
        }
        // references pasted from the other computer and copied again already live there
        if (paths.All(p => p.StartsWith(@"\\" + s.RemoteHost + @"\", StringComparison.OrdinalIgnoreCase))) return;
        // the side-channel file agent puts what it just received on this clipboard itself; sending that back would replace
        // the other computer's clipboard with a copy of its own file (seen 2026-11-23 16:47 with a queued PNG and PDF)
        if (JustReceived(s, paths))
        {
            Log.Info($"文件快传: 忽略旁路传输刚收到并放到剪贴板的 {Path.GetFileName(paths[0])}");
            return;
        }

        var payload = new FileClipboardPayload { Screen = _deskflow.LocalScreenName };
        foreach (var path in paths)
        {
            var item = Resolve(path);
            if (item == null)
            {
                Report($"「{path}」不在本机任何共享里，{peer} 无法直接粘贴（在资源管理器里共享它所在的磁盘或文件夹）", warning: true);
                return;
            }
            payload.Items.Add(item);
        }
        var what = payload.Items.Count == 1 ? payload.Items[0].Name : $"{payload.Items.Count} 项";
        var (ok, message) = await PeerLink.SendFileClipboardAsync(s.RemoteHost, payload).ConfigureAwait(false);
        Report(ok ? $"{what} 已放到 {peer} 的剪贴板，在那边粘贴时直接从本机拷贝" : $"{what} 没能放到 {peer} 的剪贴板：{message}", warning: !ok);
    }

    /// <summary>True when every file was written in the last minute into the download folder of the side-channel file agent.</summary>
    /// <param name="s">本机 Deskflow 设置（从旁路进程参数里取 --download-dir）</param>
    /// <param name="paths">剪贴板里的文件路径</param>
    private static bool JustReceived(DeskflowSettings s, List<string> paths)
    {
        var folder = s.Sidecars.Select(spec => DownloadDirArg.Match(spec.Arguments)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim('"');
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try { folder = Path.GetFullPath(folder).TrimEnd('\\') + @"\"; }
        catch { return false; }
        return paths.All(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                              && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < TimeSpan.FromMinutes(1));
    }

    /// <summary>Every local share containing <paramref name="path"/>, most specific first; null when none does.</summary>
    /// <param name="path">本机文件或文件夹的完整路径</param>
    private SharedFileRef? Resolve(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return null; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var locations = Shares()
            .Where(sh => full.Equals(sh.Path, StringComparison.OrdinalIgnoreCase) || full.StartsWith(sh.Path + @"\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sh => sh.Path.Length)
            .Select(sh => new ShareLocation { Share = sh.Name, Path = full.Length > sh.Path.Length ? full[(sh.Path.Length + 1)..] : string.Empty })
            .ToList();
        if (locations.Count == 0) return null;
        var name = Path.GetFileName(full);
        return new SharedFileRef { Name = name.Length > 0 ? name : full, Locations = locations };
    }

    /// <summary>Ordinary disk shares of this computer (administrative C$ / ADMIN$ excluded), cached for 30 s.</summary>
    private List<(string Name, string Path)> Shares()
    {
        lock (_gate)
        {
            if (_shares != null && DateTime.UtcNow - _sharesUtc < TimeSpan.FromSeconds(30)) return _shares;
        }
        var list = new List<(string Name, string Path)>();
        var resume = 0;
        var rc = NetShareEnum(null, 2, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (rc is 0 or 234)
            {
                var size = Marshal.SizeOf<SHARE_INFO_2>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<SHARE_INFO_2>(buffer + i * size);
                    if ((info.Type & 0xFF) != 0 || (info.Type & 0x80000000) != 0 || string.IsNullOrEmpty(info.Path) || string.IsNullOrEmpty(info.NetName)) continue;
                    list.Add((info.NetName, info.Path.TrimEnd('\\')));
                }
            }
            else Log.Warn($"文件快传: 枚举本机共享失败（错误 {rc}）");
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
        lock (_gate)
        {
            _shares = list;
            _sharesUtc = DateTime.UtcNow;
        }
        return list;
    }

    // ------------------------------------------------------------------ receiving side

    /// <summary>Puts the files the other hub copied on this computer's clipboard as UNC paths it can open.</summary>
    /// <param name="source">发送方地址（请求来源 IP），用来拼 \\地址\共享\路径</param>
    /// <param name="payload">发送方复制的文件列表及其所在共享</param>
    public async Task<(bool Ok, string Message)> ApplyRemoteAsync(IPAddress source, FileClipboardPayload payload)
    {
        if (!_deskflow.Settings.SmbFileClipboard) return (false, "对端关闭了 SMB 文件快传");
        if (source.AddressFamily != AddressFamily.InterNetwork) return (false, "只支持 IPv4 地址的对端");
        if (payload.Items.Count is 0 or > 2000) return (false, "文件列表为空或过长");
        if (payload.Items.Any(i => i.Locations.Count == 0 || i.Locations.Any(l => !ShareName.IsMatch(l.Share) || !ValidSharePath(l.Path))))
            return (false, "文件位置无效");
        var host = source.ToString();
        var sender = string.IsNullOrWhiteSpace(payload.Screen) ? host : payload.Screen;

        var shares = payload.Items.SelectMany(i => i.Locations).Select(l => l.Share).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (open, error) = await OpenSharesAsync(host, shares).ConfigureAwait(false);
        if (open.Count == 0 && IsLogonError(error) && WriteStoredCredential(host))
            (open, error) = await OpenSharesAsync(host, shares).ConfigureAwait(false);

        var paths = new List<string>();
        foreach (var item in payload.Items)
        {
            var location = item.Locations.FirstOrDefault(l => open.Contains(l.Share));
            if (location == null)
            {
                var hint = IsLogonError(error)
                    ? $"本机没有可用的 {sender} 账户，请在 Deskflow 页「文件快传」里填写 {sender} 的 Windows 账户和密码"
                    : $"无法访问 \\\\{host}\\{item.Locations[0].Share}（{Describe(error)}）";
                Report($"{sender} 复制的 {item.Name} 无法放到剪贴板：{hint}", warning: true);
                return (false, hint);
            }
            paths.Add($@"\\{host}\{location.Share}" + (location.Path.Length > 0 ? @"\" + location.Path : string.Empty));
        }

        if (!await OnWindowThread(() => SetFileDrop(paths)).ConfigureAwait(false)) return (false, "本机剪贴板被占用");
        var what = paths.Count == 1 ? payload.Items[0].Name : $"{paths.Count} 项";
        Report($"{sender} 复制的 {what} 已放到剪贴板，粘贴时直接从 {sender} 拷贝", warning: false);
        return (true, $"已放到剪贴板（{what}）");
    }

    /// <param name="path">共享内的相对路径：不能是绝对路径，也不能含 .. 或盘符</param>
    private static bool ValidSharePath(string path) =>
        path.Length < 4096 && !path.StartsWith('\\') && !path.Contains(':') && !path.Contains('/')
        && path.Split('\\').All(part => path.Length == 0 || (part.Length > 0 && part != "." && part != ".."));

    /// <param name="host">发送方 IP</param>
    /// <param name="shares">要探测的共享名（按优先顺序）</param>
    private static async Task<(HashSet<string> Open, int Error)> OpenSharesAsync(string host, List<string> shares)
    {
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var error = 0;
        foreach (var share in shares)
        {
            var unc = $@"\\{host}\{share}\";
            var probe = Task.Run(() => GetFileAttributes(unc) != uint.MaxValue ? 0 : Marshal.GetLastWin32Error());
            var finished = await Task.WhenAny(probe, Task.Delay(ProbeTimeout)).ConfigureAwait(false) == probe;
            var code = finished ? probe.Result : 121;
            if (code == 0) open.Add(share);
            else error = code;
            // no point in trying more shares of a computer that does not answer
            if (code is 53 or 64 or 121 or 1231) break;
        }
        return (open, error);
    }

    /// <param name="error">Win32 错误码</param>
    private static bool IsLogonError(int error) => error is 5 or 86 or 1326 or 1311 or 1907 or 1909 or 2202 or 1385;

    /// <param name="error">Win32 错误码</param>
    private static string Describe(int error) => error switch
    {
        53 or 64 or 1231 => "连不上对端的 SMB（445 端口）",
        121 => "SMB 连接超时",
        67 => "对端没有这个共享",
        _ => new System.ComponentModel.Win32Exception(error).Message,
    };

    /// <summary>Saves the peer account entered on the Deskflow page as Windows credentials for <paramref name="host"/>, so Explorer can paste from it.</summary>
    /// <param name="host">发送方 IP（凭据目标）</param>
    private bool WriteStoredCredential(string host)
    {
        var s = _deskflow.Settings;
        if (string.IsNullOrWhiteSpace(s.PeerShareUser) || string.IsNullOrEmpty(s.PeerSharePassword)) return false;
        string password;
        try { password = Dpapi.Unprotect(s.PeerSharePassword); }
        catch (Exception ex) { Log.Warn("文件快传: 读取保存的对端账户失败: " + ex.Message); return false; }
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new CREDENTIAL
            {
                Type = 2, // CRED_TYPE_DOMAIN_PASSWORD, what "cmdkey /add" and Explorer's "remember my credentials" write
                TargetName = host,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                UserName = s.PeerShareUser.Trim(),
            };
            var ok = CredWrite(ref credential, 0);
            Log.Info(ok ? $"文件快传: 已为 {host} 写入 Windows 凭据（{credential.UserName}）" : $"文件快传: 写入 {host} 的 Windows 凭据失败（错误 {Marshal.GetLastWin32Error()}）");
            return ok;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    /// <summary>Replaces the clipboard with a file list (CF_HDROP, "copy" drop effect) and remembers the change as our own.</summary>
    /// <param name="paths">要放到剪贴板的 UNC 路径</param>
    private bool SetFileDrop(List<string> paths)
    {
        const int header = 20; // DROPFILES: pFiles, pt.x, pt.y, fNC, fWide
        var text = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(_hwnd))
            {
                Thread.Sleep(20);
                continue;
            }
            var drop = GlobalAlloc(0x0002, (UIntPtr)(header + text.Length)); // GMEM_MOVEABLE
            var effect = GlobalAlloc(0x0002, (UIntPtr)4);
            try
            {
                if (drop == IntPtr.Zero || effect == IntPtr.Zero) return false;
                var p = GlobalLock(drop);
                Marshal.WriteInt32(p, 0, header);
                Marshal.WriteInt32(p, 4, 0);
                Marshal.WriteInt32(p, 8, 0);
                Marshal.WriteInt32(p, 12, 0);
                Marshal.WriteInt32(p, 16, 1);
                Marshal.Copy(text, 0, p + header, text.Length);
                GlobalUnlock(drop);
                Marshal.WriteInt32(GlobalLock(effect), 1); // DROPEFFECT_COPY: pasting copies, never moves
                GlobalUnlock(effect);
                EmptyClipboard();
                if (SetClipboardData(CF_HDROP, drop) != IntPtr.Zero) drop = IntPtr.Zero; // owned by the clipboard now
                if (_preferredDropEffect != 0 && SetClipboardData(_preferredDropEffect, effect) != IntPtr.Zero) effect = IntPtr.Zero;
                return drop == IntPtr.Zero;
            }
            finally
            {
                if (drop != IntPtr.Zero) GlobalFree(drop);
                if (effect != IntPtr.Zero) GlobalFree(effect);
                CloseClipboard();
                _ownSequence = GetClipboardSequenceNumber();
            }
        }
        return false;
    }

    /// <param name="message">状态说明</param>
    /// <param name="warning">是否需要提示用户</param>
    private void Report(string message, bool warning)
    {
        LastStatus = $"{DateTime.Now:HH:mm:ss} {message}";
        if (warning) Log.Warn("文件快传: " + message);
        else Log.Info("文件快传: " + message);
        if (warning) Notice?.Invoke(message, true);
    }

    // ------------------------------------------------------------------ native

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_2
    {
        public string NetName;
        public uint Type;
        public string Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public string Path;
        public string Password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")] private static extern uint GetFileAttributes(string path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint size);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string? server, int level, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries, ref int resume);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")] private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
}
