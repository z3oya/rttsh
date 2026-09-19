using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>Minimal line editor over Console.ReadKey(intercept), used while
/// Console.TreatControlCAsInput is on: printable chars append, Backspace deletes, Enter commits,
/// Ctrl+C returns false. Ctrl+C therefore arrives as an ordinary input character - independent
/// of Console.CancelKeyPress dispatch, which never fires while a thread sits blocked in console
/// input (see Program's class doc). With a TerminalUi the echo lands on the pinned bottom input
/// row; without one, chars echo inline. Arrow keys and other control keys are ignored: sent
/// lines are typed or pasted, and paste delivers its chars one by one.</summary>
internal static class ConsoleInput
{
    /// <summary>Reads one line; false when Ctrl+C was pressed.</summary>
    public static bool TryReadLine(TerminalUi? ui, out string line)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

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
                default:
                    // >= ' ' skips control chars; DEL (0x7f) also ignored.
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
