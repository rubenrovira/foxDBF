using System.IO.MemoryMappedFiles;

namespace CrossVault.FoxDbf;

/// <summary>
/// The OPT-IN, dependency-free memory-mapped READ source (Highlike Phase B-3, design Peak 1).
/// A read-only, seekable <see cref="Stream"/> whose reads are served from an OS-page-cache-backed
/// <see cref="MemoryMappedViewAccessor"/> (a <c>memcpy</c> from a mapped view) instead of a per-read
/// <see cref="FileStream"/> <c>read()</c> syscall. This is the single seam through which
/// <see cref="DbfTable"/> / <see cref="MemoFile"/> / <see cref="Index.IndexFile"/> obtain mapped
/// bytes — none of their decode logic changes; they merely see a different <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// SAFETY: the underlying file is opened <see cref="FileShare.ReadWrite"/> and mapped
/// <see cref="MemoryMappedFileAccess.Read"/>, so a concurrent <see cref="Write.DbfWriter"/> can still
/// append; the map is READ-ONLY and never blocks that writer. <see cref="Dispose"/> unmaps the view,
/// releases the section and closes the file handle, leaving the file fully deletable/rewritable.
/// </para>
/// <para>
/// <see cref="Length"/> reports the TRUE file length (not the page-rounded view capacity), and reads
/// are clamped to it, so every read is BYTE-IDENTICAL to a <see cref="FileStream"/> over the same
/// file (a page that straddles EOF yields a short read exactly as a <see cref="FileStream"/> would).
/// </para>
/// <para>
/// Construction NEVER throws for the caller: use <see cref="TryCreate(string)"/>, which returns
/// <see langword="null"/> on any mapping failure (empty/too-small file, sharing/permission fault) so
/// the caller degrades to the safe <see cref="FileStream"/> path.
/// </para>
/// </remarks>
internal sealed class MemoryMappedReadStream : Stream
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly FileStream _file; // owned: closed on Dispose so the file is fully released
    private readonly long _length;     // TRUE file length (view capacity is rounded up to a page)
    private long _position;
    private bool _disposed;

    private MemoryMappedReadStream(MemoryMappedFile mmf, MemoryMappedViewAccessor view, FileStream file, long length)
    {
        _mmf = mmf;
        _view = view;
        _file = file;
        _length = length;
    }

    /// <summary>
    /// Attempt to memory-map <paramref name="path"/> read-only. Returns <see langword="null"/> on ANY
    /// failure (empty file — a zero-length file cannot be mapped — sharing/permission fault, …) so the
    /// caller falls back to the classic <see cref="FileStream"/> path. Never throws.
    /// </summary>
    public static MemoryMappedReadStream? TryCreate(string path)
    {
        FileStream? file = null;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            // FileShare.ReadWrite: a concurrent writer (append) must not be blocked by the map.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = file.Length;
            if (length <= 0)
            {
                file.Dispose();
                return null; // a 0-byte file cannot be mapped — let the FileStream path report it.
            }

            // capacity 0 => map the whole current file; leaveOpen so WE own the FileStream lifetime.
            mmf = MemoryMappedFile.CreateFromFile(
                file, mapName: null, capacity: 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            return new MemoryMappedReadStream(mmf, view, file, length);
        }
        catch
        {
            view?.Dispose();
            mmf?.Dispose();
            file?.Dispose();
            return null; // any mapping failure degrades to FileStream (still correct).
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.Length == 0)
            return 0;

        int n = (int)Math.Min(buffer.Length, remaining);

        // memcpy from the mapped view (no read() syscall). Within [0, _length) <= view capacity.
        _view.SafeMemoryMappedViewHandle.ReadSpan((ulong)_position, buffer[..n]);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { /* read-only: nothing to flush */ }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                // Order matters: unmap the view, then the section, then close the file handle —
                // so the OS fully releases the file (deletable/rewritable) once we return.
                _view.Dispose();
                _mmf.Dispose();
                _file.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
