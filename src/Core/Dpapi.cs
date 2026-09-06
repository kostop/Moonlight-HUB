using System.Runtime.InteropServices;
using System.Text;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

/// <summary>Per-user DPAPI wrapper (CryptProtectData) used to store Sunshine web credentials.</summary>
public static class Dpapi
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var input = new NativeMethods.DATA_BLOB { cbData = (uint)bytes.Length, pbData = ptr };
            if (!NativeMethods.CryptProtectData(ref input, "MoonlightHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return "dpapi:" + Convert.ToBase64String(result);
            }
            finally
            {
                NativeMethods.LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // legacy plain text
        var bytes = Convert.FromBase64String(stored["dpapi:".Length..]);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var input = new NativeMethods.DATA_BLOB { cbData = (uint)bytes.Length, pbData = ptr };
            if (!NativeMethods.CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                throw new InvalidOperationException("CryptUnprotectData failed: " + Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return Encoding.UTF8.GetString(result);
            }
            finally
            {
                NativeMethods.LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
