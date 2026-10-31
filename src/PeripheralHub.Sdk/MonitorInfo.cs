namespace PeripheralHub;

/// <summary>A monitor setting the UI can change over DDC/CI.</summary>
public enum MonitorSetting { Brightness, Volume }

/// <summary>A monitor setting's current value, out of the highest value the monitor allows (usually 100).</summary>
public sealed record MonitorLevel(int Value, int Max)
{
    /// <summary>The value as 0-100, which is what the UI shows and sets.</summary>
    public int Percent => Max <= 0 ? 0 : (int)Math.Round(100.0 * Math.Clamp(Value, 0, Max) / Max);
}

/// <summary>What a monitor tile shows: its current mode and the settings it lets us change.</summary>
/// <param name="Width">Desktop resolution, in pixels.</param>
/// <param name="RefreshHz">Refresh rate, rounded (59.94 → 60).</param>
/// <param name="Brightness">Null when the monitor doesn't answer DDC/CI for it (or it hasn't been read yet).</param>
/// <param name="Volume">The monitor's speaker volume; null when it has none or doesn't say.</param>
/// <param name="Muted">Whether its speakers are muted (keeps the volume level); null when it doesn't say.</param>
public sealed record MonitorInfo(int Width, int Height, int RefreshHz, MonitorLevel? Brightness = null, MonitorLevel? Volume = null,
    bool? Muted = null);
