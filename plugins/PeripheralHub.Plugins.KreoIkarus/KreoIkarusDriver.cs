using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.KreoIkarus;

/// <summary>Kreo Ikarus: cable 3554:FB0C, 2.4G receiver 3554:FB0E, Bluetooth LE 3554:F533.</summary>
internal sealed class KreoIkarusDriver(PeripheralHubOptions options, VendorApp? vendorApp)
    : ReceiverDeviceDriver<KreoIkarusDriver.Session>(options, vendorApp)
{
    public const ushort Vid = 0x3554, WiredPid = 0xFB0C, DonglePid = 0xFB0E, BlePid = 0xF533;

    protected override string Id => "kreo-ikarus";
    protected override string Name => "Kreo Ikarus";
    protected override DeviceKind Kind => DeviceKind.Mouse;

    protected override bool IsVendorCollection(HidDeviceInfo h) =>
        (h.Matches(Vid, WiredPid) || h.Matches(Vid, DonglePid)) && !h.IsBluetooth &&
        h.UsagePage == 0xFF02 && h.Usage == 0x02 && h.InputReportLength == KreoProtocol.ReportLength;

    protected override bool IsWiredCollection(HidDeviceInfo h) => h.ProductId == WiredPid;
    protected override bool IsBle(BleDeviceInfo b) => b.VendorId == Vid && b.ProductId == BlePid;
    protected override Session CreateSession(HidConnection c) => new(c, Options);

    protected override WiredObservation? ObserveWired(Session? s, bool wiredPresent)
    {
        if (s is null) return null;
        var b = s.Battery;
        // Verified 2026-10-27: on the cable the Kreo reports a real level (85% on the receiver read 90% right after
        // plugging in, steady over a minute), not a value pinned to 100 like the F75. Charging inflates it slightly.
        return new WiredObservation(s.ShownPercent,
            b is null ? ChargeState.Unknown : b.Value.Charging ? ChargeState.Charging : ChargeState.Unknown,
            b?.Millivolts, s.BatteryAt, null);
    }

    protected override DongleObservation ObserveDongle(Session s)
    {
        var b = s.Battery;
        return new DongleObservation(s.Online, s.ShownPercent,
            b is null ? ChargeState.Unknown : b.Value.Charging ? ChargeState.Charging : ChargeState.Discharging,
            b?.Millivolts, s.BatteryAt, s.LastSeenAt, s.StartedAt);
    }

    internal sealed class Session(HidConnection connection, PeripheralHubOptions options) : PolledHidSession(connection, options)
    {
        private volatile bool _batteryDue = true;
        private DateTimeOffset _lastBatteryQuery = DateTimeOffset.MinValue;

        public bool? Online { get; private set; }
        protected override bool IsReachable => Online == true;
        public KreoBattery? Battery { get; private set; }
        public DateTimeOffset? BatteryAt { get; private set; }

        protected override void OnReport(byte[] report)
        {
            // The mouse pushes a status-change frame (e.g. battery changed, woke up); re-read right away.
            if (KreoProtocol.IsStatusPush(report)) { _batteryDue = true; Wake(); return; }
            // Every reply reaches every app that has the channel open, so this also sees replies to the Kreo app's
            // own requests. While paused, that's how the tile stays current.
            if (KreoProtocol.ParseOnline(report) is { } online) ApplyOnline(online);
            else if (KreoProtocol.ParseBattery(report) is { } battery) ApplyBattery(battery);
        }

        private void ApplyOnline(bool online)
        {
            if (online) LastSeenAt = DateTimeOffset.Now;
            if (online == Online) return;
            if (online && Online != true) _batteryDue = true; // just woke/connected
            Online = online;
            RaiseChanged();
        }

        /// <summary>The level to show: <see cref="KreoBattery.DisplayPercent"/>, smoothed by <see cref="Smooth"/>.</summary>
        public int? ShownPercent { get; private set; }

        private void ApplyBattery(KreoBattery battery)
        {
            _batteryDue = false;
            BatteryAt = DateTimeOffset.Now;
            int shown = Smooth(ShownPercent, battery.DisplayPercent, battery.Charging);
            if (battery == Battery && shown == ShownPercent) return;
            Battery = battery;
            ShownPercent = shown;
            RaiseChanged();
        }

        /// <summary>
        /// The voltage-based level wobbles about ±1% with load. A battery that isn't charging can't gain charge, so
        /// small rises while discharging are ignored. Bigger rises (it was charged somewhere else) and every reading
        /// while charging go through.
        /// </summary>
        internal static int Smooth(int? previous, int reading, bool charging) =>
            previous is { } p && !charging && reading > p && reading - p <= 3 ? p : reading;

        protected override async Task PollAsync(CancellationToken ct)
        {
            // OnReport applies replies too; applying them here as well avoids depending on which runs first.
            var reply = await RequestAsync(KreoProtocol.BuildRequest(KreoProtocol.CmdOnline),
                r => KreoProtocol.IsReplyTo(r, KreoProtocol.CmdOnline), ct);
            if (reply is not null && KreoProtocol.ParseOnline(reply) is { } online) ApplyOnline(online);
            if (Online != true) return;

            var now = DateTimeOffset.Now;
            if (!_batteryDue && now - _lastBatteryQuery < Options.BatteryPollInterval) return;
            _lastBatteryQuery = now;

            var b = await RequestAsync(KreoProtocol.BuildRequest(KreoProtocol.CmdBattery),
                r => KreoProtocol.IsReplyTo(r, KreoProtocol.CmdBattery), ct);
            if (b is not null && KreoProtocol.ParseBattery(b) is { } battery) ApplyBattery(battery);
        }
    }
}
