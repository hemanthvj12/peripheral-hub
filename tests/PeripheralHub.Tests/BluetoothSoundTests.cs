using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>"Set as default" for Bluetooth audio devices: which outputs, and which tiles get the button.</summary>
public class BluetoothSoundTests
{
    private static readonly Guid Buds = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid Other = new("bbbbbbbb-1111-2222-3333-444444444444");
    private const string Mac = "a1b2c3d4e5f6";

    private static AudioEndpoint Out(string id, Guid container, int? form) => new(id, true, "Buds", container, "BTHENUM", form);

    [Fact]
    public void The_stereo_output_is_chosen_over_the_hands_free_one()
    {
        var endpoints = new[]
        {
            Out("stereo", Buds, 3),     // headphones
            Out("hands-free", Buds, 5), // headset (call quality)
            new AudioEndpoint("mic", false, "Buds", Buds, "BTHENUM", 5),
            Out("speakers", Other, 1),
        };
        Assert.Equal(["stereo"], BluetoothSound.ChooseOutputs(endpoints, Buds));
    }

    [Fact]
    public void A_single_combined_output_is_used_even_if_it_is_a_headset()
    {
        // Newer Windows lists one endpoint for both profiles.
        Assert.Equal(["combined"], BluetoothSound.ChooseOutputs([Out("combined", Buds, 5)], Buds));
    }

    [Fact]
    public void No_outputs_outside_the_device_or_for_the_empty_container()
    {
        Assert.Empty(BluetoothSound.ChooseOutputs([Out("speakers", Other, 1)], Buds));
        Assert.Empty(BluetoothSound.ChooseOutputs([Out("steam", Guid.Empty, 1)], Guid.Empty));
    }

    private static DeviceSnapshot Snap(DeviceKind kind = DeviceKind.Audio, LinkState state = LinkState.Online, string? mac = Mac, SoundDevice? sound = null) =>
        new("bt:" + mac, "Buds", kind, ConnectionType.Bluetooth, state, 80, ChargeState.Unknown, null, BluetoothMac: mac, Sound: sound);

    [Fact]
    public void Connected_bluetooth_audio_gets_speakers_only_sound_info()
    {
        var bs = new BluetoothSound();
        bs.Use(new Dictionary<string, BluetoothSound.Outputs> { [Mac] = new(["stereo"], IsDefault: true) });

        Assert.Equal(new SoundDevice(HasOutput: true, HasInput: false, IsDefault: true), bs.Decorate(Snap()).Sound);
        Assert.True(bs.Handles(Mac.ToUpperInvariant()));
    }

    [Fact]
    public void Other_snapshots_are_left_alone()
    {
        var bs = new BluetoothSound();
        bs.Use(new Dictionary<string, BluetoothSound.Outputs> { [Mac] = new(["stereo"], IsDefault: false) });
        var own = new SoundDevice(true, true, false);

        Assert.Null(bs.Decorate(Snap(state: LinkState.Disconnected)).Sound);
        Assert.Null(bs.Decorate(Snap(kind: DeviceKind.Controller)).Sound);
        Assert.Null(bs.Decorate(Snap(mac: "000000000000")).Sound);
        Assert.Same(own, bs.Decorate(Snap(sound: own)).Sound); // a driver's own sound info wins
    }
}
