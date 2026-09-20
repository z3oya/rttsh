namespace Toolbox.Tools.RttCli;

/// <summary>Submitted-line history for one monitor session, navigated with Up/Down in the
/// input editor. Lives only for the process lifetime (nothing is persisted) and is owned by
/// the input thread: ConsoleInput navigates, Program records, no other thread touches it.
/// Readline-like semantics: the first Up saves the typed buffer as the draft and recalls the
/// newest entry, Up clamps at the oldest, Down past the newest restores the draft (edits made
/// to a recalled line are not kept). Record ignores empty lines and consecutive duplicates,
/// and returns the navigation position to the live end.</summary>
internal sealed class InputHistory
{
    private readonly List<string> _entries = [];
    /// <summary>Navigation position; == <see cref="_entries"/>.Count means the live editing
    /// position (not on any entry).</summary>
    private int _index;
    private string _draft = "";

    /// <summary>Records a submitted line. Skipped: empty lines (Enter on an empty box sends
    /// nothing) and a line identical to the previous entry (rapid re-sends would fill the
    /// whole history with one command).</summary>
    public void Record(string line)
    {
        if (line.Length == 0) return;
        if (_entries.Count == 0 || _entries[^1] != line)
            _entries.Add(line);
        _index = _entries.Count;   // back to the live position
        _draft = "";
    }

    /// <summary>Up: step to the previous (older) entry. Leaving the live position saves
    /// <paramref name="current"/> as the draft. False - with the box left unchanged - when
    /// the history is empty or the oldest entry is already shown.</summary>
    public bool TryOlder(string current, out string recalled)
    {
        if (_entries.Count == 0)
        {
            recalled = "";
            return false;
        }
        if (_index >= _entries.Count)
        {
            _draft = current;
            _index = _entries.Count - 1;
        }
        else if (_index == 0)
        {
            recalled = "";
            return false;
        }
        else
        {
            _index--;
        }
        recalled = _entries[_index];
        return true;
    }

    /// <summary>Down: step to the next (newer) entry; past the newest restores the draft
    /// saved by TryOlder. False when already at the live position.</summary>
    public bool TryNewer(out string recalled)
    {
        if (_index >= _entries.Count)
        {
            recalled = "";
            return false;
        }
        _index++;
        recalled = _index < _entries.Count ? _entries[_index] : _draft;
        return true;
    }
}
