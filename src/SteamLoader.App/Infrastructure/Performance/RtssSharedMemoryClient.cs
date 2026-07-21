using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.Text;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed record RtssTelemetrySnapshot(
    int ProcessId,
    string ExecutableName,
    string GraphicsApi,
    double FramesPerSecond,
    double FrameTimeMs,
    double OnePercentLowFps);

internal sealed unsafe class RtssSharedMemoryClient
{
    private const string MappingName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353;
    private const uint Version205 = 0x00020005;
    private const uint Version213 = 0x0002000D;
    private const uint Version214 = 0x0002000E;
    private const uint Version216 = 0x00020010;
    private const uint Version220 = 0x00020014;
    private const int BusyOffset = 36;
    private const int OsdFrameOffset = 32;
    private const int OwnerOffset = 256;
    private const int ExtendedTextOffset = 512;
    private const int ExtendedTextLength = 4096;
    private const int BufferOffset = 4608;
    private const int ExtendedText2Offset = 266752;
    private const int ExtendedText2Length = 32768;
    private const int FrametimeBufferOffset = 924;
    private const int FrametimeBufferLength = 1024;
    private const int FrametimeBufferFramerateOffset = 5024;
    private const int StatisticsCountOffset = 300;
    private const int OnePercentLowOffset = 9172;
    private const uint TelemetryFreshnessMilliseconds = 2_000;
    private const double MaximumDisplayFramerate = 1_000;
    private const uint GraphSignature = 0x47523030;
    private const uint GraphFrametimeFlags = 4 | 16 | 128;
    private const string OwnerName = "ToolsForSteam";

    public bool TryReadForeground(out RtssTelemetrySnapshot telemetry)
    {
        telemetry = new RtssTelemetrySnapshot(0, string.Empty, string.Empty, 0, 0, 0);

        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                pointer += view.PointerOffset;
                if (!TryReadHeader(pointer, out var header))
                {
                    return false;
                }

                var preferredIndex = header.Version >= Version216 ? ReadUInt32(pointer, 64) : uint.MaxValue;
                var preferredPid = header.Version >= Version216 ? ReadUInt32(pointer, 68) : 0;
                if (header.Version >= Version216 && preferredPid == 0)
                {
                    return false;
                }
                if (preferredIndex < header.AppCount
                    && TryReadApp(pointer, header, preferredIndex, preferredPid, out telemetry))
                {
                    return true;
                }

                for (uint index = 0; index < header.AppCount; index++)
                {
                    if (TryReadApp(pointer, header, index, preferredPid, out telemetry))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public bool TryWriteOverlay(string text, bool includeFrametimeGraph)
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                pointer += view.PointerOffset;
                if (!TryReadHeader(pointer, out var header) || header.OsdEntrySize < 512)
                {
                    return false;
                }

                if (!TryAcquireWriteLock(pointer, header.Version))
                {
                    return false;
                }

                try
                {
                    var entry = FindOrClaimSlot(pointer, header);
                    if (entry == null)
                    {
                        return false;
                    }

                    WriteAscii(entry + OwnerOffset, 256, OwnerName);
                    var targetOffset = header.Version >= Version220 && header.OsdEntrySize >= ExtendedText2Offset + ExtendedText2Length
                        ? ExtendedText2Offset
                        : ExtendedTextOffset;
                    var targetLength = targetOffset == ExtendedText2Offset ? ExtendedText2Length : ExtendedTextLength;
                    WriteAscii(entry, 256, string.Empty);
                    if (header.OsdEntrySize >= ExtendedTextOffset + ExtendedTextLength)
                    {
                        WriteAscii(entry + ExtendedTextOffset, ExtendedTextLength, string.Empty);
                    }

                    if (header.OsdEntrySize >= ExtendedText2Offset + ExtendedText2Length)
                    {
                        WriteAscii(entry + ExtendedText2Offset, ExtendedText2Length, string.Empty);
                    }

                    WriteAscii(entry + targetOffset, targetLength, text);
                    if (header.OsdEntrySize >= BufferOffset + 36)
                    {
                        WriteFrametimeGraph(entry + BufferOffset, includeFrametimeGraph);
                    }

                    Interlocked.Increment(ref *(int*)(pointer + OsdFrameOffset));
                    return true;
                }
                finally
                {
                    ReleaseWriteLock(pointer, header.Version);
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public void ReleaseOverlay()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                pointer += view.PointerOffset;
                if (!TryReadHeader(pointer, out var header) || !TryAcquireWriteLock(pointer, header.Version))
                {
                    return;
                }

                try
                {
                    for (uint index = 1; index < header.OsdCount; index++)
                    {
                        var entry = pointer + header.OsdOffset + index * header.OsdEntrySize;
                        if (ReadAscii(entry + OwnerOffset, 256).Equals(OwnerName, StringComparison.Ordinal))
                        {
                            new Span<byte>(entry, checked((int)header.OsdEntrySize)).Clear();
                            Interlocked.Increment(ref *(int*)(pointer + OsdFrameOffset));
                            break;
                        }
                    }
                }
                finally
                {
                    ReleaseWriteLock(pointer, header.Version);
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static bool TryReadHeader(byte* pointer, out Header header)
    {
        header = default;
        if (ReadUInt32(pointer, 0) != Signature || (ReadUInt32(pointer, 4) >> 16) != 2)
        {
            return false;
        }

        header = new Header(
            ReadUInt32(pointer, 4),
            ReadUInt32(pointer, 8),
            ReadUInt32(pointer, 12),
            ReadUInt32(pointer, 16),
            ReadUInt32(pointer, 20),
            ReadUInt32(pointer, 24),
            ReadUInt32(pointer, 28));
        return header.AppEntrySize >= 316 && header.AppCount > 0 && header.OsdCount > 1;
    }

    private static bool TryReadApp(
        byte* pointer,
        Header header,
        uint index,
        uint preferredPid,
        out RtssTelemetrySnapshot telemetry)
    {
        telemetry = new RtssTelemetrySnapshot(0, string.Empty, string.Empty, 0, 0, 0);
        var entry = pointer + header.AppOffset + index * header.AppEntrySize;
        var processId = ReadUInt32(entry, 0);
        if (processId == 0 || (preferredPid != 0 && processId != preferredPid))
        {
            return false;
        }

        var time0 = ReadUInt32(entry, 268);
        var time1 = ReadUInt32(entry, 272);
        var frames = ReadUInt32(entry, 276);
        var currentTick = unchecked((uint)Environment.TickCount64);
        if (!IsTelemetryFresh(time1, currentTick))
        {
            return false;
        }

        var bufferedFramerateTenths = header.Version >= Version205
            && header.AppEntrySize >= FrametimeBufferFramerateOffset + sizeof(uint)
                ? ReadUInt32(entry, FrametimeBufferFramerateOffset)
                : 0;
        var fps = ComputeFramesPerSecond(bufferedFramerateTenths, time0, time1, frames);
        var lowRaw = header.Version >= Version213
            && header.AppEntrySize >= OnePercentLowOffset + sizeof(uint)
            ? ReadUInt32(entry, OnePercentLowOffset)
            : 0;
        var statisticsCount = header.AppEntrySize >= StatisticsCountOffset + sizeof(uint)
            ? ReadUInt32(entry, StatisticsCountOffset)
            : 0;
        var recordedLow = statisticsCount > 0 ? lowRaw / 10d : 0;
        var low = IsReasonableFramerate(recordedLow)
            ? recordedLow
            : ComputeRollingOnePercentLow(entry, header.AppEntrySize);
        if (!IsReasonableFramerate(low))
        {
            low = 0;
        }

        telemetry = new RtssTelemetrySnapshot(
            (int)processId,
            ReadAscii(entry + 4, 260),
            FormatGraphicsApi(ReadUInt32(entry, 264)),
            fps,
            fps > 0 ? 1000d / fps : 0,
            low);
        return true;
    }

    internal static bool IsTelemetryFresh(uint lastFrameTick, uint currentTick)
    {
        if (lastFrameTick == 0)
        {
            return false;
        }

        return unchecked(currentTick - lastFrameTick) <= TelemetryFreshnessMilliseconds;
    }

    internal static double ComputeFramesPerSecond(
        uint bufferedFramerateTenths,
        uint periodStartMilliseconds,
        uint periodEndMilliseconds,
        uint periodFrames)
    {
        var bufferedFramerate = bufferedFramerateTenths / 10d;
        if (IsReasonableFramerate(bufferedFramerate))
        {
            return bufferedFramerate;
        }

        var elapsedMilliseconds = unchecked(periodEndMilliseconds - periodStartMilliseconds);
        var periodFramerate = elapsedMilliseconds > 0
            ? 1000d * periodFrames / elapsedMilliseconds
            : 0;
        return IsReasonableFramerate(periodFramerate) ? periodFramerate : 0;
    }

    private static double ComputeRollingOnePercentLow(byte* entry, uint entrySize)
    {
        if (entrySize < FrametimeBufferOffset + FrametimeBufferLength * sizeof(uint))
        {
            return 0;
        }

        var samples = new List<uint>(FrametimeBufferLength);
        for (var index = 0; index < FrametimeBufferLength; index++)
        {
            var frametimeUs = ReadUInt32(entry, FrametimeBufferOffset + index * sizeof(uint));
            if (frametimeUs is > 0 and <= 1_000_000)
            {
                samples.Add(frametimeUs);
            }
        }

        return ComputeOnePercentLow(samples);
    }

    internal static double ComputeOnePercentLow(IEnumerable<uint> frameTimesMicroseconds)
    {
        var samples = frameTimesMicroseconds
            .Where(frameTime => frameTime is > 0 and <= 1_000_000)
            .ToList();
        if (samples.Count < 10)
        {
            return 0;
        }

        samples.Sort();
        var lowSampleCount = Math.Max(1, (int)Math.Ceiling(samples.Count * 0.01));
        var percentileFrametimeUs = samples[samples.Count - lowSampleCount];
        var low = 1_000_000d / percentileFrametimeUs;
        return IsReasonableFramerate(low) ? low : 0;
    }

    private static bool IsReasonableFramerate(double value) =>
        double.IsFinite(value) && value is >= 0.1 and <= MaximumDisplayFramerate;

    private static byte* FindOrClaimSlot(byte* pointer, Header header)
    {
        byte* empty = null;
        for (uint index = 1; index < header.OsdCount; index++)
        {
            var entry = pointer + header.OsdOffset + index * header.OsdEntrySize;
            var owner = ReadAscii(entry + OwnerOffset, 256);
            if (owner.Equals(OwnerName, StringComparison.Ordinal))
            {
                return entry;
            }

            if (empty == null && string.IsNullOrEmpty(owner))
            {
                empty = entry;
            }
        }

        return empty;
    }

    private static bool TryAcquireWriteLock(byte* pointer, uint version)
    {
        if (version < Version214)
        {
            return true;
        }

        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 100)
        {
            ref var busy = ref *(int*)(pointer + BusyOffset);
            if ((Interlocked.Or(ref busy, 1) & 1) == 0)
            {
                return true;
            }

            Thread.SpinWait(200);
        }

        return false;
    }

    private static void ReleaseWriteLock(byte* pointer, uint version)
    {
        if (version >= Version214)
        {
            Interlocked.And(ref *(int*)(pointer + BusyOffset), ~1);
        }
    }

    private static void WriteFrametimeGraph(byte* buffer, bool enabled)
    {
        new Span<byte>(buffer, 36).Clear();
        if (!enabled)
        {
            return;
        }

        *(uint*)(buffer + 0) = GraphSignature;
        *(uint*)(buffer + 4) = 36;
        *(int*)(buffer + 8) = -32;
        *(int*)(buffer + 12) = -3;
        *(int*)(buffer + 16) = 0;
        *(uint*)(buffer + 20) = GraphFrametimeFlags;
        *(float*)(buffer + 24) = 0;
        *(float*)(buffer + 28) = 50_000;
        *(uint*)(buffer + 32) = 0;
    }

    private static string FormatGraphicsApi(uint flags) => (flags & 0xFFFF) switch
    {
        1 => "OpenGL",
        2 => "DirectDraw",
        3 => "D3D8",
        4 => "D3D9",
        5 => "D3D9Ex",
        6 => "D3D10",
        7 => "D3D11",
        8 or 9 => "D3D12",
        10 => "Vulkan",
        _ => "3D"
    };

    private static uint ReadUInt32(byte* pointer, int offset) => *(uint*)(pointer + offset);

    private static string ReadAscii(byte* pointer, int length)
    {
        var span = new ReadOnlySpan<byte>(pointer, length);
        var terminator = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? span : span[..terminator]);
    }

    private static void WriteAscii(byte* pointer, int length, string value)
    {
        var span = new Span<byte>(pointer, length);
        span.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, length - 1)), span);
    }

    private readonly record struct Header(
        uint Version,
        uint AppEntrySize,
        uint AppOffset,
        uint AppCount,
        uint OsdEntrySize,
        uint OsdOffset,
        uint OsdCount);
}
