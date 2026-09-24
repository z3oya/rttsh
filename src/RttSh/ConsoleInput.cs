using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>Minimal line editor, used while Console.TreatControlCAsInput is on: printable chars
/// insert at the caret, Backspace deletes before it and Delete at it, Left/Right/Home/End move
/// the caret, Enter commits, Ctrl+C returns false. Ctrl+C therefore arrives as an ordinary
/// input character - independent of Console.CancelKeyPress dispatch, which never fires while a
/// thread sits blocked in console input (see Program's class doc). With an IInputSurface (TUI
/// mode) the echo lands on the pinned bottom input row and the caret moves within the buffer;
/// without one, chars echo inline and the caret stays at the end (the plain echo cannot redraw
/// a mid-line edit, so all these keys are ignored). Up/Down recall the session history into
/// the box, caret landing at the end (readline-like). All other control keys are ignored: sent
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
        int caret = 0;   // insertion point within the buffer; Length = after the last char
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
                    if (caret > 0)
                    {
                        buffer.Remove(caret - 1, 1);
                        caret--;
                        if (ui is null) Console.Write("\b \b");
                        else ui.RedrawInput(buffer.ToString(), caret);   // the tail window may shift: full redraw
                    }
                    break;
                case ConsoleKey.Delete when ui is not null:
                    if (caret < buffer.Length)
                    {
                        buffer.Remove(caret, 1);
                        ui.RedrawInput(buffer.ToString(), caret);
                    }
                    break;
                case ConsoleKey.Home when ui is not null:
                    if (caret > 0)
                    {
                        caret = 0;
                        ui.RedrawInput(buffer.ToString(), caret);
                    }
                    break;
                case ConsoleKey.End when ui is not null:
                    if (caret < buffer.Length)
                    {
                        caret = buffer.Length;
                        ui.RedrawInput(buffer.ToString(), caret);
                    }
                    break;
                case ConsoleKey.LeftArrow when ui is not null:
                    if (caret > 0)
                        ui.RedrawInput(buffer.ToString(), --caret);
                    break;
                case ConsoleKey.RightArrow when ui is not null:
                    if (caret < buffer.Length)
                        ui.RedrawInput(buffer.ToString(), ++caret);
                    break;
                case ConsoleKey.UpArrow when ui is not null:
                    if (history.TryOlder(buffer.ToString(), out string older))
                    {
                        buffer.Clear().Append(older);
                        caret = buffer.Length;
                        ui.RedrawInput(older, caret);
                    }
                    break;
                case ConsoleKey.DownArrow when ui is not null:
                    if (history.TryNewer(out string newer))
                    {
                        buffer.Clear().Append(newer);
                        caret = buffer.Length;
                        ui.RedrawInput(newer, caret);
                    }
                    break;
                default:
                    // >= ' ' skips control chars; DEL (0x7f) also ignored. The editing keys
                    // land here too when ui is null (plain mode ignores them).
                    if (key.KeyChar is >= ' ' and not '\u007f')
                    {
                        bool atEnd = caret == buffer.Length;
                        if (atEnd) buffer.Append(key.KeyChar);
                        else buffer.Insert(caret, key.KeyChar);
                        caret++;
                        if (ui is null) Console.Write(key.KeyChar);
                        // Paste delivers one char per key event; echo in place while typing at
                        // the end and the buffer fits the row; any other edit redraws the row
                        // with the caret placed at its new spot.
                        else if (!atEnd || !ui.TryAppendInputChar(key.KeyChar))
                            ui.RedrawInput(buffer.ToString(), caret);
                    }
                    break;
            }
        }
    }
}
