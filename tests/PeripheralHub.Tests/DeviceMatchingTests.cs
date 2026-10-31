using PeripheralHub.Plugins.AulaF75;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>Which USB devices count as which product.</summary>
public class DeviceMatchingTests
{
    private static HidDeviceInfo Hid(ushort vid, ushort pid, string? manufacturer) =>
        new(@"\\?\hid#test", vid, pid, 0x0001, 0x06, 9, 2, 0, SerialNumber: null, Manufacturer: manufacturer);

    [Fact]
    public void Wired_F75_is_recognised_by_its_generic_id_plus_BY_Tech()
    {
        // Captured 2026-10-27: manufacturer "BY Tech", product "Gaming Keyboard", no serial.
        Assert.True(AulaF75Driver.IsWiredF75(Hid(0x258A, 0x010C, "BY Tech")));
    }

    [Theory]
    [InlineData("SINO WEALTH")]
    [InlineData(null)]
    public void Other_keyboards_with_the_same_generic_id_are_not_the_F75(string? manufacturer)
    {
        Assert.False(AulaF75Driver.IsWiredF75(Hid(0x258A, 0x010C, manufacturer)));
    }
}
