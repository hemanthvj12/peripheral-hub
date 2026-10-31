using PeripheralHub.Devices;
using PeripheralHub.Plugins;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>Plugin manifests and loading, using the real built-in plugins.</summary>
public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phplugins-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeripheralHub.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>Lays out the built-in plugins like the app ships them: plugin.json from source, dll from the build.</summary>
    private string BuiltInPlugins()
    {
        var target = Path.Combine(_root, "builtin");
        foreach (var project in Directory.GetDirectories(Path.Combine(RepoRoot(), "plugins")))
        {
            var manifest = PluginManifest.Load(Path.Combine(project, PluginManifest.FileName));
            var dir = Directory.CreateDirectory(Path.Combine(target, manifest.Id)).FullName;
            File.Copy(Path.Combine(project, PluginManifest.FileName), Path.Combine(dir, PluginManifest.FileName));
            File.Copy(Path.Combine(AppContext.BaseDirectory, manifest.Assembly), Path.Combine(dir, manifest.Assembly));
        }
        return target;
    }

    private string WriteManifest(string folder, string json)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), json);
        return dir;
    }

    private static string Manifest(string id, string version, int sdk = 1) =>
        $$"""{ "id": "{{id}}", "name": "Test", "version": "{{version}}", "sdkVersion": {{sdk}}, "assembly": "x.dll" }""";

    private string ActivatedFile => Path.Combine(_root, "activated-plugins.json");

    private DeviceHub Hub(params string[] pluginDirs) => new(new PeripheralHubOptions(), pluginDirs, ActivatedFile);

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static HidDeviceInfo Usb(ushort vid, ushort pid, string? manufacturer = null) =>
        new($@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#test", vid, pid, 0xFF02, 0x02, 17, 17, 0, null, manufacturer);

    private static HidDeviceInfo BluetoothLeHid(ushort vid, ushort pid) =>
        new($@"\\?\hid#{{00001812-0000-1000-8000-00805f9b34fb}}_dev_vid&02{vid:x4}_pid&{pid:x4}#test", vid, pid, 0x01, 0x02, 8, 0, 0, null);

    private static BleDeviceInfo Ble(ushort vid, ushort pid, bool connected) =>
        new("aa0000000001", vid, pid, "Device", connected, 70, Now, ["Input.Mouse"]);

    private static ClassicBtDeviceInfo Classic(ushort? vid, ushort? pid, bool connected) =>
        new("bb0000000002", vid, pid, "Wireless Controller", connected, null, null, ["Input.Gaming"]);

    private static ScanContext Scan(IReadOnlyList<HidDeviceInfo>? hid = null, IReadOnlyList<BleDeviceInfo>? le = null,
        IReadOnlyList<ClassicBtDeviceInfo>? classic = null) => new(hid ?? [], le ?? [], classic ?? [], Now);

    [Fact]
    public void Plugins_stay_dormant_until_their_device_is_detected()
    {
        using var hub = Hub(BuiltInPlugins());

        Assert.Empty(hub.Plugins.Errors);
        Assert.Equal(["aula-f75", "kreo-ikarus", "monitors", "nothing-ear", "sony-controllers", "usb-headsets", "xbox-controllers"], hub.Plugins.Available.Select(p => p.Manifest.Id));
        Assert.All(hub.Plugins.Available, p => Assert.Null(p.Plugin)); // no plugin code loaded
        Assert.Equal(["monitors", "usb-headsets", "xbox-controllers"], hub.Plugins.Active.Select(p => p.Manifest.Id)); // always on: its devices are invisible to the scan
        // Makers' apps are manifest data: available before the plugin's code runs (kept devices need them).
        Assert.Equal("OemDrv", hub.Plugins.VendorAppFor("aula-f75")?.ProcessName);
        Assert.False(hub.Plugins.ClaimsBluetooth(0x3554, 0xF533));

        hub.ActivateDetected(Scan(hid: [Usb(0x046D, 0xC52B)])); // someone else's receiver
        Assert.Equal(["monitors", "usb-headsets", "xbox-controllers"], hub.Plugins.Active.Select(p => p.Manifest.Id));
        Assert.False(File.Exists(ActivatedFile));
    }

    [Fact]
    public void Detected_device_turns_its_plugin_on_and_it_is_remembered()
    {
        string builtIn = BuiltInPlugins();
        using (var hub = Hub(builtIn))
        {
            var activated = new List<string>();
            hub.PluginActivated += p => activated.Add(p.Manifest.Id);

            hub.ActivateDetected(Scan(hid: [Usb(0x3554, 0xFB0E)])); // Kreo receiver plugged in

            var kreo = Assert.Single(hub.Plugins.Active, p => !p.Manifest.AlwaysOn);
            Assert.Equal("kreo-ikarus", kreo.Manifest.Id);
            Assert.NotNull(kreo.Plugin);
            Assert.Equal(["kreo-ikarus"], activated);
            Assert.True(hub.Plugins.ClaimsBluetooth(0x3554, 0xF533));

            hub.ActivateDetected(Scan(hid: [Usb(0x3554, 0xFB0E)])); // seen again: nothing new
            Assert.Single(activated);
        }

        // Next start: on straight away, even with nothing plugged in.
        using var restarted = Hub(builtIn);
        Assert.Equal(["kreo-ikarus", "monitors", "usb-headsets", "xbox-controllers"], restarted.Plugins.Active.Select(p => p.Manifest.Id));
    }

    [Fact]
    public void Every_built_in_plugin_starts_when_its_device_shows_up()
    {
        using var hub = Hub(BuiltInPlugins());
        hub.ActivateDetected(Scan(
            hid: [Usb(0x3554, 0xFB0C), Usb(0x258A, 0x010C, "BY Tech")],   // Kreo cable, F75 cable
            classic: [Classic(0x054C, 0x0CE6, connected: true)]));      // DualSense over Bluetooth

        Assert.Equal(["monitors", "usb-headsets", "xbox-controllers", "aula-f75", "kreo-ikarus", "sony-controllers"], hub.Plugins.Active.Select(p => p.Manifest.Id));
        Assert.Empty(hub.Plugins.Errors);
        Assert.Equal("OemDrv", hub.Plugins.VendorAppFor("aula-f75")?.ProcessName);
    }

    [Theory]
    [InlineData("kreo receiver", true)]
    [InlineData("kreo ble connected", true)]
    [InlineData("kreo ble paired but off", false)]
    [InlineData("kreo ble hid collection only", false)]  // judged by the Bluetooth connected flag, not lingering HID nodes
    [InlineData("generic sinowealth keyboard", false)]   // F75 cable ID without the BY Tech manufacturer
    [InlineData("f75 cable", true)]
    [InlineData("sony classic connected", true)]
    [InlineData("sony classic paired but off", false)]
    [InlineData("classic device without vendor id", false)]
    [InlineData("nothing ear connected", true)]
    [InlineData("nothing ear paired but off", false)]
    public void Detection_needs_a_present_or_connected_device(string situation, bool detected)
    {
        var (plugins, _) = PluginLoader.ReadManifests([BuiltInPlugins()]);
        var context = situation switch
        {
            "kreo receiver" => Scan(hid: [Usb(0x3554, 0xFB0E)]),
            "kreo ble connected" => Scan(le: [Ble(0x3554, 0xF533, true)]),
            "kreo ble paired but off" => Scan(le: [Ble(0x3554, 0xF533, false)]),
            "kreo ble hid collection only" => Scan(hid: [BluetoothLeHid(0x3554, 0xF533)]),
            "generic sinowealth keyboard" => Scan(hid: [Usb(0x258A, 0x010C, "SINO WEALTH")]),
            "f75 cable" => Scan(hid: [Usb(0x258A, 0x010C, "BY Tech")]),
            "sony classic connected" => Scan(classic: [Classic(0x054C, 0x09CC, true)]),
            "sony classic paired but off" => Scan(classic: [Classic(0x054C, 0x09CC, false)]),
            "nothing ear connected" => Scan(classic: [Classic(0x02B0, 0x0000, true)]),
            "nothing ear paired but off" => Scan(classic: [Classic(0x02B0, 0x0000, false)]),
            _ => Scan(classic: [Classic(null, null, true)]),
        };
        Assert.Equal(detected, plugins.Any(p => PluginMatcher.IsDetected(p.Manifest, context)));
    }

    [Fact]
    public void Plugin_that_fails_to_load_is_reported_once_and_others_still_work()
    {
        string dir = Path.Combine(_root, "p");
        WriteManifest(Path.Combine("p", "no-dll"), """
            { "id": "no-dll", "name": "N", "version": "1.0.0", "assembly": "x.dll",
              "devices": [ { "connection": "usb", "vendorId": "1234", "productId": "5678" } ] }
            """);
        using var hub = Hub(dir);

        hub.ActivateDetected(Scan(hid: [Usb(0x1234, 0x5678)]));
        hub.ActivateDetected(Scan(hid: [Usb(0x1234, 0x5678)]));

        Assert.Empty(hub.Plugins.Active);
        Assert.Contains("x.dll is missing", Assert.Single(hub.Plugins.Errors).Message);
    }

    [Fact]
    public void Manifests_give_vendor_apps()
    {
        var (available, _) = PluginLoader.ReadManifests([BuiltInPlugins()]);
        var set = new PluginSet(available, []);
        foreach (var p in available) set.MarkActive(PluginLoader.LoadCode(p));
        Assert.Equal("OemDrv", set.VendorAppFor("aula-f75")?.ProcessName);
        Assert.Equal("Kreo app", set.VendorAppFor("kreo-ikarus")?.DisplayName);
        Assert.Null(set.VendorAppFor("dualsense"));
    }

    [Theory]
    [InlineData(0x3554, 0xF533, true)]  // Kreo Ikarus over BLE
    [InlineData(0x3554, 0xFA07, true)]  // AULA F75 over BLE
    [InlineData(0x054C, 0x0CE6, true)]  // any Sony device
    [InlineData(0x054C, null, true)]    // classic Sony device with no product ID
    [InlineData(0x3554, 0xFB0E, false)] // Kreo receiver ID: only claimed over USB
    [InlineData(0x02B0, 0x0000, true)]  // Nothing earbuds (Ear (2)): the nothing-ear plugin reads them
    [InlineData(0x000A, 0x0000, false)] // other earphones stay with the generic Bluetooth driver
    public void Plugins_claim_their_bluetooth_devices(int vid, int? pid, bool claimed)
    {
        var (plugins, errors) = PluginLoader.ReadManifests([BuiltInPlugins()]);
        Assert.Empty(errors);
        var set = new PluginSet(plugins, []);
        foreach (var p in plugins) set.MarkActive(p);
        Assert.Equal(claimed, set.ClaimsBluetooth((ushort)vid, (ushort?)pid));
    }

    [Fact]
    public void Highest_version_wins_across_folders()
    {
        WriteManifest(Path.Combine("a", "dup"), Manifest("dup", "1.2.0"));
        var newer = WriteManifest(Path.Combine("b", "dup"), Manifest("dup", "1.10.0"));
        WriteManifest(Path.Combine("c", "dup"), Manifest("dup", "1.9.9"));

        var (plugins, errors) = PluginLoader.ReadManifests(new[] { "a", "b", "c" }.Select(d => Path.Combine(_root, d)));

        Assert.Empty(errors);
        Assert.Equal(newer, Assert.Single(plugins).Directory);
    }

    [Fact]
    public void Broken_or_too_new_plugins_are_reported_and_skipped()
    {
        WriteManifest(Path.Combine("p", "bad-json"), "{ not json");
        WriteManifest(Path.Combine("p", "future"), Manifest("future", "1.0.0", sdk: PluginApi.Version + 1));
        WriteManifest(Path.Combine("p", "ok"), Manifest("ok", "1.0.0"));

        var (plugins, errors) = PluginLoader.ReadManifests([Path.Combine(_root, "p"), Path.Combine(_root, "missing")]);

        Assert.Equal("ok", Assert.Single(plugins).Manifest.Id);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Message.Contains("Update PeripheralHub"));
    }

    [Fact]
    public void Manifest_paths_cannot_leave_the_plugin_folder()
    {
        // The old "models" (pictures) entry is ignored now: an older manifest still loads.
        var dir = WriteManifest("escape", """
            { "id": "escape", "name": "E", "version": "1.0.0", "assembly": "x.dll",
              "models": [ { "id": "m", "image": "..\\..\\secret.png" } ] }
            """);
        var (plugins, errors) = PluginLoader.ReadManifests([_root]);
        Assert.Empty(errors);
        Assert.Null(plugins.Single().PathOf(@"..\escape2\x.dll"));
        Assert.NotNull(plugins.Single().PathOf("x.dll"));
        Assert.Equal(dir, plugins.Single().Directory);
    }

    [Theory]
    [InlineData("""{ "connection": "serial", "vendorId": "3554" }""", "must be usb or bluetooth")]
    [InlineData("""{ "connection": "usb", "vendorId": "XYZ" }""", "isn't a 4-digit hex ID")]
    public void Bad_device_entries_are_rejected(string device, string message)
    {
        var json = $$"""{ "id": "t", "name": "T", "version": "1.0.0", "assembly": "x.dll", "devices": [ {{device}} ] }""";
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(json)).Message);
    }
}
