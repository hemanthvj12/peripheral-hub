using PeripheralHub.Plugins.UsbHeadsets;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>
/// USB headsets from Windows' sound devices. Seen live 2026-10-29: a CB CryoCore's speakers and mic share container
/// 36c59302-42e5-11f1-941f-806e6f6e6963 with enumerator "USB"; Steam Streaming and Punktfunk devices are "ROOT" with an
/// empty container; the monitor's speaker is "HDAUDIO".
/// </summary>
public class UsbHeadsetTests
{
    private static readonly Guid Cryo = new("36c59302-42e5-11f1-941f-806e6f6e6963");
    private static readonly Guid Cam = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid NoContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    private static AudioEndpoint Ep(string id, bool output, string name, Guid container, string bus = "USB") => new(id, output, name, container, bus);

    private static readonly AudioEndpoint[] Seen =
    [
        Ep("out-steam", true, "Steam Streaming Speakers", NoContainer, "ROOT"),
        Ep("out-monitor", true, "2- AMD High Definition Audio Device", new Guid("ef47bb67-9737-595d-8011-ae6eb70894f6"), "HDAUDIO"),
        Ep("out-cryo", true, "4- CB CryoCore", Cryo),
        Ep("in-cryo", false, "4- CB CryoCore", Cryo),
        Ep("in-steam", false, "Steam Streaming Microphone", NoContainer, "ROOT"),
        Ep("in-cam", false, "USB Webcam", Cam), // a webcam mic: no speakers, not a headset
    ];

    [Fact]
    public void Only_usb_devices_with_speakers_and_a_mic_are_headsets()
    {
        var h = Assert.Single(UsbHeadsetDriver.FindHeadsets(Seen));
        Assert.Equal((Cryo, "CB CryoCore"), (h.Container, h.Name));
        Assert.Equal(["out-cryo"], h.OutputIds);
        Assert.Equal(["in-cryo"], h.InputIds);
    }

    [Fact]
    public void Software_devices_sharing_the_empty_container_are_not_a_headset()
    {
        // Steam's virtual speakers and mic both have the "no container" id and would otherwise pair up.
        Assert.Empty(UsbHeadsetDriver.FindHeadsets([Ep("o", true, "Steam", NoContainer, "ROOT"), Ep("i", false, "Steam", NoContainer, "ROOT")]));
    }

    [Fact]
    public void A_wired_controllers_headphone_jack_is_not_a_headset()
    {
        // Owner's bug 2026-10-29: a DualSense and a DualShock 4 on USB showed as "DualSense Wireless Controller" and
        // "Wireless Controller" headsets. Their sound devices share the controller's container.
        var pad = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var withPad = Seen.Concat([Ep("out-ds", true, "2- Sony Audio", pad), Ep("in-ds", false, "2- Sony Audio", pad)]).ToList();

        Assert.Equal(["CB CryoCore"], UsbHeadsetDriver.FindHeadsets(withPad, new HashSet<Guid> { pad }).Select(h => h.Name));
        Assert.Equal(2, UsbHeadsetDriver.FindHeadsets(withPad).Count); // without knowing the controller, it would count
    }

    [Fact]
    public void Anything_windows_names_a_controller_is_not_a_headset()
    {
        var x = new Guid("12121212-3434-5656-7878-909090909090");
        Assert.Empty(UsbHeadsetDriver.FindHeadsets([Ep("o", true, "DualSense Wireless Controller", x), Ep("i", false, "DualSense Wireless Controller", x)]));
    }

    [Theory]
    [InlineData("4- CB CryoCore", "CB CryoCore")]
    [InlineData("CB CryoCore", "CB CryoCore")]
    [InlineData("12 - Arctis 7", "Arctis 7")]
    [InlineData("", "USB headset")]
    public void Windows_install_numbers_are_dropped_from_the_name(string adapter, string expected) =>
        Assert.Equal(expected, UsbHeadsetDriver.CleanName(adapter));

    [Fact]
    public void Default_means_both_its_speakers_and_its_mic()
    {
        var h = UsbHeadsetDriver.FindHeadsets(Seen)[0];
        Assert.True(UsbHeadsetDriver.IsDefault(h, "out-cryo", "in-cryo"));
        Assert.False(UsbHeadsetDriver.IsDefault(h, "out-monitor", "in-cryo"));
        Assert.False(UsbHeadsetDriver.IsDefault(h, "out-cryo", null));
    }
}
