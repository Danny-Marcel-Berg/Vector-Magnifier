using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace ZoomHost;

public enum VectorPacketKind : uint
{
    Text = 0,
    Icon = 1,
    Line = 2,
    RectOutline = 3
}

/// <summary>
/// Binary-compatible C++20 / C# shared memory packet describing a single vector draw call.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
public unsafe struct VectorTextPacket
{
    public const int MaxFontFamilyLength = 64;
    public const int MaxTextLength = 256;
    public const int MaxGlyphCount = 256;

    public uint ProcessId;
    public ulong Hwnd;
    public ulong FrameId;
    public uint PacketKind;

    /// <summary>Exact global subpixel X coordinate of the text baseline (or start X for lines/icons).</summary>
    public float ScreenX;

    /// <summary>Exact global subpixel Y coordinate of the text baseline (or start Y for lines/icons).</summary>
    public float ScreenY;

    public float BoundsLeft;
    public float BoundsTop;
    public float BoundsRight;
    public float BoundsBottom;

    public float ClipLeft;
    public float ClipTop;
    public float ClipRight;
    public float ClipBottom;

    public float FontEmSize;
    public uint TextColorRGBA;
    public uint BgColorRGBA;
    public int FontWeight;
    public int FontStyle;

    public fixed char FontFamily[MaxFontFamilyLength];
    public fixed char Text[MaxTextLength];

    public int GlyphCount;
    public fixed ushort GlyphIndices[MaxGlyphCount];
    public fixed float GlyphAdvances[MaxGlyphCount];

    public static void WriteFixedString(char* dest, int maxChars, ReadOnlySpan<char> value)
    {
        int copyLen = Math.Min(value.Length, maxChars - 1);
        for (int i = 0; i < copyLen; i++)
        {
            dest[i] = value[i];
        }
        dest[copyLen] = '\0';
    }

    public static string ReadFixedString(char* src, int maxChars)
    {
        int len = 0;
        while (len < maxChars && src[len] != '\0')
        {
            len++;
        }
        return new string(src, 0, len);
    }
}

/// <summary>
/// Lock-free MPMC ring buffer backed by <see cref="MemoryMappedFile"/> (<c>Local\VectorMagnifier_IPC</c>)
/// with capacity for 4096 <see cref="VectorTextPacket"/> entries and atomic Write/Read indices.
/// </summary>
public sealed unsafe class SharedMemoryRingBuffer : IDisposable
{
    public const string MapName = @"Local\VectorMagnifier_IPC";
    public const int Capacity = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct RingHeader
    {
        public long WriteIndex;
        private fixed byte _pad0[56];
        public long ReadIndex;
        public int IsMagnifierActive;
        public int ViewportLeft;
        public int ViewportTop;
        public int ViewportRight;
        public int ViewportBottom;
        private fixed byte _pad1a[4];
        public long LastHeartbeatTicks;
        private fixed byte _pad1b[24];
        public uint RingCapacity;
        public uint PacketSize;
        public fixed long Sequences[Capacity];
    }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _basePtr;
    private RingHeader* _header;
    private VectorTextPacket* _packets;
    private bool _disposed;

    public SharedMemoryRingBuffer(bool initializeHeader = true)
    {
        long headerSize = sizeof(RingHeader);
        long totalSize = headerSize + (long)Capacity * sizeof(VectorTextPacket);

        _mmf = MemoryMappedFile.CreateOrOpen(MapName, totalSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        _header = (RingHeader*)_basePtr;
        _packets = (VectorTextPacket*)(_basePtr + headerSize);

        if (initializeHeader)
        {
            _header->WriteIndex = 0;
            _header->ReadIndex = 0;
            _header->IsMagnifierActive = 0;
            _header->ViewportLeft = 0;
            _header->ViewportTop = 0;
            _header->ViewportRight = 0;
            _header->ViewportBottom = 0;
            _header->LastHeartbeatTicks = Environment.TickCount64;
            _header->RingCapacity = Capacity;
            _header->PacketSize = (uint)sizeof(VectorTextPacket);
            for (int i = 0; i < Capacity; i++)
            {
                _header->Sequences[i] = i;
            }
        }
    }

    public void UpdateMagnifierState(bool isActive, int left, int top, int right, int bottom)
    {
        if (_disposed || _header is null)
        {
            return;
        }

        Volatile.Write(ref _header->ViewportLeft, left);
        Volatile.Write(ref _header->ViewportTop, top);
        Volatile.Write(ref _header->ViewportRight, right);
        Volatile.Write(ref _header->ViewportBottom, bottom);
        Volatile.Write(ref _header->LastHeartbeatTicks, Environment.TickCount64);
        Volatile.Write(ref _header->IsMagnifierActive, isActive ? 1 : 0);
    }

    public bool TryWrite(in VectorTextPacket packet)
    {
        if (_disposed)
        {
            return false;
        }

        const int mask = Capacity - 1;
        while (true)
        {
            long pos = Volatile.Read(ref _header->WriteIndex);
            long seq = Volatile.Read(ref _header->Sequences[pos & mask]);
            long diff = seq - pos;

            if (diff == 0)
            {
                if (Interlocked.CompareExchange(ref _header->WriteIndex, pos + 1, pos) == pos)
                {
                    _packets[pos & mask] = packet;
                    Volatile.Write(ref _header->Sequences[pos & mask], pos + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                return false; // Ring buffer full
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    public bool TryRead(out VectorTextPacket packet)
    {
        if (_disposed)
        {
            packet = default;
            return false;
        }

        const int mask = Capacity - 1;
        while (true)
        {
            long pos = Volatile.Read(ref _header->ReadIndex);
            long seq = Volatile.Read(ref _header->Sequences[pos & mask]);
            long diff = seq - (pos + 1);

            if (diff == 0)
            {
                if (Interlocked.CompareExchange(ref _header->ReadIndex, pos + 1, pos) == pos)
                {
                    packet = _packets[pos & mask];
                    Volatile.Write(ref _header->Sequences[pos & mask], pos + Capacity);
                    return true;
                }
            }
            else if (diff < 0)
            {
                packet = default;
                return false; // Ring buffer empty
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_basePtr is not null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
            _header = null;
            _packets = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }
}

