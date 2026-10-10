namespace TextFlow.Core.Input;

/// <summary>A focusable element as a WinEvent names it: window, object id and child id.</summary>
public readonly record struct FieldId(nint Window, int ObjectId, int ChildId);

/// <summary>
/// Fields of the foreground window that the security policy already allowed. A focus event that lands back on one
/// of them keeps capture on (the engine re-checks it in the background); any other field is evaluated first, with
/// capture off. Win11 Notepad bounces the focus between two of its windows after every paste, and the keys typed
/// during each re-evaluation used to be lost. Thread-safe: the hook thread asks, the engine thread answers.
/// </summary>
public sealed class KnownFields
{
    /// <summary>Plenty for one window; a page that keeps creating fields cannot grow the set without bound.</summary>
    public const int Capacity = 32;

    private readonly Lock _lock = new();
    private readonly List<FieldId> _allowed = [];
    private FieldId? _evaluating;
    private int _evaluatingVersion;

    public bool IsKnown(FieldId field)
    {
        lock (_lock)
        {
            return _allowed.Contains(field);
        }
    }

    /// <summary>The engine is about to judge <paramref name="field"/> for focus version <paramref name="version"/>.</summary>
    public void Evaluating(FieldId field, int version)
    {
        lock (_lock)
        {
            _evaluating = field;
            _evaluatingVersion = version;
        }
    }

    /// <summary>The policy allowed the field being evaluated for <paramref name="version"/>; a stale verdict is ignored.</summary>
    public void Allowed(int version)
    {
        lock (_lock)
        {
            if (_evaluating is not { } field || version != _evaluatingVersion || _allowed.Contains(field))
            {
                return;
            }

            if (_allowed.Count == Capacity)
            {
                _allowed.RemoveAt(0);
            }

            _allowed.Add(field);
        }
    }

    /// <summary>Another window is in front: its fields are judged afresh.</summary>
    public void ForegroundChanged() => Clear();

    /// <summary>A known field stopped being allowed (it became a password field): trust none of them.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _allowed.Clear();
            _evaluating = null;
        }
    }
}
