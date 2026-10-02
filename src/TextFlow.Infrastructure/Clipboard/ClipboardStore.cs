using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Clipboard;

/// <summary>In-memory copy of clipboard formats. Holds user content: never log, call <see cref="Wipe"/> when done.</summary>
public sealed class ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> items)
{
    public IReadOnlyList<(uint Format, byte[] Data)> Items { get; } = items;

    public bool IsEmpty => Items.Count == 0;

    public void Wipe()
    {
        foreach (var (_, data) in Items)
        {
            Array.Clear(data);
        }
    }
}

/// <summary>
/// Raw Win32 clipboard access. Must only be called on a <see cref="Windows.MessageLoopThread"/>
/// that owns <paramref name="owner"/>: clipboard owners receive sent messages and must pump.
/// </summary>
internal sealed unsafe class ClipboardStore(HWND owner)
{
    private const uint CfText = 1, CfBitmap = 2, CfMetafilePict = 3, CfOemText = 7, CfDib = 8, CfPalette = 9;
    private const uint CfUnicodeText = 13, CfEnhMetafile = 14, CfLocale = 16, CfDibV5 = 17;
    private const uint CfOwnerDisplay = 0x80, CfDspBitmap = 0x82, CfDspMetafilePict = 0x83, CfDspEnhMetafile = 0x8E;
    private const uint CfPrivateFirst = 0x200, CfGdiObjLast = 0x3FF;

    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 10;

    /// <summary>Snapshots bigger than this are not taken; the caller should avoid the clipboard strategy.</summary>
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private static readonly uint ExcludeFromMonitoring = PInvoke.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = PInvoke.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = PInvoke.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public static uint SequenceNumber => PInvoke.GetClipboardSequenceNumber();

    /// <returns>Null when the clipboard could not be opened or exceeds <see cref="MaxSnapshotBytes"/>.</returns>
    public ClipboardSnapshot? TakeSnapshot()
    {
        if (!Open())
        {
            return null;
        }

        try
        {
            var formats = EnumerateFormats();
            var items = new List<(uint, byte[])>();
            long total = 0;
            foreach (var format in formats.Where(f => ShouldPreserve(f, formats)))
            {
                var data = ReadGlobal(format);
                if (data is null)
                {
                    continue;
                }

                total += data.Length;
                if (total > MaxSnapshotBytes)
                {
                    items.ForEach(i => Array.Clear(i.Item2));
                    return null;
                }

                items.Add((format, data));
            }

            return new ClipboardSnapshot(items);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Places text marked as excluded from clipboard history, cloud sync and monitors.</summary>
    public bool SetPrivateText(string text)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            PInvoke.EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * sizeof(char)];
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
            return SetGlobal(CfUnicodeText, bytes) && MarkPrivate();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Restores a snapshot, also marked private so the restore does not create a duplicate history entry.</summary>
    public bool Restore(ClipboardSnapshot snapshot)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            PInvoke.EmptyClipboard();
            var ok = true;
            foreach (var (format, data) in snapshot.Items)
            {
                ok &= SetGlobal(format, data);
            }

            return MarkPrivate() && ok;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static bool MarkPrivate()
    {
        var zero = new byte[sizeof(uint)];
        return SetGlobal(ExcludeFromMonitoring, zero)
            & SetGlobal(CanIncludeInHistory, zero)
            & SetGlobal(CanUploadToCloud, zero);
    }

    private bool Open()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (PInvoke.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static HashSet<uint> EnumerateFormats()
    {
        var formats = new HashSet<uint>();
        for (var f = PInvoke.EnumClipboardFormats(0); f != 0; f = PInvoke.EnumClipboardFormats(f))
        {
            formats.Add(f);
        }

        return formats;
    }

    /// <summary>Skips GDI handles (not HGLOBAL) and formats Windows synthesizes from others.</summary>
    private static bool ShouldPreserve(uint format, HashSet<uint> present)
    {
        if (format is CfBitmap or CfMetafilePict or CfPalette or CfEnhMetafile
            or CfOwnerDisplay or CfDspBitmap or CfDspMetafilePict or CfDspEnhMetafile)
        {
            return false;
        }

        if (format is >= CfPrivateFirst and <= CfGdiObjLast)
        {
            return false;
        }

        if (format is CfText or CfOemText or CfLocale && present.Contains(CfUnicodeText))
        {
            return false;
        }

        return format != CfDib || !present.Contains(CfDibV5);
    }

    private static byte[]? ReadGlobal(uint format)
    {
        var handle = PInvoke.GetClipboardData(format);
        if (handle.IsNull)
        {
            return null;
        }

        var global = new HGLOBAL(handle.Value);
        var size = (long)PInvoke.GlobalSize(global);
        if (size <= 0 || size > MaxSnapshotBytes)
        {
            return null;
        }

        var source = PInvoke.GlobalLock(global);
        if (source is null)
        {
            return null;
        }

        try
        {
            return new ReadOnlySpan<byte>(source, (int)size).ToArray();
        }
        finally
        {
            PInvoke.GlobalUnlock(global);
        }
    }

    private static bool SetGlobal(uint format, byte[] data)
    {
        var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (global.IsNull)
        {
            return false;
        }

        var target = PInvoke.GlobalLock(global);
        if (target is null)
        {
            PInvoke.GlobalFree(global);
            return false;
        }

        data.CopyTo(new Span<byte>(target, data.Length));
        PInvoke.GlobalUnlock(global);

        if (PInvoke.SetClipboardData(format, new HANDLE(global.Value)).IsNull)
        {
            PInvoke.GlobalFree(global); // ownership only transfers on success
            return false;
        }

        return true;
    }
}
