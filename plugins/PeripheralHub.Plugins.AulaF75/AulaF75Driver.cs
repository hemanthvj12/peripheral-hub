using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.AulaF75;

/// <summary>
/// AULA F75: 2.4G receiver 3554:FA09, Bluetooth LE 3554:FA07, cable 258A:010C.
/// The cable shows up as a Sinowealth "Gaming Keyboard" with no known battery protocol, so it only proves presence.
/// 258A:010C is a generic Sinowealth ID, so the manufacturer string must also be "BY Tech" (AULA's keyboard maker; checked
/// 2026-10-27, product string is just "Gaming Keyboard"). Another BY Tech-built board with the same ID would still match.
/// </summary>
internal sealed class AulaF75Driver(PeripheralHubOptions options, VendorApp? vendorApp)
    : ReceiverDeviceDriver<AulaF75Driver.Session>(options, vendorApp)
{
    public const ushort Vid = 0x3554, DonglePid = 0xFA09, BlePid = 0xFA07;
    public const ushort WiredVid = 0x258A, WiredPid = 0x010C;
    public const string WiredManufacturer = "BY Tech";

    protected override string Id => "aula-f75";
    protected override string Name => "AULA F75";
    protected override DeviceKind Kind => DeviceKind.Keyboard;

    protected override bool IsVendorCollection(HidDeviceInfo h) =>
        h.Matches(Vid, DonglePid) && !h.IsBluetooth &&
        h.UsagePage == 0xFF02 && h.Usage == 0x02 && h.InputReportLength == AulaProtocol.ReportLength;

    protected override bool IsWiredCollection(HidDeviceInfo h) => false;
    protected override bool IsWiredPresence(HidDeviceInfo h) => IsWiredF75(h);

    internal static bool IsWiredF75(HidDeviceInfo h) =>
        h.Matches(WiredVid, WiredPid) && string.Equals(h.Manufacturer, WiredManufacturer, StringComparison.OrdinalIgnoreCase);
    protected override bool IsBle(BleDeviceInfo b) => b.VendorId == Vid && b.ProductId == BlePid;
    protected override Session CreateSession(HidConnection c) => new(c, Options);

    protected override WiredObservation? ObserveWired(Session? s, bool wiredPresent) =>
        wiredPresent ? new WiredObservation(null, ChargeState.Charging, null, null, "The F75 doesn't report its level over USB") : null;

    protected override DongleObservation ObserveDongle(Session s)
    {
        var b = s.Battery;
        if (b is { CableIn: true })
            return new DongleObservation(s.Online, null, ChargeState.Charging, null, s.BatteryAt, s.LastSeenAt, s.StartedAt,
                "Level isn't reported while charging");
        return new DongleObservation(s.Online, b?.Percent, b is null ? ChargeState.Unknown : ChargeState.Discharging,
            null, s.BatteryAt, s.LastSeenAt, s.StartedAt);
    }

    internal sealed class Session(HidConnection connection, PeripheralHubOptions options) : PolledHidSession(connection, options)
    {
        private volatile bool _batteryDue = true;
        private DateTimeOffset _lastBatteryQuery = DateTimeOffset.MinValue;

        public bool? Online { get; private set; }
        protected override bool IsReachable => Online == true;
        public AulaBattery? Battery { get; private set; }
        public DateTimeOffset? BatteryAt { get; private set; }

        protected override void OnReport(byte[] report)
        {
            // Every reply reaches every app that has the channel open, so this also sees replies to the AULA app's
            // requests, plus the receiver's own status pushes. While paused, that's how the tile stays current.
            var (linked, pushedBattery) = AulaProtocol.ParseStatusPush(report);
            if (linked is { } l) { ApplyOnline(l); if (l) _batteryDue = true; Wake(); }
            if (pushedBattery is { } pb) ApplyBattery(pb);
            if (AulaProtocol.ParseOnline(report) is { } online) ApplyOnline(online);
            if (AulaProtocol.ParseBattery(report) is { } battery) ApplyBattery(battery);
        }

        private void ApplyOnline(bool online)
        {
            if (online) LastSeenAt = DateTimeOffset.Now;
            if (online == Online) return;
            if (online) _batteryDue = true;
            Online = online;
            RaiseChanged();
        }

        private void ApplyBattery(AulaBattery battery)
        {
            _batteryDue = false;
            BatteryAt = DateTimeOffset.Now;
            if (battery == Battery) return;
            Battery = battery;
            RaiseChanged();
        }

        protected override async Task PollAsync(CancellationToken ct)
        {
            // OnReport applies replies too; applying them here as well avoids depending on which runs first.
            var reply = await RequestAsync(AulaProtocol.OnlineRequest(),
                r => AulaProtocol.IsReplyTo(r, AulaProtocol.CmdOnline), ct);
            if (reply is not null && AulaProtocol.ParseOnline(reply) is { } online) ApplyOnline(online);
            if (Online != true) return;

            var now = DateTimeOffset.Now;
            if (!_batteryDue && now - _lastBatteryQuery < Options.BatteryPollInterval) return;
            _lastBatteryQuery = now;

            var b = await RequestAsync(AulaProtocol.BatteryRequest(),
                r => AulaProtocol.IsReplyTo(r, AulaProtocol.CmdBattery), ct);
            if (b is not null && AulaProtocol.ParseBattery(b) is { } battery) ApplyBattery(battery);
        }
    }
}
