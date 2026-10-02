using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Windows;

/// <summary>
/// Dedicated STA thread running a Win32 message loop. Required by low-level hooks (callbacks are
/// delivered through the installing thread's queue) and by clipboard ownership (owners must pump).
/// Work is marshaled in with <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class MessageLoopThread : IDisposable
{
    private const uint WmInvoke = 0x8000 + 1; // WM_APP + 1

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource<uint> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    private volatile bool _disposed;

    public MessageLoopThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _threadId = _started.Task.GetAwaiter().GetResult();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!PInvoke.PostThreadMessage(_threadId, WmInvoke, default, default))
        {
            completion.TrySetException(new InvalidOperationException("Message loop thread is not accepting work."));
        }

        return completion.Task;
    }

    public Task InvokeAsync(Action work) => InvokeAsync(() =>
    {
        work();
        return true;
    });

    private unsafe void Run()
    {
        MSG msg;
        // Force creation of the thread's message queue before publishing the thread id.
        PInvoke.PeekMessage(&msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _started.SetResult(PInvoke.GetCurrentThreadId());

        while (PInvoke.GetMessage(&msg, default, 0, 0) > 0)
        {
            if (msg.message == WmInvoke && msg.hwnd.IsNull)
            {
                DrainWork();
                continue;
            }

            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        DrainWork();
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var item))
        {
            item();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        _thread.Join(TimeSpan.FromSeconds(2));
        _threadId = 0;
    }
}
