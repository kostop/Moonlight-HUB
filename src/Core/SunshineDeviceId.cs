using System.Security.Cryptography;
using System.Text;

namespace MoonlightHub.Core;

/// <summary>
/// Reproduces libdisplaydevice's (Sunshine) Windows device id: a SHA-1 name-based UUID (nil namespace)
/// over EDID bytes + the stable parts of the PnP instance id (UTF-16LE, including the terminating NUL).
/// Verified on this machine against the ids Sunshine prints in "Currently available display devices".
/// </summary>
public static class SunshineDeviceId
{
    public static string Compute(string instanceId, byte[]? edid, string devicePathFallback)
    {
        var data = new List<byte>();
        if (!string.IsNullOrEmpty(instanceId))
        {
            var i1 = instanceId.IndexOf('&');
            var i2 = i1 >= 0 ? instanceId.IndexOf('&', i1 + 1) : -1;
            var i3 = i2 >= 0 ? instanceId.IndexOf('&', i2 + 1) : -1;
            if (i2 >= 0 && i3 >= 0)
            {
                if (edid != null) data.AddRange(edid);
                data.AddRange(Encoding.Unicode.GetBytes(instanceId[..i2]));
                data.AddRange(Encoding.Unicode.GetBytes(instanceId[i3..]));
                data.Add(0);
                data.Add(0); // std::wstring sized with the terminating NUL in libdisplaydevice
            }
        }

        if (data.Count == 0)
        {
            if (string.IsNullOrEmpty(devicePathFallback)) return string.Empty;
            data.AddRange(Encoding.Unicode.GetBytes(devicePathFallback));
        }

        return "{" + Uuid5Nil(data.ToArray()) + "}";
    }

    /// <summary>RFC 4122 v5 UUID with the nil namespace (boost::uuids::name_generator_sha1{uuid{}}).</summary>
    public static string Uuid5Nil(byte[] name)
    {
        var buffer = new byte[16 + name.Length];
        Buffer.BlockCopy(name, 0, buffer, 16, name.Length);
        var hash = SHA1.HashData(buffer);
        var u = new byte[16];
        Buffer.BlockCopy(hash, 0, u, 0, 16);
        u[6] = (byte)((u[6] & 0x0F) | 0x50);
        u[8] = (byte)((u[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(u).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
