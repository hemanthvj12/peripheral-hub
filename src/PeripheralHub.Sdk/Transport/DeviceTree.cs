using PeripheralHub.Native;

namespace PeripheralHub.Transport;

/// <summary>The software that created a virtual device, e.g. Windows instance "SWD\…\PF_PAD_0" named "Punktfunk Virtual DualSense".</summary>
public sealed record VirtualOrigin(string InstanceId, string? Name)
{
    /// <summary>Short creator name for a badge (see <see cref="ShortName"/>), e.g. "Punktfunk" or "Nefarius".</summary>
    public string ShortCreator => ShortName(Name);

    /// <summary>
    /// "Punktfunk Virtual DualSense" → "Punktfunk"; "Nefarius Virtual Gamepad Emulation Bus G2" (the driver DSX's
    /// emulated controllers hang off, seen 2026-10-28) → "Nefarius"; "" when only generic words are left.
    /// </summary>
    public static string ShortName(string? deviceName)
    {
        string[] generic = ["virtual", "dualsense", "dualshock", "edge", "4", "wireless", "controller", "gamepad", "device",
            "emulated", "emulation", "bus", "driver", "enumerator", "software", "root", "xbox", "360", "for", "windows"];
        var words = (deviceName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !generic.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Where(w => !(w.Length <= 3 && w.Any(char.IsDigit))); // version tags like "G2", "v3"
        return words.FirstOrDefault() ?? ""; // first word only, so the badge stays short
    }
}

/// <summary>Where a device sits in Windows' device tree.</summary>
public static class DeviceTree
{
    /// <summary>
    /// Real devices hang off hardware: walking up the tree reaches a USB host controller or Bluetooth radio on the
    /// PCI/ACPI buses first.
    /// Virtual ones (game-streaming and remapping tools: punktfunk, DSX, DS4Windows…) are created by software, so their
    /// chain passes through a software device (<c>SWD\</c>) or a root-enumerated bus driver (<c>ROOT\</c>, e.g. ViGEmBus).
    /// Returns that ancestor, or null for a real device (or when the device can't be found).
    /// Verified 2026-10-28: punktfunk's virtual DualSense = HID child of <c>SWD\VID_054C&amp;PID_0CE6&amp;MI_03\PF_PAD_0</c>.
    /// </summary>
    public static VirtualOrigin? FindVirtualOrigin(string instanceId)
    {
        if (Win32.CM_Locate_DevNodeW(out int node, instanceId, 0) != Win32.CrSuccess) return null;
        for (int depth = 0; depth < 16; depth++)
        {
            if (Win32.CM_Get_Parent(out int parent, node, 0) != Win32.CrSuccess) return null;
            var id = DeviceId(parent);
            // Reached real hardware (USB controllers and Bluetooth radios sit on PCI/ACPI) or the top: a real device.
            if (id is null || IsHardwareBus(id) || id.StartsWith(@"HTREE\ROOT", StringComparison.OrdinalIgnoreCase)) return null;
            if (IsSoftwareCreated(id))
                return new VirtualOrigin(id, BluetoothDevices.ReadString(parent, BluetoothDevices.FriendlyName)
                                             ?? BluetoothDevices.ReadString(parent, BluetoothDevices.DeviceDesc));
            node = parent;
        }
        return null;
    }

    /// <summary>
    /// <c>SWD\</c> (software device) or a root-enumerated driver such as ViGEmBus (<c>ROOT\SYSTEM\…</c>). Not
    /// <c>ROOT\ACPI_HAL\…</c>: that's the real PC's hardware root, which every real device hangs under (checked
    /// 2026-10-28: a rule without this exception flagged all 25 real HID collections as virtual).
    /// </summary>
    public static bool IsSoftwareCreated(string instanceId) =>
        instanceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) ||
        (instanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) &&
         !instanceId.StartsWith(@"ROOT\ACPI_HAL", StringComparison.OrdinalIgnoreCase));

    public static bool IsHardwareBus(string instanceId) =>
        instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ||
        instanceId.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) ||
        instanceId.StartsWith(@"ACPI_HAL\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The device instance id of a HID interface path:
    /// \\?\hid#vid_054c&amp;pid_0ce6&amp;mi_03#1&amp;8ad35a9&amp;3&amp;0000#{4d1e55b2-…} → HID\vid_054c&amp;pid_0ce6&amp;mi_03\1&amp;8ad35a9&amp;3&amp;0000.
    /// </summary>
    public static string? InstanceIdFromPath(string interfacePath)
    {
        var p = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? interfacePath[4..] : interfacePath;
        int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) p = p[..guid];
        return p.Length == 0 ? null : p.Replace('#', '\\');
    }

    private static string? DeviceId(int node)
    {
        var buf = new char[512];
        return Win32.CM_Get_Device_IDW(node, buf, buf.Length, 0) == Win32.CrSuccess ? new string(buf).TrimEnd('\0') : null;
    }
}
