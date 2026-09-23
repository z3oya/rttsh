namespace Toolbox.Tools.RttCli;

/// <summary>The line editor's view of the terminal. TerminalUi implements it in TUI mode;
/// tests substitute a recorder - the real console cannot be driven from unit tests.</summary>
internal interface IInputSurface
{
    void WriteLog(string text);
    bool TryAppendInputChar(char c);
    void RedrawInput(string buffer);
    void CommitInput(string line);
}
