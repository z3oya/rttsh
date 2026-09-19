using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>Minimal line editor over Console.ReadKey(intercept), used while
/// Console.TreatControlCAsInput is on: printable chars append (echoed), Backspace deletes,
/// Enter commits, Ctrl+C returns false. Ctrl+C therefore arrives as an ordinary input
/// character - independent of Console.CancelKeyPress dispatch, which never fires while a
/// thread sits blocked in console input (see Program's class doc). Redirected stdin keeps
/// Console.ReadLine instead (the caller decides). Arrow keys and other control keys are
/// ignored: sent lines are typed or pasted, and paste delivers its chars one by one.</summary>
internal static class ConsoleInput
{
    /// <summary>Reads one line; false when Ctrl+C was pressed.</summary>
    public static bool TryReadLine(out string line)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                line = "";
                return false;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();   // echo the line break
                    line = buffer.ToString();
                    return true;
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");   // erase the echoed char
                    }
                    break;
                default:
                    // >= ' ' skips control chars; DEL (0x7f) also ignored.
                    if (key.KeyChar is >= ' ' and not '\u007f')
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
    }
}
