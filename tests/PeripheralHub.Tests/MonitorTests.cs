using PeripheralHub.Plugins.Monitors;

namespace PeripheralHub.Tests;

/// <summary>
/// Monitors. Seen live 2026-10-29: an Acer VG240Y S on DisplayPort (output technology 10), instance
/// DISPLAY\ACR0750\7&amp;315967f&amp;1&amp;UID512, EDID serial text "344027044", DDC/CI brightness 0/100 and volume 100/100.
/// </summary>
public class MonitorTests
{
    [Theory]
    [InlineData(10u, ConnectionType.DisplayPort)]
    [InlineData(5u, ConnectionType.Hdmi)]
    [InlineData(18u, ConnectionType.UsbC)]
    [InlineData(4u, ConnectionType.Dvi)]
    [InlineData(0u, ConnectionType.Vga)]
    [InlineData(15u, ConnectionType.Wireless)]
    [InlineData(17u, ConnectionType.Virtual)]
    [InlineData(11u, ConnectionType.BuiltIn)]
    [InlineData(0x80000000u, ConnectionType.BuiltIn)]
    [InlineData(16u, ConnectionType.Wired)]
    public void Output_technology_picks_the_badge(uint tech, ConnectionType expected) =>
        Assert.Equal(expected, MonitorDriver.ConnectionFor(tech, createdBySoftware: false));

    [Fact]
    public void A_display_made_by_software_is_virtual_whatever_it_claims() =>
        Assert.Equal(ConnectionType.Virtual, MonitorDriver.ConnectionFor(10, createdBySoftware: true));

    [Fact]
    public void Only_real_video_cables_use_ddc()
    {
        Assert.True(MonitorDriver.UsesDdc(ConnectionType.DisplayPort));
        Assert.True(MonitorDriver.UsesDdc(ConnectionType.Hdmi));
        Assert.False(MonitorDriver.UsesDdc(ConnectionType.BuiltIn));
        Assert.False(MonitorDriver.UsesDdc(ConnectionType.Virtual));
        Assert.False(MonitorDriver.UsesDdc(ConnectionType.Wireless));
    }

    [Fact]
    public void Pnp_id_comes_from_the_instance_id()
    {
        Assert.Equal("ACR0750", MonitorDriver.PnpId(@"DISPLAY\ACR0750\7&315967f&1&UID512"));
        Assert.Null(MonitorDriver.PnpId(@"SWD\SOMETHING\1"));
    }

    [Theory]
    [InlineData("ACR0750", "VG240Y S", ConnectionType.DisplayPort, "Acer VG240Y S")]
    [InlineData("DEL4321", "DELL U2720Q", ConnectionType.DisplayPort, "DELL U2720Q")]   // maker already in the name
    [InlineData("CHO0030", "Smart TV", ConnectionType.Hdmi, "Smart TV")]             // maker unknown
    [InlineData("SAM1234", "", ConnectionType.Hdmi, "Samsung monitor")]
    [InlineData("BOE0123", "", ConnectionType.BuiltIn, "Built-in display")]
    [InlineData("LGD0567", "LP140WF7", ConnectionType.BuiltIn, "Built-in display")] // panel part number: left out
    [InlineData(null, "", ConnectionType.Wired, "Monitor")]
    public void Names_add_the_maker_when_known(string? pnp, string friendly, ConnectionType c, string expected) =>
        Assert.Equal(expected, MonitorDriver.NameFor(pnp, friendly, c));

    private static byte[] Edid(uint numericSerial = 0, string? serialText = null)
    {
        var e = new byte[128];
        BitConverter.GetBytes(numericSerial).CopyTo(e, 12);
        if (serialText is not null)
        {
            // Second descriptor block (offset 72): 00 00 00 FF 00 then 13 bytes of text ended by 0A, padded with spaces.
            e[72 + 3] = 0xFF;
            var text = (serialText + "\n").PadRight(13, ' ');
            for (int i = 0; i < 13; i++) e[72 + 5 + i] = (byte)text[i];
        }
        return e;
    }

    [Fact]
    public void Serial_prefers_the_text_descriptor()
    {
        Assert.Equal("344027044", MonitorDriver.EdidSerial(Edid(numericSerial: 1234, serialText: "344027044")));
        Assert.Equal("1234", MonitorDriver.EdidSerial(Edid(numericSerial: 1234)));
        Assert.Null(MonitorDriver.EdidSerial(Edid()));                         // no serial at all
        Assert.Null(MonitorDriver.EdidSerial(Edid(numericSerial: 0x01010101))); // placeholder some makers use
        Assert.Equal("1234", MonitorDriver.EdidSerial(Edid(numericSerial: 1234, serialText: "000000"))); // zero text: ignored
        Assert.Null(MonitorDriver.EdidSerial(new byte[10]));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(32, 64, 50)]
    [InlineData(120, 100, 100)]
    [InlineData(5, 0, 0)]
    public void Levels_show_as_percent(int value, int max, int percent) =>
        Assert.Equal(percent, new MonitorLevel(value, max).Percent);
}
