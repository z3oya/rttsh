using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>Minimal line editor, used while Console.TreatControlCAsInput is on: printable chars
/// append, Backspace deletes, Enter commits, Ctrl+C returns false. Ctrl+C therefore arrives as
/// an ordinary input character - independent of Console.CancelKeyPress dispatch, which never
/// fires while a thread sits blocked in console input (see Program's class doc). With an
/// IInputSurface (TUI mode) the echo lands on the pinned bottom input row; without one, chars
/// echo inline. Up/Down recall the session history into the box (TUI mode only: the plain
/// inline echo cannot cleanly erase a wrapped line). All other control keys are ignored: sent
/// lines are typed or pasted, and paste delivers its chars one by one.</summary>
internal static class ConsoleInput
{
    /// <summary>Reads one line from the real console; false when Ctrl+C was pressed.</summary>
    public static bool TryReadLine(IInputSurface? ui, InputHistory history, out string line) =>
        TryReadLine(ui, history, static () => Console.ReadKey(intercept: true), out line);

    /// <summary>Reads one line from <paramref name="readKey"/> (a seam for tests); false when
    /// Ctrl+C was pressed.</summary>
    public static bool TryReadLine(IInputSurface? ui, InputHistory history, Func<ConsoleKeyInfo> readKey, out string line)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = readKey();

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                ui?.WriteLog("^C\r\n");
                line = "";
                return false;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    string text = buffer.ToString();
                    if (ui is null) Console.WriteLine();
                    else ui.CommitInput(text);
                    line = text;
                    return true;
                }
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        if (ui is null) Console.Write("\b \b");
                        else ui.RedrawInput(buffer.ToString());   // the tail window may shift: full redraw
                    }
                    break;
                case ConsoleKey.UpArrow when ui is not null:
                    if (history.TryOlder(buffer.ToString(), out string older))
                    {
                        buffer.Clear().Append(older);
                        ui.RedrawInput(older);
                    }
                    break;
                case ConsoleKey.DownArrow when ui is not null:
                    if (history.TryNewer(out string newer))
                    {
                        buffer.Clear().Append(newer);
                        ui.RedrawInput(newer);
                    }
                    break;
                default:
                    // >= ' ' skips control chars; DEL (0x7f) also ignored. Arrows land here too
                    // when ui is null (plain mode ignores them).
                    if (key.KeyChar is >= ' ' and not '\u007f')
                    {
                        buffer.Append(key.KeyChar);
                        if (ui is null) Console.Write(key.KeyChar);
                        // Paste delivers one char per key event; echo in place while the
                        // buffer fits the row, fall back to a full redraw once it does not.
                        else if (!ui.TryAppendInputChar(key.KeyChar))
                            ui.RedrawInput(buffer.ToString());
                    }
                    break;
            }
        }
    }
}
