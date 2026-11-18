using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Search.Core;

namespace Search;

sealed partial class BrowserWindow
{
    readonly Stack<(SavedTab Tab, Space Space, int Index)> closed = new();

    public Tab? Current => Space?.Selected;
    CoreWebView2? Core(Tab? t) => t?.View?.CoreWebView2;

    // Selecting, waking, sleeping -------------------------------------------

    public async Task Select(Tab t, bool force = false)
    {
        if (t.Space != Space) SwitchSpace(t.Space, wake: false);
        var prev = Space.Selected;
        if (prev == t && t.Awake && !force) { t.View!.Focus(); return; }
        if (prev != null && prev != t && prev.Awake)
        {
            prev.Seen = DateTime.Now;
            if (Settings.FloatOnLeave && prev.Audible) await Float(prev, "leave");
        }
        Space.Selected = t;
        t.Seen = DateTime.Now;
        ShowSelected();
        Render();
        SaveSoon();
        if (!t.Awake) await Wake(t);
        ShowSelected();
        t.View?.Focus();
    }

    /// Exactly one page shows: the space's selected tab. The rest are kept
    /// hidden and told to use as little memory as they can.
    void ShowSelected()
    {
        var sel = Space?.Selected;
        foreach (var t in Spaces.SelectMany(s => s.All))
        {
            if (t.View == null) continue;
            bool show = t == sel;
            bool was = t.View.Visibility == Visibility.Visible;
            t.View.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            if (was && !show) t.Left = DateTime.Now;
            if (t.View.CoreWebView2 is { } core)
            {
                core.MemoryUsageTargetLevel = show ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
                if (show && core.IsSuspended) core.Resume();
            }
        }
        empty.Visibility = sel == null ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task Wake(Tab t, bool navigate = true)
    {
        if (t.Awake) return;
        var g = Theme.GroundColor;
        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(g.R, g.G, g.B),
            Visibility = t == Space.Selected ? Visibility.Visible : Visibility.Hidden,
        };
        t.View = view;
        Pages.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(Web.Env, Web.Options(t));
            await Wire(t, view.CoreWebView2);
        }
        catch (Exception e)
        {
            Pages.Children.Remove(view);
            t.View = null;
            Toast("This page couldn't open: " + e.Message);
            return;
        }
        if (!navigate) return;
        var url = t.Pending ?? t.Url;
        t.Pending = null;
        if (!string.IsNullOrEmpty(url) && url != "about:blank") Load(view.CoreWebView2, url);
    }

    void Load(CoreWebView2 core, string url)
    {
        try { core.Navigate(url); }
        catch (ArgumentException) { core.Navigate(Engine.Url(url, SearchTemplate)!.AbsoluteUri); }
    }

    public void Sleep(Tab t)
    {
        if (t.View is not { } v) return;
        t.View = null;
        Pages.Children.Remove(v);
        v.Dispose();
        t.Loading = t.Audible = t.Reading = false;
        t.ShieldScript = t.HiddenScript = null;
        if (t.Pinned) t.Url = t.Home!;
        Render();
    }

    /// Memory, in three steps. A tab you leave is told to use as little as it
    /// can at once; after FreezeSeconds its page is frozen (no scripts, no
    /// timers, memory handed back where the engine can); after SleepMinutes
    /// the page is closed altogether and the tab is only an address again.
    /// A tab playing sound is left alone.
    void SleepIdle()
    {
        var now = DateTime.Now;
        foreach (var t in Spaces.SelectMany(s => s.All).ToList())
        {
            if (!t.Awake || t == Current || t.Audible) continue;
            var away = now - (t.Left > t.Seen ? t.Left : t.Seen);
            if (Settings.SleepMinutes > 0 && away > TimeSpan.FromMinutes(Settings.SleepMinutes)) { Sleep(t); continue; }
            if (Settings.FreezeSeconds > 0 && away > TimeSpan.FromSeconds(Settings.FreezeSeconds)) _ = Freeze(t);
        }
    }

    async Task Freeze(Tab t)
    {
        if (Core(t) is not { IsSuspended: false } core || t == Current || t.Audible) return;
        try { await core.TrySuspendAsync(); } catch { /* not hidden yet, or busy: next round */ }
    }

    /// Minimised: every page is frozen and this process gives back what it can.
    async Task FreezeAll()
    {
        foreach (var t in Spaces.SelectMany(s => s.All).ToList())
            if (!t.Audible && Core(t) is { IsSuspended: false } core)
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                if (t != Current) try { await core.TrySuspendAsync(); } catch { }
            }
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // The page's own events ---------------------------------------------------

    async Task Wire(Tab t, CoreWebView2 core)
    {
        Web.Know(core.Profile, t.Private);
        core.Settings.IsPasswordAutosaveEnabled = Settings.SavePasswords && !t.Private;
        core.Settings.IsGeneralAutofillEnabled = !t.Private;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = true;

        // The ad blocker: only requests to the listed hosts reach the handler.
        foreach (var d in Shield.Unwanted)
        {
            core.AddWebResourceRequestedFilter($"*://*.{d}/*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter($"*://{d}/*", CoreWebView2WebResourceContext.All);
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!Settings.ShieldOn || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var request)) return;
            Uri.TryCreate(t.Url, UriKind.Absolute, out var page);
            if (page != null && Paused(page.Host)) return;
            if (Shield.ShouldBlock(request, page))
                e.Response = Web.Env.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        if (Settings.ShieldOn) t.ShieldScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(Shield.CosmeticScript(Settings.ShieldPaused));
        t.HiddenScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(Hidden.Script());

        core.SourceChanged += (_, _) => { t.Url = core.Source; Render(); };
        core.DocumentTitleChanged += (_, _) =>
        {
            t.Title = core.DocumentTitle;
            if (!t.Private) History.Retitle(core.Source, t.Title);
            Render();
        };
        core.NavigationStarting += (_, e) =>
        {
            t.Loading = true;
            if (!e.IsRedirected) t.Reading = false;
            Render();
        };
        core.NavigationCompleted += (_, e) =>
        {
            t.Loading = false;
            if (e.IsSuccess && !t.Private) { History.Record(core.Source, core.DocumentTitle, DateTime.Now); historyDirty = true; SaveSoon(); }
            Render();
        };
        core.FaviconChanged += async (_, _) => { t.Icon = await IconOf(core); Render(); };
        core.NewWindowRequested += (_, e) => OpenRequested(t, e);
        // Never dispose a page from inside its own event.
        core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => Close(t));
        core.WebMessageReceived += (_, e) => Message(t, e.WebMessageAsJson);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.IsDocumentPlayingAudioChanged += (_, _) => { t.Audible = core.IsDocumentPlayingAudio; Render(); };
        core.IsMutedChanged += (_, _) => { t.Muted = core.IsMuted; Render(); };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    Sleep(t);
                    if (t == Current) await Wake(t);
                });
            }
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                if (t == Current) Toast("The page stopped working. Reloading it.");
                try { core.Reload(); } catch { }
            }
        };
    }

    static async Task<BitmapImage?> IconOf(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null || stream.Length == 0) return null;
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// A link that wants a new window becomes a tab right after its own,
    /// keeping its tie to the page that opened it (sign-in windows need it).
    /// Ctrl held: in the background.
    async void OpenRequested(Tab from, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;
        bool background = Native.CtrlHeld;
        var t = new Tab(from.Space, e.Uri) { Private = from.Private };
        int at = from.Pinned ? 0 : from.Space.Tabs.IndexOf(from) + 1;
        from.Space.Tabs.Insert(Math.Clamp(at, 0, from.Space.Tabs.Count), t);
        try
        {
            await Wake(t, navigate: false);
            if (t.View?.CoreWebView2 is { } core) e.NewWindow = core;
        }
        finally { deferral.Complete(); }
        if (!background) await Select(t); else Render();
        SaveSoon();
    }

    void Message(Tab t, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            if (kind == "hide" && t.Host.Length > 0)
            {
                Hidden.Add(t.Host, new Veil
                {
                    Selector = doc.RootElement.GetProperty("selector").GetString() ?? "",
                    Label = doc.RootElement.GetProperty("label").GetString() ?? "",
                });
                Store.Save("hidden.json", Hidden);
                _ = RefreshHidden();
            }
            else if (kind == "hiding")
            {
                bool on = doc.RootElement.GetProperty("on").GetBoolean();
                if (on && t == Current) Toast("Click something to hide it on " + Address.WithoutWww(t.Host));
            }
        }
        catch { }
    }

    public async Task RefreshHidden()
    {
        var script = Hidden.Script();
        foreach (var t in Spaces.SelectMany(s => s.All))
        {
            if (Core(t) is not { } core) continue;
            if (t.HiddenScript != null) core.RemoveScriptToExecuteOnDocumentCreated(t.HiddenScript);
            t.HiddenScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        if (Core(Current) is { } here && Current!.Host.Length > 0) await here.ExecuteScriptAsync(Hidden.Apply(Current.Host));
    }

    public async Task RefreshShield()
    {
        var script = Shield.CosmeticScript(Settings.ShieldPaused);
        foreach (var t in Spaces.SelectMany(s => s.All))
        {
            if (Core(t) is not { } core) continue;
            if (t.ShieldScript != null) core.RemoveScriptToExecuteOnDocumentCreated(t.ShieldScript);
            t.ShieldScript = Settings.ShieldOn ? await core.AddScriptToExecuteOnDocumentCreatedAsync(script) : null;
        }
    }

    public bool Paused(string host) =>
        Settings.ShieldPaused.Contains(Hidden.Key(host)) || Settings.ShieldPaused.Contains(host);

    // Going places --------------------------------------------------------------

    public string SearchTemplate => Engine.Template(Settings.Engine, Settings.CustomEngine);

    /// What was typed, as an address or as a search.
    public string Resolve(string text)
    {
        var u = Address.File(text) ?? Address.Url(text);
        if (u != null) return Address.Reachable(u).AbsoluteUri;
        return Engine.Url(text, SearchTemplate)?.AbsoluteUri ?? "about:blank";
    }

    public void Go(string text, bool newTab)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var url = Resolve(text);
        if (newTab || Current == null) Open(url);
        else Navigate(Current, url);
    }

    public void Navigate(Tab t, string url)
    {
        t.Url = url;
        t.Reading = false;
        if (Core(t) is { } core) Load(core, url);
        else { t.Pending = url; if (t == Current) _ = Wake(t); }
        Render();
    }

    public Tab Open(string url, bool select = true, bool priv = false, Space? space = null, int? index = null, bool external = false)
    {
        if (external)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Current!;
        }
        space ??= Space;
        var t = new Tab(space, url) { Private = priv };
        int at = index ?? space.Tabs.Count;
        space.Tabs.Insert(Math.Clamp(at, 0, space.Tabs.Count), t);
        if (select) _ = Select(t); else Render();
        SaveSoon();
        return t;
    }

    public void OpenPrivate() => OpenOmnibox(newTab: true, priv: true);

    /// Addresses handed over by Windows: a link clicked in another app.
    public void Arrived(string[] lines, bool activate = true)
    {
        if (!Started) return;
        Tab? last = null;
        foreach (var raw in lines)
        {
            var arg = raw.Trim().Trim('"');
            if (arg.Length == 0 || arg.StartsWith("--")) continue;
            string url = Uri.TryCreate(arg, UriKind.Absolute, out var u) && (u.IsFile || u.Scheme.StartsWith("http"))
                ? u.AbsoluteUri : Resolve(arg);
            last = Open(url, select: false);
        }
        if (last != null) _ = Select(last);
        if (activate) Native.BringForward(this);
    }

    public void Close(Tab t)
    {
        var space = t.Space;
        bool wasSelected = space.Selected == t;
        if (t.Pinned)
        {
            Sleep(t);
            if (wasSelected) SelectAfterClosing(space, null, -1);
            Render(); SaveSoon();
            return;
        }
        int index = space.Tabs.IndexOf(t);
        if (!t.Private && t.Url.Length > 0) closed.Push((t.Save(), space, index));
        space.Tabs.Remove(t);
        Sleep(t);
        if (wasSelected) SelectAfterClosing(space, t, index);
        Render(); SaveSoon();
    }

    void SelectAfterClosing(Space space, Tab? gone, int index)
    {
        Tab? next = null;
        if (space.Tabs.Count > 0 && index >= 0) next = space.Tabs[Math.Min(index, space.Tabs.Count - 1)];
        next ??= space.All.Where(x => x != gone && x != space.Selected).OrderByDescending(x => x.Awake).ThenByDescending(x => x.Seen).FirstOrDefault();
        space.Selected = null;
        if (next != null && space == Space) _ = Select(next);
        else if (next != null) space.Selected = next;
        ShowSelected();
    }

    public void Reopen()
    {
        while (closed.Count > 0)
        {
            var (saved, space, index) = closed.Pop();
            if (!Spaces.Contains(space)) continue;
            var t = Open(saved.Url, select: false, space: space, index: index);
            t.Title = saved.Title ?? ""; t.Name = saved.Name;
            _ = Select(t);
            return;
        }
    }

    public void TogglePin(Tab t)
    {
        var s = t.Space;
        if (t.Pinned) { s.Pins.Remove(t); t.Home = null; s.Tabs.Insert(0, t); }
        else if (!t.Private) { s.Tabs.Remove(t); t.Home = t.Url; s.Pins.Add(t); }
        Render(); SaveSoon();
    }

    public void Rename(Tab t)
    {
        var name = Prompt.Ask(this, "Name this tab", t.Name ?? t.Display);
        if (name == null) return;
        t.Name = string.IsNullOrWhiteSpace(name) || name == t.Title ? null : name.Trim();
        Render(); SaveSoon();
    }

    void Mute(Tab t)
    {
        if (Core(t) is { } core) core.IsMuted = !core.IsMuted;
    }

    public void Step(int by)
    {
        var all = Space.All.ToList();
        if (all.Count == 0) return;
        int i = Current == null ? -1 : all.IndexOf(Current);
        _ = Select(all[((i + by) % all.Count + all.Count) % all.Count]);
    }

    public void SelectNumber(int n)
    {
        var all = Space.All.ToList();
        if (all.Count == 0) return;
        _ = Select(n >= 9 ? all[^1] : all[Math.Min(n, all.Count) - 1]);
    }

    public void Back() { if (Core(Current) is { CanGoBack: true } c) c.GoBack(); }
    public void Forward() { if (Core(Current) is { CanGoForward: true } c) c.GoForward(); }

    public void Reload()
    {
        if (Current is not { } t) return;
        if (Core(t) is { } c) { t.Reading = false; c.Reload(); }
        else _ = Wake(t);
    }

    // Spaces -------------------------------------------------------------------

    public void SwitchSpace(Space s, bool wake = true)
    {
        if (s == Space) return;
        Space = s;
        if (s.Selected == null && s.All.Any()) s.Selected = s.All.First();
        ShowSelected();
        Render();
        SaveSoon();
        if (wake && s.Selected is { } sel) _ = Select(sel, force: true);
    }

    public void StepSpace(int by, bool offerNew = false)
    {
        int i = Spaces.IndexOf(Space) + by;
        if (i >= Spaces.Count && offerNew) { NewSpace(); return; }
        if (i < 0 || i >= Spaces.Count) return;
        SwitchSpace(Spaces[i]);
        Toast(Space.Name);
    }

    public void NewSpace()
    {
        var made = NewSpaceDialog.Ask(this);
        if (made == null) return;
        Spaces.Add(made);
        SwitchSpace(made);
        OpenOmnibox(newTab: true);
    }

    void DeleteSpace(Space s)
    {
        if (Spaces.Count < 2) return;
        var n = s.All.Count();
        if (n > 0 && MessageBox.Show(this, $"Delete “{s.Name}” and close its {n} tab{(n == 1 ? "" : "s")}?", "Search",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var t in s.All.ToList()) Sleep(t);
        int i = Spaces.IndexOf(s);
        Spaces.Remove(s);
        if (Space == s) { Space = null!; SwitchSpace(Spaces[Math.Max(0, i - 1)]); }
        Render(); SaveSoon();
    }

    void MoveToSpace(Tab t, Space to)
    {
        var from = t.Space;
        bool wasSelected = from.Selected == t;
        from.Tabs.Remove(t);
        if (from.Pins.Remove(t)) t.Home = null;
        if (from.Profile != to.Profile) Sleep(t);   // its sign-ins belong to the other space
        t.Space = to;
        to.Tabs.Add(t);
        if (wasSelected) SelectAfterClosing(from, t, -1);
        ShowSelected(); Render(); SaveSoon();
        Toast("Moved to " + to.Name);
    }

    // The page's own tools ---------------------------------------------------------

    public async Task Float(Tab t, string mode)
    {
        if (Core(t) is not { } core) return;
        try
        {
            var call = JsonSerializer.Serialize(new { expression = Scripts.FloatCall(mode), userGesture = true, awaitPromise = true, returnByValue = true });
            var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", call);
            using var doc = JsonDocument.Parse(result);
            var value = doc.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.GetString() : null;
            if (mode == "toggle" && value == "none") Toast("No video on this page");
            else if (mode == "toggle" && value?.StartsWith("refused") == true) Toast("This video can't float");
        }
        catch { if (mode == "toggle") Toast("No video on this page"); }
    }

    public async Task ToggleReader()
    {
        if (Current is not { } t || Core(t) is not { } core) return;
        if (t.Reading) { t.Reading = false; core.Reload(); return; }
        var look = Theme.Look == "system" ? "system" : Theme.Look;
        var result = await core.ExecuteScriptAsync(Scripts.Reader(look));
        if (result.Contains("read")) t.Reading = true;
        else Toast("Nothing to read on this page");
    }

    public async Task ToggleHiding()
    {
        if (Core(Current) is not { } core || Current!.Host.Length == 0) return;
        await core.ExecuteScriptAsync(Scripts.Hider.Value);
        Current.View!.Focus();
    }

    public async Task Peek(string? selector)
    {
        if (Core(Current) is { } core) await core.ExecuteScriptAsync(Scripts.Peek(selector));
    }

    public async Task Restore(string host, string? selector)
    {
        if (selector == null) Hidden.Clear(host); else Hidden.Remove(host, selector);
        Store.Save("hidden.json", Hidden);
        await Peek(null);
        await RefreshHidden();
    }

    public void CopyCleanLink()
    {
        if (Current is not { } t) return;
        Clipboard.SetText(CleanLink.Clean(t.Url));
        Toast("Copied");
    }

    public void OpenDevTools() => Core(Current)?.OpenDevToolsWindow();

    public async Task ClearBrowsingData()
    {
        foreach (var p in Web.Profiles) await p.ClearBrowsingDataAsync();
        History.Visits.Clear();
        Store.Save("history.json", History);
    }
}
