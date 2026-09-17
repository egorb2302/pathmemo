using System.Runtime.InteropServices;
using PathMemo.Platform.Native;

namespace PathMemo.Platform;

internal enum MediumKind
{
    Unknown,
    SolidState,
    Rotational,
}

/// <summary>
/// Detects whether a volume sits on rotational media, because that inverts the right
/// parallelism. <c>MaxDegreeOfParallelism = CPU count</c> on an HDD thrashes the head and
/// runs 2-3x slower than a single thread (README section 4.4).
/// </summary>
internal static class StorageMedium
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;   // StorageDeviceSeekPenaltyProperty = 7
        public uint QueryType;    // PropertyStandardQuery = 0
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    internal static unsafe MediumKind Detect(string volumeLetter)
    {
        // The volume device path, e.g. \\.\C: - opened with no access rights, which is
        // enough for a property query and does not require elevation.
        var path = @"\\.\" + volumeLetter.TrimEnd('\\');

        var handle = Kernel32Extra.CreateFile(
            path, 0,
            Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite,
            0, Kernel32Extra.OpenExisting, 0, 0);

        if (handle == -1) return MediumKind.Unknown;

        try
        {
            var query = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
            var descriptor = default(DeviceSeekPenaltyDescriptor);

            var ok = Kernel32Extra.DeviceIoControl(
                handle, Kernel32Extra.IoctlStorageQueryProperty,
                &query, (uint)sizeof(StoragePropertyQuery),
                &descriptor, (uint)sizeof(DeviceSeekPenaltyDescriptor),
                out _, 0);

            if (!ok) return MediumKind.Unknown;
            return descriptor.IncursSeekPenalty ? MediumKind.Rotational : MediumKind.SolidState;
        }
        finally
        {
            Kernel32Extra.CloseHandle(handle);
        }
    }

    /// <summary>Worker count for the walk scanner, given the medium.</summary>
    internal static int RecommendedParallelism(MediumKind kind) => kind switch
    {
        MediumKind.Rotational => 2,
        MediumKind.SolidState => Math.Min(8, Environment.ProcessorCount),
        _ => 4,
    };
}
