using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the spike's Exec tab is given: the container's name.</summary>
internal sealed record ExecSpikeTabProps(string Name);

/// <summary>
/// Task 1's spike, replaced by the real Exec tab in Task 6: the terminal over a local echo (every <c>input</c> comes back as
/// <c>output</c>), and a line (<c>spike-result</c>) with what a self-test found: the echo read back from xterm's buffer, the time to
/// ready, the size, the WebView's user data folder, and the throughput of 10 MB of output under flow control. With
/// <c>BERTH_SPIKE_STREAM</c> set, output then keeps streaming (about 640 KB a second) for memory checks.
/// </summary>
internal sealed class ExecSpikeTab : Component<ExecSpikeTabProps>
{
    // Built once: a grid handed new definitions on every render is updated on every render.
    private static readonly GridSize[] Columns = [GridSize.Star()];
    private static readonly GridSize[] Rows = [GridSize.Auto, GridSize.Star()];

    public override Element Render()
    {
        // The theme at startup only: UseIsDarkTheme reads Application.RequestedTheme, which does not follow Windows switching between
        // light and dark while the app runs. The terminal follows the WebView's ActualTheme instead (TerminalHandle.ThemeChanged).
        var dark = UseIsDarkTheme();
        var (result, setResult) = UseState("starting");
        var spike = UseMemo(() => new EchoSpike(), Array.Empty<object>());
        spike.SetResult = setResult;

        var colors = UseMemo(() => Colors(dark), dark);
        UseEffect(() => spike.Stop, Array.Empty<object>());

        var terminal = Component<TerminalView, TerminalViewProps>(
            new(Props.Name, colors.Background, spike.OnMessage, spike.OnReady, spike.OnFailed));

        return Grid(
                Columns,
                Rows,
                TextBlock(result).AutomationId("spike-result").Grid(row: 0, column: 0).Margin(0, 0, 0, 8),
                terminal.Grid(row: 1, column: 0))
            .Margin(0, 0, 0, 16)
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>The terminal's colours from Presentation, over the theme's solid background and primary text.</summary>
    internal static TerminalColors Colors(bool dark)
    {
        var background = ColorOf("SolidBackgroundFillColorBaseBrush", dark, null);
        var foreground = ColorOf("TextFillColorPrimaryBrush", dark, background);
        return TerminalTheme.For(dark, highContrast: false, Hex(background), Hex(foreground));
    }

    /// <summary>A theme brush's colour, its transparency blended over <paramref name="under"/> when given.</summary>
    private static Windows.UI.Color ColorOf(string key, bool dark, Windows.UI.Color? under)
    {
        var color = ThemeRef.Resolve(key, dark) is SolidColorBrush brush ? brush.Color : Windows.UI.Color.FromArgb(0xff, 0x80, 0x80, 0x80);
        if (under is not { } back || color.A == 0xff)
            return color;
        var a = color.A / 255.0;
        return Windows.UI.Color.FromArgb(0xff, Mix(color.R, back.R), Mix(color.G, back.G), Mix(color.B, back.B));

        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * a) + (bottom * (1 - a)));
    }

    private static string Hex(Windows.UI.Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}");
}

/// <summary>The echo and the self-test. UI thread only.</summary>
internal sealed class EchoSpike
{
    private const int ChunkBytes = 32 * 1024;
    private const int TotalBytes = 10 * 1024 * 1024;
    private const int StreamChunkBytes = 64 * 1024;

    private static readonly byte[] Chunk = Lines(ChunkBytes, "throughput");
    private static readonly string ChunkMessage = TerminalProtocol.Output(Chunk);
    private static readonly byte[] StreamChunk = Lines(StreamChunkBytes, "stream");
    private static readonly string StreamMessage = TerminalProtocol.Output(StreamChunk);

    private readonly TerminalFlow _flow = new();
    private TerminalHandle? _handle;
    private TaskCompletionSource? _acked;
    private long _ackedTotal;
    private bool _stopped;
    private TerminalHandle? _tested;
    private string _size = "?";
    private string _found = "";

    public Action<string> SetResult { get; set; } = _ => { };

    public void OnReady(TerminalHandle handle)
    {
        _handle = handle;
        _flow.Reset();
        handle.Post(TerminalProtocol.Theme(ExecSpikeTab.Colors(handle.IsDark)));
        handle.ThemeChanged = dark =>
        {
            var colors = ExecSpikeTab.Colors(dark);
            handle.SetBackground(colors.Background);
            handle.Post(TerminalProtocol.Theme(colors));
            _found = "theme now " + (dark ? "dark" : "light") + " (" + colors.Background + " on " + colors.Foreground + "); " + _found;
            Show();
        };
        handle.Post(TerminalProtocol.Enabled(true));
        // Once per WebView: a page left and shown again has a new one.
        if (_tested != handle)
        {
            _tested = handle;
            _found = "";
            _acked?.TrySetCanceled(); // ends the test of the WebView before, whose acks will not come
            _acked = null;
            _ = SelfTestAsync(handle);
        }
    }

    public void OnFailed(string message) => SetResult("The terminal could not start: " + message);

    public void OnMessage(TerminalMessage message)
    {
        switch (message)
        {
            case TerminalMessage.Ready ready:
                _size = SizeText(ready.Size.Columns, ready.Size.Rows);
                Show();
                break;
            case TerminalMessage.Resize resize:
                _size = SizeText(resize.Size.Columns, resize.Size.Rows);
                Show();
                break;
            case TerminalMessage.Input input:
                _handle?.Post(TerminalProtocol.Output(Echo(input.Bytes.Span)));
                break;
            case TerminalMessage.Ack ack:
                _flow.Acked(ack.Bytes);
                _ackedTotal += ack.Bytes;
                var waiter = _acked;
                _acked = null;
                waiter?.TrySetResult();
                break;
            case TerminalMessage.Copy copy:
                _found = "copied " + copy.Text.Length.ToString(CultureInfo.InvariantCulture) + " characters; " + _found;
                Show();
                break;
            case TerminalMessage.Paste:
                _handle?.Post(TerminalProtocol.PasteText("pasted"));
                break;
            case TerminalMessage.Leave:
                // Task 6 moves the focus to the tab strip; the spike moves it to the previous element, which proves it can leave.
                var moved = _handle?.XamlRoot is { } root
                    && Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(
                        Microsoft.UI.Xaml.Input.FocusNavigationDirection.Previous,
                        new Microsoft.UI.Xaml.Input.FindNextElementOptions { SearchRoot = root.Content });
                _found = "leave (focus moved " + (moved ? "yes" : "no") + "); " + _found;
                Show();
                break;
        }
    }

    public void Stop()
    {
        _stopped = true;
        _acked?.TrySetCanceled();
    }

    private async Task SelfTestAsync(TerminalHandle handle)
    {
        try
        {
            // Posts and scripts travel separately: wait until the page has taken the "enabled" message, or the input is dropped.
            for (var i = 0; i < 40 && await handle.EvalAsync("window.berthTerminal.options.disableStdin") != "false"; i++)
                await Task.Delay(50);
            await handle.EvalAsync("window.berthTerminal.input('berth-echo-1\\r', true)");
            await Task.Delay(500);
            var line = ReadString(await handle.EvalAsync("window.berthTerminal.buffer.active.getLine(0).translateToString(true)"));
            var font = ReadString(await handle.EvalAsync(
                "(function(){var e=document.querySelector('.xterm-rows');return e?getComputedStyle(e).fontFamily+' '+getComputedStyle(e).fontSize:'';})()"));
            var theme = ReadString(await handle.EvalAsync(
                "(function(){var o=window.berthTerminal.options,t=o.theme||{};return 'bg '+t.background+' fg '+t.foreground+' red '+t.red+' contrast '+o.minimumContrastRatio+' fonts '+document.fonts.check('13px \"Cascadia Mono\"');})()"));
            _found = string.Create(CultureInfo.InvariantCulture,
                $"echo={line}; ready after {Ms(handle.ReadyAfter)} (browser started after {Ms(handle.StartedAfter)}); font={font}; theme={theme}; user data={handle.UserDataFolder}");
            Show();
            if (_stopped)
                return;

            // The page's own key rules, with synthetic keydowns on xterm's text area (no real keys reach a locked session): Ctrl+C with
            // a selection copies and clears it, Ctrl+V asks for the clipboard (the echo pastes "pasted"), F6 asks to leave.
            var selectionKept = await handle.EvalAsync(
                "(function(){var t=window.berthTerminal,a=t.textarea;function k(o){a.dispatchEvent(new KeyboardEvent('keydown',Object.assign({bubbles:true,cancelable:true},o)));}" +
                "t.select(0,0,12);k({key:'c',code:'KeyC',ctrlKey:true});var kept=t.hasSelection();k({key:'v',code:'KeyV',ctrlKey:true});k({key:'F6',code:'F6'});return kept;})()");
            await Task.Delay(300);
            _found = "selection kept after Ctrl+C: " + selectionKept + "; " + _found;
            Show();

            var (elapsed, stall) = await ThroughputAsync(handle);
            if (_stopped)
                return;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            _found += string.Create(CultureInfo.InvariantCulture,
                $"; 10 MB output acknowledged in {elapsed} ms ({TotalBytes / 1024.0 / 1024 / (elapsed / 1000.0):0.0} MB/s), longest UI stall {stall} ms, working set {process.WorkingSet64 / 1024 / 1024} MB");
            Show();

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BERTH_SPIKE_STREAM")))
                await StreamAsync(handle);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends 10 MB in 32 KB messages under flow control and waits for the last ack; also the longest gap between UI ticks.</summary>
    private async Task<(long Elapsed, long Stall)> ThroughputAsync(TerminalHandle handle)
    {
        var start = _ackedTotal;
        var watch = Stopwatch.StartNew();
        var stall = 0L;
        var last = watch.ElapsedMilliseconds;
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        timer.Tick += (_, _) =>
        {
            var now = watch.ElapsedMilliseconds;
            stall = Math.Max(stall, now - last);
            last = now;
        };
        timer.Start();
        try
        {
            for (var sent = 0; sent < TotalBytes && !_stopped; sent += Chunk.Length)
            {
                while (_flow.Paused && !_stopped)
                    await WaitForAck();
                handle.Post(ChunkMessage);
                _flow.Sent(Chunk.Length);
            }

            while (_ackedTotal - start < TotalBytes && !_stopped)
                await WaitForAck();
        }
        finally
        {
            timer.Stop();
        }

        return (watch.ElapsedMilliseconds, stall);
    }

    /// <summary>
    /// Output for as long as the tab is open: a 64 KB message every 100 ms while the flow allows. The result line is set on every
    /// tick, so this tab and the terminal host render ten times a second as well; every 5 s it says how many times the page loaded,
    /// when the page started and how many lines xterm holds, which shows the page was never reloaded nor its content lost.
    /// </summary>
    private async Task StreamAsync(TerminalHandle handle)
    {
        var baseline = _found;
        var probe = "";
        for (var tick = 0L; !_stopped && _tested == handle; tick++)
        {
            if (!_flow.Paused)
            {
                handle.Post(StreamMessage);
                _flow.Sent(StreamChunk.Length);
            }

            if (tick % 50 == 0)
            {
                probe = ReadString(await handle.EvalAsync(
                    "(function(){var b=window.berthTerminal.buffer.active;return 'origin='+Math.round(performance.timeOrigin)+' lines='+b.length+' last='+b.getLine(b.length-1).translateToString(true).substring(0,32);})()"));
            }

            _found = string.Create(CultureInfo.InvariantCulture,
                $"{baseline}; streaming tick {tick}, {_ackedTotal / 1024 / 1024} MB acknowledged, page loads {handle.LoadCount}, {probe}");
            Show();
            await Task.Delay(100);
        }
    }

    private Task WaitForAck()
    {
        _acked ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _acked.Task;
    }

    private void Show() => SetResult(_found.Length == 0 ? "size=" + _size : "size=" + _size + "; " + _found);

    /// <summary>What was typed, with Enter as a new line so a typed line stays on screen.</summary>
    private static byte[] Echo(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 4);
        foreach (var b in input)
        {
            output.Add(b);
            if (b == (byte)'\r')
                output.Add((byte)'\n');
        }

        return [.. output];
    }

    private static string SizeText(int columns, int rows) => string.Create(CultureInfo.InvariantCulture, $"{columns}x{rows}");

    private static string Ms(TimeSpan? time) =>
        time is { } t ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0} ms") : "?";

    /// <summary>ExecuteScriptAsync's result is JSON: a string comes back quoted.</summary>
    private static string ReadString(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : json;
    }

    /// <summary><paramref name="length"/> bytes of numbered lines, coloured every few lines so the output exercises escape sequences.</summary>
    private static byte[] Lines(int length, string label)
    {
        var text = new StringBuilder(length + 128);
        for (var i = 0; text.Length < length; i++)
        {
            var colour = 31 + (i % 6);
            text.Append(CultureInfo.InvariantCulture, $"\u001b[{colour}m{label}\u001b[0m line {i:00000} abcdefghijklmnopqrstuvwxyz 0123456789\r\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString(0, length));
    }
}
