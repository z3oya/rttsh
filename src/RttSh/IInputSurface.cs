namespace Toolbox.Tools.RttCli;

/// <summary>The line editor's view of the terminal. TerminalUi implements it in TUI mode;
/// tests substitute a recorder - the real console cannot be driven from unit tests.</summary>
internal interface IInputSurface
{
    void WriteLog(string text);
    bool TryAppendInputChar(char c);
    /// <summary>Redraws the input row and places the caret at <paramref name="caret"/>
    /// (an index into <paramref name="buffer"/>; its Length = after the last char).</summary>
    void RedrawInput(string buffer, int caret);
    void CommitInput(string line);
}
