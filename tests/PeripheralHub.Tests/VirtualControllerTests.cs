using PeripheralHub.Plugins.SonyControllers;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>Telling software-created (virtual) controllers from real ones, from Windows' device tree (seen 2026-10-28).</summary>
public class VirtualControllerTests
{
    [Fact]
    public void Hid_interface_path_maps_to_its_device_instance_id()
    {
        Assert.Equal(@"hid\vid_054c&pid_0ce6&mi_03\1&8ad35a9&3&0000",
            DeviceTree.InstanceIdFromPath(@"\\?\hid#vid_054c&pid_0ce6&mi_03#1&8ad35a9&3&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    }

    [Theory]
    [InlineData(@"SWD\VID_054C&PID_0CE6&MI_03\PF_PAD_0", true)]   // punktfunk's virtual DualSense
    [InlineData(@"SWD\PUNKTFUNK\PF_MOUSE_0", true)]             // punktfunk's virtual mouse
    [InlineData(@"ROOT\SYSTEM\0002", true)]                     // a root-enumerated bus driver such as ViGEmBus
    [InlineData(@"ROOT\ACPI_HAL\0000", false)]                  // the real PC's hardware root: every real device is under it
    [InlineData(@"USB\VID_054C&PID_0CE6\8&F2CB6FA&0&9", false)]
    public void Software_created_ancestors(string instanceId, bool software)
    {
        Assert.Equal(software, DeviceTree.IsSoftwareCreated(instanceId));
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_7AE0&SUBSYS_00000000&REV_11\3&11583659&0&A0", true)] // USB controller
    [InlineData(@"ACPI\PNP0A08\0", true)]
    [InlineData(@"ACPI_HAL\PNP0C08\0", true)]
    [InlineData(@"SWD\PUNKTFUNK\PF_MOUSE_0", false)]
    public void Hardware_buses(string instanceId, bool hardware)
    {
        Assert.Equal(hardware, DeviceTree.IsHardwareBus(instanceId));
    }

    [Theory]
    [InlineData("Punktfunk Virtual DualSense", "Punktfunk")]
    [InlineData("DSX Virtual DualSense Controller", "DSX")]
    [InlineData("Nefarius Emulation Bus G2", "Nefarius")]   // DSX's emulated DualShock 4 / DualSense hang off this driver
    [InlineData("Virtual DualSense", "")]   // nothing but generic words → the tile says "DualSense (Virtual)"
    [InlineData(null, "")]
    public void Creator_name_is_shortened_for_the_tile(string? deviceName, string creator)
    {
        Assert.Equal(creator, VirtualOrigin.ShortName(deviceName));
    }
}
