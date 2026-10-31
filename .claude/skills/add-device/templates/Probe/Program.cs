// Throwaway probe for the add-device skill (see ../../SKILL.md). Commands:
//   list                                   every HID collection, Bluetooth LE device and classic Bluetooth device (with services)
//   hid    <path-substring> <hex request>  send ONE request on the first matching HID collection, print replies for 3 s
//   listen <path-substring> [seconds]      print every report on a HID collection (e.g. while the vendor app runs)
//   bt     <mac> <service-guid> <hex>      send ONE request on a classic Bluetooth serial service, print replies for 3 s
//   watch  [plugins-dir] [seconds]         run the hub with built plugins and print every device snapshot
// Only send requests you have confirmed are read-only: vendor command sets include writes to flash.
using PeripheralHub;
using PeripheralHub.Transport;

static byte[] Hex(string s) => s.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
static string Dump(byte[] b) => BitConverter.ToString(b).Replace('-', ' ');

switch (args.FirstOrDefault())
{
    case "list":
        foreach (var h in new HidEnumerator().Enumerate().OrderBy(h => h.Path))
            Console.WriteLine($"HID {h.VendorId:X4}:{h.ProductId:X4} page {h.UsagePage:X4}/{h.Usage:X2} in {h.InputReportLength} out {h.OutputReportLength} " +
                              $"feat {h.FeatureReportLength} mfr \"{h.Manufacturer}\" {(h.IsBluetooth ? "BT " : "")}" +
                              $"{(h.Virtual is { } v ? $"VIRTUAL(\"{v.Name}\" {v.InstanceId}) " : "")}{h.Path}");
        foreach (var b in BluetoothDevices.EnumerateLe())
            Console.WriteLine($"BLE {b.VendorId:X4}:{b.ProductId:X4} {b.Mac} \"{b.Name}\" connected={b.Connected} battery={b.BatteryPercent} " +
                              $"[{string.Join(", ", b.Category ?? [])}]");
        foreach (var c in BluetoothDevices.EnumerateClassic())
            Console.WriteLine($"BT  {c.VendorId:X4}:{c.ProductId:X4} {c.Mac} \"{c.Name}\" connected={c.Connected} battery={c.BatteryPercent} " +
                              $"[{string.Join(", ", c.Category ?? [])}] services: {string.Join(" ", c.Services ?? [])}");
        break;

    case "hid" or "listen":
    {
        var info = new HidEnumerator().Enumerate().FirstOrDefault(h => h.Path.Contains(args[1], StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"No HID collection matches \"{args[1]}\"");
        using var conn = HidConnection.TryOpen(info) ?? throw new InvalidOperationException("Can't open it (in use exclusively?)");
        conn.ReportReceived += r => Console.WriteLine($"recv {Dump(r)}");
        conn.Start(); // ONE persistent reader: a second pending read would swallow replies
        if (args[0] == "hid")
        {
            var request = Hex(args[2]);
            Console.WriteLine($"send {Dump(request)}");
            await conn.RequestAsync(request, _ => false, TimeSpan.FromSeconds(3)); // waits; replies print above
        }
        else await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 30));
        break;
    }

    case "bt":
    {
        using var link = await BluetoothSerial.ConnectAsync(args[1].Replace(":", ""), Guid.Parse(args[2]), TimeSpan.FromSeconds(8));
        var request = Hex(args[3]);
        Console.WriteLine($"send {Dump(request)}");
        await link.SendAsync(request);
        var buf = new byte[512];
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            int n = await link.ReceiveAsync(buf, until - DateTime.UtcNow);
            if (n <= 0) break;
            Console.WriteLine($"recv {Dump(buf[..n])}");
        }
        break;
    }

    case "watch":
    {
        string dir = args.Length > 1 ? args[1] : Path.Combine(@"D:\Vibe Projects\PeripheralHub", @"artifacts\plugins\Debug");
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
        using var hub = new DeviceHub(new PeripheralHubOptions { Log = m => Console.WriteLine("LOG " + m) }, [dir]);
        hub.Start();
        for (int t = 5; t <= seconds; t += 5)
        {
            await Task.Delay(5000);
            Console.WriteLine($"--- {t}s");
            foreach (var d in hub.Devices)
                Console.WriteLine($"{d.Id} \"{d.Name}\" {d.Connection} {d.State} {d.BatteryPercent}% {d.Charging} model={d.Model}" +
                    (d.Parts is null ? "" : " " + string.Join(" ", d.Parts.Select(p => $"[{p.Label} {p.Percent}%{(p.Charging ? " chg" : "")}{(p.Live ? "" : " old")}]"))) +
                    (d.Note is null ? "" : $" note: {d.Note}"));
        }
        break;
    }

    default:
        Console.WriteLine("Commands: list | hid <path-part> <hex> | listen <path-part> [s] | bt <mac> <service-guid> <hex> | watch [plugins-dir] [s]");
        break;
}
