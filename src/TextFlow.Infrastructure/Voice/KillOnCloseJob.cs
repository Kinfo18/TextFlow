using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.JobObjects;

namespace TextFlow.Infrastructure.Voice;

/// <summary>
/// A Windows job object that kills its processes when its last handle closes. TextFlow keeps the handle for its whole
/// life, so a crash or a "Finalizar tarea" of TextFlow also ends the voice worker instead of leaving it holding VRAM.
/// </summary>
internal sealed unsafe class KillOnCloseJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public KillOnCloseJob()
    {
        _handle = PInvoke.CreateJobObject(null, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception();
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(
                (HANDLE)_handle.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            var error = new Win32Exception();
            Dispose();
            throw error;
        }
    }

    /// <exception cref="Win32Exception">Windows refused (the process already ended, for instance).</exception>
    public void Add(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (!PInvoke.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception();
        }
    }

    public void Dispose() => _handle.Dispose();
}
