using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PathMemo.Platform.Native;

namespace PathMemo.Duplicates;

/// <summary>
/// Opening a candidate and reducing it to a hash, with the barriers that decide what is
/// never read at all (README sections 8.1, 8.4).
/// </summary>
/// <remarks>
/// <para>
/// Every file is opened exactly once per stage, with <c>FILE_SHARE_READ | WRITE | DELETE</c> -
/// without the last two, half of <c>AppData</c> is unreadable while the applications that
/// own it are running, and a duplicate finder that silently skips the busiest directories
/// on the machine is worse than useless.
/// </para>
/// <para>
/// The identity of the file comes off the same handle as the first hash. That is where
/// stage 1 of README section 8.1 actually happens: the snapshot format carries no file id
/// (README section 5.3), so "these two names are one file" cannot be decided from it - but
/// the handle needed to hash the bytes answers it for the cost of one more call, before a
/// single byte is read.
/// </para>
/// </remarks>
internal static class HashPipeline
{
    /// <summary>
    /// Opens a candidate for reading, or returns null with the Win32 error.
    /// </summary>
    /// <remarks>
    /// <c>FILE_FLAG_OPEN_NO_RECALL</c> is the second of the two barriers around cloud
    /// placeholders (threat T10): the attributes recorded by the scan are the first, and
    /// this one holds even when the file became a placeholder after the scan. Opening with
    /// it does not hydrate anything; a read would fail rather than pull the data down.
    /// </remarks>
    internal static SafeFileHandle? Open(string path, out int error)
    {
        var handle = FileApi.CreateFile(
            path,
            Kernel32Extra.GenericRead | Kernel32Extra.FileReadAttributes,
            FileApi.ShareAll,
            0,
            FileApi.OpenExisting,
            Kernel32Extra.FileFlagSequentialScan | Kernel32Extra.FileFlagOpenNoRecall,
            0);

        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return null;
    }

    /// <summary>The NTFS identity and link count of an open file.</summary>
    internal static unsafe bool TryIdentify(SafeFileHandle handle, out FileIdentity identity, out int links)
    {
        identity = default;
        links = 1;

        var info = default(FileIdInfo);
        if (FileApi.GetFileInformationByHandleEx(handle, Kernel32Extra.FileIdInfo, &info, (uint)sizeof(FileIdInfo)))
            identity = new FileIdentity(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);

        if (FileApi.TryReadStandardInfo(handle, out var standard)) links = (int)standard.NumberOfLinks;

        return identity.IsKnown;
    }

    /// <summary>
    /// Whether the data is somewhere else and reading it would fetch it over the network.
    /// </summary>
    /// <remarks>
    /// Checked on the handle, after the snapshot's own flag has already excluded the ones
    /// the scan saw. A "find duplicates" that hydrates a OneDrive folder pulls hundreds of
    /// gigabytes and fills the very disk it was asked to free (README section 8.4).
    /// </remarks>
    internal static bool IsElsewhere(SafeFileHandle handle)
    {
        if (!FileApi.TryReadTagInfo(handle, out var tag)) return false;

        const uint mask = Kernel32Extra.FileAttributeOffline
                        | Kernel32Extra.FileAttributeRecallOnOpen
                        | Kernel32Extra.FileAttributeRecallOnDataAccess;

        return (tag.FileAttributes & mask) != 0;
    }

    /// <summary>
    /// The cheap hash of README section 8.1, stage 2: the first and last 64 KB.
    /// </summary>
    /// <remarks>
    /// The ends rather than the beginning twice, because file formats that share a header
    /// are the common case - a hundred JPEGs from one camera, or two builds of the same
    /// installer - and the tail is where they differ. Small files are read whole: two
    /// reads of 64 KB on a 100 KB file is the whole file plus an overlap.
    /// </remarks>
    internal static string? Partial(SafeFileHandle handle, long size, DupeQuery query, out long read)
    {
        read = 0;
        var window = query.PartialHashBytes;

        if (size <= window * 2L) return Full(handle, size, query, out read);

        var buffer = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            using var digest = new Digest(query.Algorithm);

            if (!Chunk(handle, buffer.AsSpan(0, window), 0, digest, ref read)) return null;
            if (!Chunk(handle, buffer.AsSpan(0, window), size - window, digest, ref read)) return null;

            return digest.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The whole file, streamed through one pooled buffer (README section 17.3).</summary>
    internal static string? Full(SafeFileHandle handle, long size, DupeQuery query, out long read)
    {
        read = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(query.BufferBytes);
        try
        {
            using var digest = new Digest(query.Algorithm);
            long offset = 0;

            while (offset < size)
            {
                var wanted = (int)Math.Min(buffer.Length, size - offset);

                int got;
                try
                {
                    got = RandomAccess.Read(handle, buffer.AsSpan(0, wanted), offset);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }

                // A file that shrank under us is not the file the scan measured, and a
                // short hash of it would be a hash of something else.
                if (got == 0) return null;

                digest.Append(buffer.AsSpan(0, got));
                offset += got;
                read += got;
            }

            return digest.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool Chunk(
        SafeFileHandle handle, Span<byte> buffer, long offset, Digest digest, ref long read)
    {
        var filled = 0;

        while (filled < buffer.Length)
        {
            int got;
            try
            {
                got = RandomAccess.Read(handle, buffer[filled..], offset + filled);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (got == 0) return false;
            filled += got;
        }

        digest.Append(buffer);
        read += filled;
        return true;
    }
}

/// <summary>
/// One hash, whichever of the two the run asked for (README section 8.2).
/// </summary>
/// <remarks>
/// A thin wrapper rather than an interface: there are two implementations and neither is
/// ever chosen at runtime by anything but a flag, so the branch is cheaper than the
/// indirection (README section 17.2).
/// </remarks>
internal sealed class Digest : IDisposable
{
    private readonly XxHash128? _fast;
    private readonly IncrementalHash? _crypto;

    internal Digest(HashKind kind)
    {
        if (kind == HashKind.Sha256) _crypto = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        else _fast = new XxHash128();
    }

    internal void Append(ReadOnlySpan<byte> data)
    {
        _fast?.Append(data);
        _crypto?.AppendData(data);
    }

    internal string Finish() =>
        Convert.ToHexStringLower(_fast is not null ? _fast.GetCurrentHash() : _crypto!.GetHashAndReset());

    public void Dispose() => _crypto?.Dispose();
}
