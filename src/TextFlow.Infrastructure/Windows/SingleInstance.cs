using Windows.Win32;

namespace TextFlow.Infrastructure.Windows;

/// <summary>
/// One TextFlow per Windows session (two hooks would expand everything twice). A named mutex marks the
/// running instance; a later launch signals a named event so the running one shows its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const uint AllowAnyProcess = unchecked((uint)-1); // ASFW_ANY

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;

    /// <param name="name">Session-local name (<c>Local\</c> prefix is added).</param>
    public SingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _mutex = new Mutex(initiallyOwned: false, $@"Local\{name}.Instance", out var created);
        IsFirst = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        if (IsFirst)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>True when no other instance holds the name: this process should run.</summary>
    public bool IsFirst { get; }

    /// <summary>Raised on a thread-pool thread when another launch asked this instance to show itself.</summary>
    public event Action? Activated;

    /// <summary>Called by a later launch. It is the foreground process, so it lets the first one take the foreground.</summary>
    public void ActivateFirst()
    {
        PInvoke.AllowSetForegroundWindow(AllowAnyProcess);
        _activate.Set();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        _mutex.Dispose();
    }
}
