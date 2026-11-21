#pragma once

#include <Windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace VectorMagnifier
{
    inline constexpr wchar_t kIpcMapName[] = L"Local\\VectorMagnifier_IPC";
    inline constexpr uint32_t kRingCapacity = 4096;
    inline constexpr int kMaxFontFamilyLength = 64;
    inline constexpr int kMaxTextLength = 256;
    inline constexpr int kMaxGlyphCount = 256;

    enum class VectorPacketKind : uint32_t
    {
        Text = 0,
        Icon = 1,
        Line = 2,
        RectOutline = 3
    };

#pragma pack(push, 1)
    struct VectorTextPacket
    {
        uint32_t ProcessId;
        uint64_t Hwnd;
        uint64_t FrameId;
        uint32_t PacketKind;

        float ScreenX;
        float ScreenY;

        float BoundsLeft;
        float BoundsTop;
        float BoundsRight;
        float BoundsBottom;

        float ClipLeft;
        float ClipTop;
        float ClipRight;
        float ClipBottom;

        float FontEmSize;
        uint32_t TextColorRGBA;
        uint32_t BgColorRGBA;
        int32_t FontWeight;
        int32_t FontStyle;

        char16_t FontFamily[kMaxFontFamilyLength];
        char16_t Text[kMaxTextLength];

        int32_t GlyphCount;
        uint16_t GlyphIndices[kMaxGlyphCount];
        float GlyphAdvances[kMaxGlyphCount];
    };
#pragma pack(pop)

    static_assert(sizeof(VectorTextPacket) == 2264, "VectorTextPacket size must match C# layout.");
    static_assert(offsetof(VectorTextPacket, FontFamily) == 84, "FontFamily offset must match C# layout.");
    static_assert(offsetof(VectorTextPacket, GlyphCount) == 724, "GlyphCount offset must match C# layout.");

    struct RingHeader
    {
        volatile LONGLONG WriteIndex;
        uint8_t _pad0[56];
        volatile LONGLONG ReadIndex;
        volatile int32_t IsMagnifierActive;
        volatile int32_t ViewportLeft;
        volatile int32_t ViewportTop;
        volatile int32_t ViewportRight;
        volatile int32_t ViewportBottom;
        uint8_t _pad1a[4];
        volatile LONGLONG LastHeartbeatTicks;
        uint8_t _pad1b[24];
        uint32_t RingCapacity;
        uint32_t PacketSize;
        volatile LONGLONG Sequences[kRingCapacity];
    };

    static_assert(sizeof(RingHeader) == 32904, "RingHeader size must match C# layout.");

    inline void WriteFixedUtf16(char16_t* dest, int maxChars, const wchar_t* src, int srcLen = -1)
    {
        if (!dest || maxChars <= 0)
            return;

        if (!src)
        {
            dest[0] = u'\0';
            return;
        }

        int len = srcLen;
        if (len < 0)
        {
            len = static_cast<int>(wcslen(src));
        }
        if (len >= maxChars)
        {
            len = maxChars - 1;
        }

        for (int i = 0; i < len; ++i)
        {
            dest[i] = static_cast<char16_t>(src[i]);
        }
        dest[len] = u'\0';
    }

    inline uint32_t ColorRefToRgba(COLORREF colorRef, uint8_t alpha = 0xFF)
    {
        const uint32_t r = static_cast<uint32_t>(GetRValue(colorRef));
        const uint32_t g = static_cast<uint32_t>(GetGValue(colorRef));
        const uint32_t b = static_cast<uint32_t>(GetBValue(colorRef));
        return (r << 24) | (g << 16) | (b << 8) | static_cast<uint32_t>(alpha);
    }

    inline uint32_t D2DColorToRgba(float r, float g, float b, float a)
    {
        auto clampByte = [](float v) -> uint32_t {
            if (v <= 0.0f) return 0u;
            if (v >= 1.0f) return 255u;
            return static_cast<uint32_t>(v * 255.0f + 0.5f);
        };
        return (clampByte(r) << 24) | (clampByte(g) << 16) | (clampByte(b) << 8) | clampByte(a);
    }

    class SharedRingWriter
    {
    public:
        bool Open()
        {
            if (_header != nullptr)
                return true;

            const SIZE_T totalSize = sizeof(RingHeader) + static_cast<SIZE_T>(kRingCapacity) * sizeof(VectorTextPacket);
            _hMap = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, kIpcMapName);
            if (!_hMap)
                return false;

            _base = static_cast<uint8_t*>(MapViewOfFile(_hMap, FILE_MAP_ALL_ACCESS, 0, 0, totalSize));
            if (!_base)
            {
                CloseHandle(_hMap);
                _hMap = nullptr;
                return false;
            }

            _header = reinterpret_cast<RingHeader*>(_base);
            _packets = reinterpret_cast<VectorTextPacket*>(_base + sizeof(RingHeader));

            if (_header->RingCapacity != kRingCapacity || _header->PacketSize != sizeof(VectorTextPacket))
            {
                Close();
                return false;
            }

            return true;
        }

        void Close()
        {
            if (_base)
            {
                UnmapViewOfFile(_base);
                _base = nullptr;
            }
            if (_hMap)
            {
                CloseHandle(_hMap);
                _hMap = nullptr;
            }
            _header = nullptr;
            _packets = nullptr;
        }

        bool TryGetMagnifierViewport(int32_t& left, int32_t& top, int32_t& right, int32_t& bottom)
        {
            if (!_header && !Open())
                return false;

            const int32_t active = _header->IsMagnifierActive;
            const LONGLONG lastBeat = _header->LastHeartbeatTicks;
            std::atomic_thread_fence(std::memory_order_acquire);
            if (active == 0)
                return false;

            const LONGLONG nowTicks = static_cast<LONGLONG>(GetTickCount64());
            if (lastBeat > 0 && (nowTicks - lastBeat) > 500)
                return false;

            left = _header->ViewportLeft;
            top = _header->ViewportTop;
            right = _header->ViewportRight;
            bottom = _header->ViewportBottom;
            return true;
        }

        bool TryWrite(const VectorTextPacket& packet)
        {
            if (!_header && !Open())
                return false;

            constexpr LONGLONG mask = static_cast<LONGLONG>(kRingCapacity) - 1;
            while (true)
            {
                const LONGLONG pos = _header->WriteIndex;
                std::atomic_thread_fence(std::memory_order_acquire);
                const LONGLONG seq = _header->Sequences[pos & mask];
                std::atomic_thread_fence(std::memory_order_acquire);

                const LONGLONG diff = seq - pos;
                if (diff == 0)
                {
                    if (InterlockedCompareExchange64(&_header->WriteIndex, pos + 1, pos) == pos)
                    {
                        _packets[pos & mask] = packet;
                        std::atomic_thread_fence(std::memory_order_release);
                        _header->Sequences[pos & mask] = pos + 1;
                        return true;
                    }
                }
                else if (diff < 0)
                {
                    return false; // Ring buffer full
                }
                else
                {
                    YieldProcessor();
                }
            }
        }

    private:
        HANDLE _hMap = nullptr;
        uint8_t* _base = nullptr;
        RingHeader* _header = nullptr;
        VectorTextPacket* _packets = nullptr;
    };
}

