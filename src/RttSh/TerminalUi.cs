using System.Runtime.InteropServices;

namespace Toolbox.Tools.RttCli;

/// <summary>Chat-style console layout for monitor mode: RTT output scrolls in the top region
/// while a separator rule and a "&gt; " input line stay pinned to the bottom of the window;
/// Enter commits the input (echoed into the log), Ctrl+C exits. Built from plain VT sequences
/// (DECSTBM scroll region + save/restore cursor) - no TUI framework. One save slot holds the
/// log cursor; the input row is drawn with absolute positioning only, so RX bursts never move
/// or erase the typed line, and the caret stays hidden during log writes - RX bursts must
/// never visibly steal the input focus.
///
/// Thread safety: every method takes the <see cref="Program"/> console gate passed to
/// <see cref="TryCreate"/>. The gate is the same lock renderer/diagnostics already use and
/// C# locks are reentrant, so callers that already hold it stay safe while the input thread
/// (which holds nothing) serializes against them. Windows needs
/// ENABLE_VIRTUAL_TERMINAL_PROCESSING; if that fails (or stdout is redirected) TryCreate
/// returns null and Program keeps the plain inline mode.</summary>
internal sealed class TerminalUi : IDisposable, IInputSurface
{
    private const string Esc = "\x1b";
    private const string HideCursor = "\x1b[?25l";
    private const string ShowCursor = "\x1b[?25h";
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private readonly TextWriter _out;
    private readonly object _gate;
    private int _height;
    private int _width;
    private string _lastInput = "";
    private int _lastCaret;     // caret char index within _lastInput; Length = after the last char
    private int _windowStart;   // preferred left edge of the visible input window (a char index)
    private long _lastResizeCheckTicks;
    private bool _disposed;

    private static readonly char[] NewlineChars = ['\r', '\n'];
    /// <summary>The resize probe (a console IPC call) runs at most this often; a window drag
    /// lags by up to one interval, which is invisible next to the batch cadence.</summary>
    private const int ResizeCheckIntervalMs = 250;

    private TerminalUi(TextWriter output, object gate)
    {
        _out = output;
        _gate = gate;
    }

    /// <summary>Returns null when the layout is not possible (redirected stdout, no VT support).</summary>
    public static TerminalUi? TryCreate(object gate)
    {
        if (Console.IsOutputRedirected) return null;
        if (!EnableVt()) return null;
        return new TerminalUi(Console.Out, gate);
    }

    /// <summary>(Re)establishes margins, rule and input row; also the resize handler.</summary>
    public void Layout()
    {
        lock (_gate)
        {
            if (_disposed) return;
            (_height, _width) = (Console.WindowHeight, Console.WindowWidth);
            _lastResizeCheckTicks = Environment.TickCount64;
            int logBottom = _height - 2;
            if (logBottom < 1) return;   // degenerate window; retried on the next resize check
            _out.Write(BuildLayoutSequence());
            _out.Flush();
        }
    }

    /// <summary>Appends text to the scrolling log region. Newlines normalize to \r\n:
    /// a lone LF inside the region moves down without returning to column 1. Most batches
    /// contain no newline at all and skip the rebuild entirely.</summary>
    public void WriteLog(string text)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckResize();
            if (text.IndexOfAny(NewlineChars) >= 0)
                text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            // One console write per batch: fewer syscalls, and the sequence can never tear.
            _out.Write($"{HideCursor}{Esc}8{text}{Esc}7{InputRowSequence()}");
            _out.Flush();
        }
    }

    /// <summary>Pure half of <see cref="TryAppendInputChar"/>, so the fit rule is testable
    /// without a console: the char can echo in place only when the current window stays put
    /// (no slide) and the shown text plus the new char still fit the row's cell budget. The
    /// state updates on success remain with the caller.</summary>
    internal static bool FastAppendFits(int width, string buffer, int windowStart, char c)
    {
        (int start, _, int caretColumn) = ComputeWindow(width, buffer, buffer.Length, windowStart);
        return start == windowStart && caretColumn - 3 + DisplayWidth.Of(c) <= Math.Max(1, width - 3);
    }

    /// <summary>Fast append while typing or pasting: when the buffer still fits the row, echo
    /// the single char in place (the caret already sits at the end of the shown text) instead
    /// of redrawing the whole line per keystroke. Returns false when the tail window would
    /// shift (the row is full in cells, which with CJK input comes before the row is full in
    /// chars) and the caller must fall back to a full redraw. The editor only calls this while
    /// typing at the end - any other edit redraws.</summary>
    public bool TryAppendInputChar(char c)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (!FastAppendFits(_width, _lastInput, _windowStart, c)) return false;
            _lastInput += c;
            _lastCaret = _lastInput.Length;   // the caret rides the append: a log burst redraw must find it at the end
            _out.Write(c);
            _out.Flush();
            return true;
        }
    }

    /// <summary>Redraws the bottom input row with the current buffer and the caret at
    /// <paramref name="caret"/>. When the buffer does not fit the row, the visible window
    /// slides only as far as the caret requires and the caret is placed by cell width
    /// (CJK chars count double). Absolute positioning only - never touches the log-cursor
    /// save slot.</summary>
    public void RedrawInput(string buffer, int caret)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckResize();
            _lastInput = buffer;
            _lastCaret = caret;
            _out.Write(InputRowSequence());
            _out.Flush();
        }
    }

    /// <summary>Enter was pressed: echo the sent line into the log and clear the input row.
    /// The line comes from the editor, so it cannot contain newlines - no normalization.</summary>
    public void CommitInput(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckResize();
            _lastInput = "";
            _lastCaret = 0;
            _out.Write($"{HideCursor}{Esc}8> {line}\r\n{Esc}7{InputRowSequence()}");
            _out.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Restore the caret, reset the scroll region - or the user's shell inherits both.
            _out.Write($"{ShowCursor}{Esc}[r{Esc}[{_height};1H");
            _out.Flush();
        }
    }

    /// <summary>Re-runs the layout when the window size changed since the last draw; throttled
    /// because the size probe is a console IPC call on the RX hot path.</summary>
    private void CheckResize()
    {
        long now = Environment.TickCount64;
        if (now - _lastResizeCheckTicks < ResizeCheckIntervalMs) return;
        _lastResizeCheckTicks = now;
        if (Console.WindowHeight != _height || Console.WindowWidth != _width)
        {
            (_height, _width) = (Console.WindowHeight, Console.WindowWidth);
            if (_height - 2 >= 1)
                _out.Write(BuildLayoutSequence());
        }
    }

    /// <summary>Clear, set the scroll region, draw rule and input row, park the log cursor
    /// in the region (save slot) and the caret on the input row - in one atomic write.</summary>
    private string BuildLayoutSequence()
    {
        int logBottom = _height - 2;
        return $"{Esc}[r" +                                     // reset margins before re-seting them
               $"{Esc}[2J{Esc}[1;1H" +                          // clear screen, home
               $"{Esc}[1;{logBottom}r" +                        // scroll region: rows 1..H-2
               $"{Esc}[{_height - 1};1H" + new string('-', _width) +
               $"{Esc}[1;1H{Esc}7" +                            // save slot = log cursor at top
               InputRowSequence();
    }

    /// <summary>The input row redrawn from the fields: clear row, show the window around the
    /// caret, place the caret exactly. The caret index is clamped by <see cref="ComputeWindow"/>,
    /// so callers may hold a stale value (e.g. after CommitInput cleared the row).</summary>
    private string InputRowSequence()
    {
        (int start, string shown, int caretColumn) = ComputeWindow(_width, _lastInput, _lastCaret, _windowStart);
        _windowStart = start;
        return $"{Esc}[{_height};1H{Esc}[2K> {shown}{Esc}[{_height};{caretColumn}H{ShowCursor}";
    }

    /// <summary>Pure layout math for the input row, unit-testable without a console: pick the
    /// visible window of <paramref name="buffer"/> that contains <paramref name="caret"/>,
    /// returning its start, its text, and the caret's row column (1-based, including the
    /// "&gt; " prefix).
    ///
    /// Columns are cells, not chars - a CJK char is two cells (DisplayWidth), so a wide char
    /// never straddles the right edge and the caret lands between painted glyphs. The window
    /// slides the previous one (<paramref name="preferredStart"/>) only as far as the caret
    /// demands; when the buffer fits, or the window reaches the buffer end, it extends back
    /// left to fill the row. Stale inputs are clamped (caret into the buffer, a preferred
    /// start that a delete has invalidated). The loop terminates: the window at worst shrinks
    /// to the single char under the caret.</summary>
    internal static (int Start, string Shown, int CaretColumn) ComputeWindow(int width, string buffer, int caret, int preferredStart)
    {
        int usable = Math.Max(1, width - 3);
        caret = Math.Clamp(caret, 0, buffer.Length);
        int start = Math.Clamp(preferredStart, 0, caret);
        while (true)
        {
            int end = start, cells = 0;
            while (end < buffer.Length)
            {
                int w = DisplayWidth.Of(buffer[end]);
                if (cells + w > usable) break;
                cells += w;
                end++;
            }
            if (caret > end)
            {
                start++;                          // caret right of the window: slide and retry
                continue;
            }
            while (end == buffer.Length && start > 0 && cells + DisplayWidth.Of(buffer[start - 1]) <= usable)
            {
                start--;                          // window right-aligned: fill the slack to its left
                cells += DisplayWidth.Of(buffer[start]);
            }
            return (start, buffer[start..end], 3 + DisplayWidth.OfRange(buffer, start, caret));
        }
    }

    private static bool EnableVt()
    {
        if (!OperatingSystem.IsWindows()) return true;   // Unix terminals are VT-native
        try
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }

    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
