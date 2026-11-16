using System.Diagnostics;
using System.Globalization;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the terminal host is given.</summary>
/// <param name="Name">The container's name, for the terminal's accessible name.</param>
/// <param name="Background">The terminal's background, "#rrggbb": the WebView shows it before the page has drawn, so nothing flashes white.</param>
/// <param name="OnMessage">Each message from the page, read with <see cref="TerminalProtocol.Parse"/>; one it cannot read is dropped.</param>
/// <param name="OnReady">The page is open and fitted (after each load): the handle takes posts from now on.</param>
/// <param name="OnFailed">The WebView could not start (no runtime, no user data folder): the exception's message.</param>
/// <remarks>
/// The handlers are read when they are called, not when the WebView is made, so a page that hands new ones on every render costs
/// nothing; only <paramref name="Name"/> changes what the control is given.
/// </remarks>
internal sealed record TerminalViewProps(
    string Name, string Background, Action<TerminalMessage> OnMessage, Action<TerminalHandle> OnReady, Action<string> OnFailed);

/// <summary>
/// The terminal page (xterm.js, bundled in <c>Assets\Terminal</c>) in a WebView2. The WebView serves the bundled folder as
/// <c>https://terminal.berth.example/</c> with no other origin reachable. It starts once it is in the window and is closed, which
/// ends its browser processes, on unmount and also when it leaves the window while still mounted: the navigation cache keeps a page
/// that was left mounted without rendering it. A WebView closed that way is replaced by a new one, which starts (and says ready
/// with a new handle) when the page is shown again.
/// </summary>
internal sealed class TerminalView : Component<TerminalViewProps>
{
    public override Element Render()
    {
        // A new generation for each WebView: the one closed on leaving the window is never used again.
        var (generation, updateGeneration) = UseReducer(0);
        var handle = UseMemo(() => new TerminalHandle(), generation);
        handle.Props = Props;
        handle.Left = () => updateGeneration(g => g + 1);
        var name = Props.Name;
        var background = Props.Background;

        // Built once per name and generation: a page rendering ten times a second hands the control the same element.
        var element = UseMemo(
            () => WebView2()
                .Set(handle.Attach)
                .WebMessageReceived(handle.Received)
                .AutomationName("Terminal for " + name)
                .WithKey(generation.ToString(CultureInfo.InvariantCulture)),
            name,
            generation);

        UseEffect(() => handle.SetBackground(background), background, handle);
        UseEffect(() => handle.Close, handle);

        return element;
    }
}

/// <summary>The host's side of a live terminal page. All members are called on the UI thread.</summary>
internal sealed class TerminalHandle
{
    /// <summary>The bundled folder's origin. Under <c>.example</c>, a reserved top-level domain that no name server answers.</summary>
    public const string HostName = "terminal.berth.example";

    private static readonly Uri PageUri = new("https://" + HostName + "/index.html");

    private readonly Queue<string> _waiting = new();
    private readonly Stopwatch _sinceAttach = new();
    private WebView2? _control;
    private bool _ready;
    private bool _started;
    private bool _closed;

    /// <summary>The latest props: the handlers are read from here when they are called.</summary>
    internal TerminalViewProps? Props { get; set; }

    /// <summary>How long the WebView took from being made to its first page saying ready; null until then. For checks.</summary>
    public TimeSpan? ReadyAfter { get; private set; }

    /// <summary>How long the WebView took to start its browser (<c>EnsureCoreWebView2Async</c>); null until then. For checks.</summary>
    public TimeSpan? StartedAfter { get; private set; }

    /// <summary>Where the WebView keeps its profile; null until it has started. For checks.</summary>
    public string? UserDataFolder { get; private set; }

    /// <summary>How many times the bundled page started loading in this WebView: 1 unless it was reloaded. For checks.</summary>
    public int LoadCount { get; private set; }

    /// <summary>The page's messages: <paramref name="json"/> goes to the page now, or once it said ready. Nothing after close.</summary>
    public void Post(string json)
    {
        if (_closed)
            return;
        if (!_ready || _control?.CoreWebView2 is not { } core)
        {
            _waiting.Enqueue(json);
            return;
        }

        core.PostWebMessageAsString(json);
    }

    /// <summary>Moves the keyboard focus to the terminal: the XAML focus to the WebView, then the page's own to xterm.</summary>
    public void Focus()
    {
        if (_closed || _control is null)
            return;
        _control.Focus(FocusState.Programmatic);
        Post(TerminalProtocol.Focus());
    }

    /// <summary>Runs <paramref name="script"/> in the page and gives its result as JSON. For checks and the spike only.</summary>
    public async Task<string> EvalAsync(string script)
    {
        if (_closed || _control?.CoreWebView2 is not { } core)
            return "null";
        return await core.ExecuteScriptAsync(script);
    }

    /// <summary>Whether the WebView is drawn dark (its ActualTheme): follows Windows switching between light and dark while the app runs.</summary>
    public bool IsDark => _control?.ActualTheme == ElementTheme.Dark;

    /// <summary>Called with <see cref="IsDark"/> each time the WebView's ActualTheme changes, until close.</summary>
    public Action<bool>? ThemeChanged { get; set; }

    /// <summary>The window content the WebView is in; null before it is in a window or after close. For moving the focus out.</summary>
    internal XamlRoot? XamlRoot => _closed ? null : _control?.XamlRoot;

    /// <summary>Called once the WebView was closed because it left the window while mounted; the view then makes a new one.</summary>
    internal Action? Left { get; set; }

    /// <summary>
    /// The element's setter: runs on mount and on every update; only the first control is taken. The browser starts once the
    /// control is in the window (a page kept in the navigation cache is not).
    /// </summary>
    internal void Attach(WebView2 control)
    {
        if (_control is not null || _closed)
            return;
        _control = control;
        if (Props is { } props)
            SetBackground(props.Background);
        control.Loaded += OnLoaded;
        control.Unloaded += OnUnloaded;
        control.ActualThemeChanged += OnActualThemeChanged;
        if (control.IsLoaded)
            OnLoaded(control, null!);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _started || _control is not { } control)
            return;
        _started = true;
        _sinceAttach.Start();
        _ = StartAsync(control);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_closed)
            ThemeChanged?.Invoke(sender.ActualTheme == ElementTheme.Dark);
    }

    // Unloaded also comes when a control is only moved; it counts as leaving when the control is still out of the window after the
    // layout pass that moved it.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _control is not { } control)
            return;
        control.DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || control.IsLoaded)
                return;
            Close();
            Left?.Invoke();
        });
    }

    internal void Received(string json)
    {
        if (_closed || Props is not { } props || TerminalProtocol.Parse(json) is not { } message)
            return;

        if (message is TerminalMessage.Ready)
        {
            _ready = true;
            ReadyAfter ??= _sinceAttach.Elapsed;
            while (_ready && !_closed && _waiting.TryDequeue(out var waiting))
                _control?.CoreWebView2?.PostWebMessageAsString(waiting);
            props.OnReady(this);
        }

        props.OnMessage(message);
    }

    /// <summary>The colour the WebView shows behind the page, "#rrggbb"; anything else is ignored.</summary>
    internal void SetBackground(string background)
    {
        if (_control is null || _closed || background is not ['#', .. { Length: 6 } hex]
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return;
        _control.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0xff, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Unmount: closes the WebView, which ends its browser processes. Nothing is posted or reported afterwards.</summary>
    internal void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _ready = false;
        _waiting.Clear();
        if (_control is { } control)
        {
            _control = null;
            control.Loaded -= OnLoaded;
            control.Unloaded -= OnUnloaded;
            control.ActualThemeChanged -= OnActualThemeChanged;
            control.Close();
        }
    }

    private async Task StartAsync(WebView2 control)
    {
        try
        {
            await control.EnsureCoreWebView2Async();
            if (_closed)
                return;
            StartedAfter = _sinceAttach.Elapsed;
            var core = control.CoreWebView2;
            UserDataFolder = core.Environment.UserDataFolder;
            core.SetVirtualHostNameToFolderMapping(
                HostName, Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"), CoreWebView2HostResourceAccessKind.Deny);

            var settings = core.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            // Only the bundled page: any other navigation (a link in output, a script) is cancelled, and no window opens.
            core.NavigationStarting += (_, e) =>
            {
                if (!IsBundled(e.Uri))
                {
                    e.Cancel = true;
                }
                else
                {
                    _ready = false; // the page loads again: posts wait for its next ready
                    LoadCount++;
                }
            };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // A browser or renderer process that ends leaves an empty page: said like a failed start, so the tab can offer a new one.
            core.ProcessFailed += (_, e) =>
            {
                if (!_closed)
                    Props?.OnFailed("The terminal's browser process ended (" + e.ProcessFailedKind.ToString() + ").");
            };
            core.Navigate(PageUri.AbsoluteUri);
        }
        catch (Exception ex) when (!_closed)
        {
            Props?.OnFailed(ex.Message);
        }
        catch (Exception) when (_closed)
        {
            // Closed while it started: nothing to report.
        }
    }

    private static bool IsBundled(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);
}
