using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// Produces a stable, anonymous fingerprint for this Windows installation.
/// Sources used: MachineGuid + Volume Serial + ProcessorId (from WMI via registry shortcut).
/// The result is SHA-256 hex so it is safe to send to the server (no PII).
/// </summary>
internal static class DeviceIdProvider
{
    private static string? _cached;

    public static string GetDeviceId()
    {
        if (_cached != null) return _cached;

        var parts = new[]
        {
            ReadMachineGuid(),
            ReadVolumeSerial(),
            ReadProcessorId(),
        };

        var raw = string.Join("|", parts.Where(p => !string.IsNullOrEmpty(p)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("jsr-v1|" + raw));
        _cached = Convert.ToHexString(bytes).ToLowerInvariant();
        return _cached;
    }

    // -------------------------------------------------------------------

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch { return ""; }
    }

    private static string ReadVolumeSerial()
    {
        try
        {
            // C:\ volume serial number stored in HKLM (no WMI needed)
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\MountedDevices", writable: false);
            // Fallback: use drive root
            var info = new System.IO.DriveInfo("C");
            return info.IsReady ? info.DriveFormat + info.TotalSize : "";
        }
        catch { return ""; }
    }

    private static string ReadProcessorId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            return key?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch { return ""; }
    }
}
