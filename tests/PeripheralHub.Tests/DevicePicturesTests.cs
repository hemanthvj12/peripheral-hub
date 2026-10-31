namespace PeripheralHub.Tests;

/// <summary>
/// Earbuds or headset for the generic audio picture. Windows can't tell: Ear (2) reports class of device 0x240404
/// ("wearable headset") and category Communication.Headset.Bluetooth (checked 2026-10-29).
/// </summary>
public class DevicePicturesTests
{
    [Theory]
    [InlineData("Ear (2)")]
    [InlineData("Nothing Ear")]
    [InlineData("Nothing Ear (a)")]
    [InlineData("Galaxy Buds2 Pro")]
    [InlineData("Galaxy Buds")]
    [InlineData("AirPods Pro")]
    [InlineData("HUAWEI FreeBuds 5i")]
    [InlineData("realme Buds Air 5")]
    [InlineData("boAt Airdopes TWS")]
    [InlineData("JBL Tune Earbuds")]
    [InlineData("OnePlus Bullets Wireless Earphones")]
    public void Earbud_names(string name) => Assert.True(DevicePictures.IsEarbuds(name, null));

    [Theory]
    [InlineData("CB CryoCore")]
    [InlineData("WH-1000XM5")]
    [InlineData("HyperX Cloud II Wireless")]
    [InlineData("Arctis Nova 7")]
    [InlineData("Gaming Headset Gear")]
    [InlineData("Clear Audio Headphones")]
    public void Headset_names(string name) => Assert.False(DevicePictures.IsEarbuds(name, null));

    [Fact]
    public void Left_and_right_batteries_mean_earbuds_whatever_the_name() =>
        Assert.True(DevicePictures.IsEarbuds("Some Wireless", [new BatteryPart("L", 80, false, true), new BatteryPart("R", 80, false, true)]));
}
