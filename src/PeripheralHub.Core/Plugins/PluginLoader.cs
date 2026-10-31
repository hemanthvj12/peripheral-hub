using System.Reflection;
using System.Runtime.Loader;

namespace PeripheralHub.Plugins;

/// <summary>A plugin folder that was read successfully.</summary>
/// <param name="Plugin">The entry point; null when only the manifest was read (see <see cref="PluginLoader.ReadManifests"/>).</param>
public sealed record LoadedPlugin(PluginManifest Manifest, string Directory, IDevicePlugin? Plugin)
{
    /// <summary>Full path of a file the manifest names (e.g. an image), or null if it isn't inside the plugin folder.</summary>
    public string? PathOf(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Directory, relative));
        var root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>A plugin folder that couldn't be used, and why.</summary>
public sealed record PluginError(string Directory, string Message);

/// <summary>
/// Finds plugins: every subfolder of the plugin directories that has a <c>plugin.json</c>. The app ships its built-in
/// plugins in <c>plugins\</c> next to the exe; plugins added later go in <c>%APPDATA%\PeripheralHub\plugins</c>
/// (outside the install folder, which Setup wipes on reinstall).
/// When the same id is in several places, the highest version wins (a later directory wins a tie).
/// Reading manifests runs no plugin code; <see cref="LoadCode"/> is only called once a plugin's device is detected.
/// </summary>
public static class PluginLoader
{
    public static IReadOnlyList<string> DefaultDirectories { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "plugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralHub", "plugins"),
    ];

    /// <summary>Reads manifests only (no plugin code runs), picking one folder per plugin id.</summary>
    public static (List<LoadedPlugin> Plugins, List<PluginError> Errors) ReadManifests(IEnumerable<string> directories)
    {
        var errors = new List<PluginError>();
        var byId = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in directories)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifestPath = Path.Combine(dir, PluginManifest.FileName);
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var m = PluginManifest.Load(manifestPath);
                    if (m.SdkVersion > PluginApi.Version)
                        throw new InvalidDataException(
                            $"{m.Id} needs plugin API {m.SdkVersion}; this PeripheralHub has {PluginApi.Version}. Update PeripheralHub.");
                    if (byId.TryGetValue(m.Id, out var other) &&
                        Version.Parse(other.Manifest.Version) > Version.Parse(m.Version)) continue;
                    byId[m.Id] = new LoadedPlugin(m, dir, null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(new PluginError(dir, ex.Message));
                }
            }
        }
        return (byId.Values.OrderBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    /// <summary>Loads a plugin's assembly and creates its entry point (this is the first time any of its code runs).</summary>
    /// <exception cref="Exception">Anything that stops the plugin loading; the message says why.</exception>
    public static LoadedPlugin LoadCode(LoadedPlugin p)
    {
        try { return p with { Plugin = CreateEntryPoint(p) }; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    private static IDevicePlugin CreateEntryPoint(LoadedPlugin p)
    {
        var path = p.PathOf(p.Manifest.Assembly) ?? throw new InvalidDataException("assembly path leaves the plugin folder");
        if (!File.Exists(path)) throw new FileNotFoundException($"{p.Manifest.Assembly} is missing");
        var context = new PluginLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);
        var types = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDevicePlugin).IsAssignableFrom(t))
            .ToList();
        if (types.Count != 1)
            throw new InvalidDataException($"{p.Manifest.Assembly} must have exactly one public IDevicePlugin class (found {types.Count})");
        return (IDevicePlugin)Activator.CreateInstance(types[0])!;
    }

    /// <summary>
    /// Each plugin gets its own load context, so plugins can ship their own dependencies without clashing. Anything the
    /// host already has (the SDK, the framework) comes from the host, so plugin and host share one copy of the SDK types.
    /// </summary>
    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath))
    {
        private readonly string _dir = Path.GetDirectoryName(mainAssemblyPath)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))) return null;
            try { return Default.LoadFromAssemblyName(name); } catch (FileNotFoundException) { }
            var local = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
        }
    }
}
