using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search;

/// The engine: WebView2, the Chromium-based Edge runtime already on every
/// Windows 10 and 11 PC, so there is no second browser engine to ship. One
/// environment for the app; one profile for signed-in spaces and one for
/// each fresh space.
static class Web
{
    public static CoreWebView2Environment Env { get; private set; } = null!;
    public static string DataFolder => Path.Combine(Store.Folder, "Web");
    public static string ExtensionsFolder => Path.Combine(Store.Folder, "Extensions");

    static readonly Dictionary<string, CoreWebView2Profile> profiles = new();
    static readonly HashSet<string> synced = new();

    /// Chromium's switches for using less memory. Tabs of the same site share
    /// one page process rather than one each; lean mode also shrinks the
    /// engine's caches as on a low-memory device. Read once, at launch.
    public static string Arguments(bool lean) =>
        "--process-per-site" + (lean ? " --enable-low-end-device-mode" : "");

    public static async Task StartAsync(bool lean)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = Arguments(lean),
        };
        Env = await CoreWebView2Environment.CreateAsync(null, DataFolder, options);
    }

    public static string? RuntimeVersion
    {
        get { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; } }
    }

    public static CoreWebView2ControllerOptions Options(Tab tab)
    {
        var o = Env.CreateCoreWebView2ControllerOptions();
        o.ProfileName = tab.Space.Profile;
        o.IsInPrivateModeEnabled = tab.Private;
        return o;
    }

    public static IEnumerable<CoreWebView2Profile> Profiles => profiles.Values;

    /// Each profile the first time it is seen: the window's colours, and
    /// the extensions installed for every space.
    public static void Know(CoreWebView2Profile profile, bool isPrivate)
    {
        profile.PreferredColorScheme = Scheme;
        if (isPrivate) return;
        profiles[profile.ProfileName] = profile;
        if (synced.Add(profile.ProfileName)) _ = SyncExtensions(profile);
    }

    public static CoreWebView2PreferredColorScheme Scheme => Theme.Look switch
    {
        "dark" => CoreWebView2PreferredColorScheme.Dark,
        "light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    public static void Recolour()
    {
        foreach (var p in profiles.Values) p.PreferredColorScheme = Scheme;
    }

    // Extensions -----------------------------------------------------------

    public static Extensions Installed { get; } = Store.Load<Extensions>("extensions.json");

    static async Task SyncExtensions(CoreWebView2Profile profile)
    {
        try
        {
            var have = await profile.GetBrowserExtensionsAsync();
            foreach (var ext in Installed.Items.ToList())
            {
                if (!Directory.Exists(ext.Folder)) continue;
                if (have.Any(h => h.Name == ext.Name)) continue;
                try { await profile.AddBrowserExtensionAsync(ext.Folder); } catch { }
            }
        }
        catch { }
    }

    static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true });

    /// From a Chrome Web Store link or id: the store's package, unpacked,
    /// then added to every profile.
    public static async Task<string> InstallFromStore(string link)
    {
        var id = Crx.IdFrom(link) ?? throw new InvalidOperationException("That isn't a Chrome Web Store link.");
        var version = RuntimeVersion?.Split(' ')[0] ?? "130.0.0.0";
        var bytes = await http.GetByteArrayAsync(Crx.DownloadUrl(id, version));
        var folder = Path.Combine(ExtensionsFolder, id);
        await Task.Run(() => Crx.Unpack(bytes, folder));
        return await AddFolder(folder, id);
    }

    public static async Task<string> AddFolder(string folder, string? storeId = null)
    {
        string? name = null;
        foreach (var p in profiles.Values)
        {
            var ext = await p.AddBrowserExtensionAsync(folder);
            name ??= ext.Name;
        }
        if (name is null) throw new InvalidOperationException("Open a page first, then add the extension.");
        Installed.Items.RemoveAll(e => e.Folder == folder || e.Name == name);
        Installed.Items.Add(new InstalledExtension { Name = name, Folder = folder, StoreId = storeId });
        Store.Save("extensions.json", Installed);
        return name;
    }

    public static async Task Remove(string name)
    {
        foreach (var p in profiles.Values)
            foreach (var e in await p.GetBrowserExtensionsAsync())
                if (e.Name == name) await e.RemoveAsync();
        var gone = Installed.Items.Where(e => e.Name == name).ToList();
        Installed.Items.RemoveAll(e => e.Name == name);
        Store.Save("extensions.json", Installed);
        foreach (var g in gone)
            if (g.Folder.StartsWith(ExtensionsFolder)) try { Directory.Delete(g.Folder, true); } catch { }
    }

    public static async Task Enable(string name, bool on)
    {
        foreach (var p in profiles.Values)
            foreach (var e in await p.GetBrowserExtensionsAsync())
                if (e.Name == name) await e.EnableAsync(on);
    }
}

public sealed class InstalledExtension
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? StoreId { get; set; }
}

public sealed class Extensions
{
    public List<InstalledExtension> Items { get; set; } = new();
}
