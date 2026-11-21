#include "../Shared/SharedProtocol.h"
#include "third_party/minhook/include/MinHook.h"

#include <d2d1.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwrite.h>
#include <dwrite_3.h>
#include <cmath>
#include <mutex>
#include <unordered_map>
#include <vector>

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "d3d11.lib")

using namespace VectorMagnifier;

namespace
{
    HMODULE g_hModule = nullptr;
    HANDLE g_initThread = nullptr;
    SharedRingWriter g_ringWriter;
    std::atomic<bool> g_hooksInstalled{ false };

    thread_local HWND g_currentMsgHwnd = nullptr;
    thread_local int g_d2dHookDepth = 0;
    thread_local int g_gdiHookDepth = 0;

    struct DepthGuard
    {
        int& Counter;
        explicit DepthGuard(int& c) : Counter(c) { ++Counter; }
        ~DepthGuard() { --Counter; }
    };

    struct StagedMemDcPacket
    {
        HDC Hdc;
        VectorTextPacket Packet;
    };

    thread_local std::vector<StagedMemDcPacket> g_stagedMemDcPackets;

    std::mutex g_frameMutex;
    std::unordered_map<HWND, uint64_t> g_hwndFrameIds;

    uint64_t GetCurrentFrameId(HWND hwnd)
    {
        const uint64_t tickBucket = GetTickCount64() / 16;
        std::lock_guard<std::mutex> lock(g_frameMutex);
        auto& entry = g_hwndFrameIds[hwnd];
        if (tickBucket > entry)
        {
            entry = tickBucket;
        }
        return entry;
    }

    bool IntersectsViewport(const VectorTextPacket& pkt, int32_t vLeft, int32_t vTop, int32_t vRight, int32_t vBottom)
    {
        return pkt.BoundsRight >= static_cast<float>(vLeft) &&
               pkt.BoundsLeft <= static_cast<float>(vRight) &&
               pkt.BoundsBottom >= static_cast<float>(vTop) &&
               pkt.BoundsTop <= static_cast<float>(vBottom);
    }

    HWND ResolveFallbackThreadHwnd()
    {
        if (g_currentMsgHwnd && IsWindow(g_currentMsgHwnd))
        {
            return g_currentMsgHwnd;
        }

        GUITHREADINFO gui{};
        gui.cbSize = sizeof(GUITHREADINFO);
        if (GetGUIThreadInfo(GetCurrentThreadId(), &gui))
        {
            if (gui.hwndFocus && IsWindow(gui.hwndFocus))
                return gui.hwndFocus;
            if (gui.hwndActive && IsWindow(gui.hwndActive))
                return gui.hwndActive;
        }

        return GetForegroundWindow();
    }

    bool ResolveDcScreenOrigin(HDC hdc, HWND& outHwnd, POINT& outOrigin)
    {
        outHwnd = WindowFromDC(hdc);
        if (!outHwnd)
        {
            return false;
        }

        if (GetDCOrgEx(hdc, &outOrigin))
        {
            return true;
        }

        outOrigin = { 0, 0 };
        ClientToScreen(outHwnd, &outOrigin);
        return true;
    }

    void StageMemDcPacket(HDC hdc, const VectorTextPacket& pkt)
    {
        if (g_stagedMemDcPackets.size() >= 256)
        {
            g_stagedMemDcPackets.erase(g_stagedMemDcPackets.begin(), g_stagedMemDcPackets.begin() + 64);
        }
        g_stagedMemDcPackets.push_back({ hdc, pkt });
    }

    // -------------------------------------------------------------------------
    // GDI Hooks: ExtTextOutW, PolyTextOutW, BitBlt, DrawIconEx
    // -------------------------------------------------------------------------
    using ExtTextOutW_t = BOOL(WINAPI*)(
        HDC hdc,
        int x,
        int y,
        UINT options,
        const RECT* lprect,
        LPCWSTR lpString,
        UINT c,
        const INT* lpDx);

    using PolyTextOutW_t = BOOL(WINAPI*)(
        HDC hdc,
        const POLYTEXTW* pptxt,
        int cStrings);

    using BitBlt_t = BOOL(WINAPI*)(
        HDC hdc,
        int x,
        int y,
        int cx,
        int cy,
        HDC hdcSrc,
        int x1,
        int y1,
        DWORD rop);

    using GdiAlphaBlend_t = BOOL(WINAPI*)(
        HDC hdcDest,
        int xoriginDest,
        int yoriginDest,
        int wDest,
        int hDest,
        HDC hdcSrc,
        int xoriginSrc,
        int yoriginSrc,
        int wSrc,
        int hSrc,
        BLENDFUNCTION ftn);

    using DrawIconEx_t = BOOL(WINAPI*)(
        HDC hdc,
        int xLeft,
        int yTop,
        HICON hIcon,
        int cxWidth,
        int cyWidth,
        UINT istepIfAniCur,
        HBRUSH hbrFlickerFreeDraw,
        UINT diFlags);

    using LineTo_t = BOOL(WINAPI*)(HDC hdc, int x, int y);
    using Rectangle_t = BOOL(WINAPI*)(HDC hdc, int left, int top, int right, int bottom);

    ExtTextOutW_t g_origExtTextOutW = nullptr;
    ExtTextOutW_t g_origExtTextOutW_Full = nullptr;
    PolyTextOutW_t g_origPolyTextOutW = nullptr;
    PolyTextOutW_t g_origPolyTextOutW_Full = nullptr;
    BitBlt_t g_origBitBlt = nullptr;
    BitBlt_t g_origBitBlt_Full = nullptr;
    GdiAlphaBlend_t g_origGdiAlphaBlend = nullptr;
    GdiAlphaBlend_t g_origGdiAlphaBlend_Full = nullptr;
    DrawIconEx_t g_origDrawIconEx = nullptr;
    LineTo_t g_origLineTo = nullptr;
    Rectangle_t g_origRectangle = nullptr;

    BOOL HandleExtTextOutW(
        ExtTextOutW_t origFn,
        HDC hdc,
        int x,
        int y,
        UINT options,
        const RECT* lprect,
        LPCWSTR lpString,
        UINT c,
        const INT* lpDx)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (g_gdiHookDepth > 0 || !hdc || !lpString || c == 0 ||
            !g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            return origFn(hdc, x, y, options, lprect, lpString, c, lpDx);
        }

        DepthGuard guard(g_gdiHookDepth);

        HWND hwnd = nullptr;
        POINT dcOrigin{ 0, 0 };
        const bool isWindowDc = ResolveDcScreenOrigin(hdc, hwnd, dcOrigin);
        const bool isMemDc = !isWindowDc && (GetObjectType(hdc) == OBJ_MEMDC);
        if (!isWindowDc && !isMemDc)
        {
            return origFn(hdc, x, y, options, lprect, lpString, c, lpDx);
        }

        if (!hwnd)
        {
            hwnd = ResolveFallbackThreadHwnd();
        }

        const UINT count = (c < static_cast<UINT>(kMaxTextLength))
            ? c
            : static_cast<UINT>(kMaxTextLength - 1);

        LOGFONTW lf{};
        HFONT hFont = static_cast<HFONT>(GetCurrentObject(hdc, OBJ_FONT));
        if (hFont)
        {
            GetObjectW(hFont, sizeof(LOGFONTW), &lf);
        }

        TEXTMETRICW tm{};
        GetTextMetricsW(hdc, &tm);

        const int dxStep = (options & 0x2000u) ? 2 : 1; // ETO_PDY stores alternating (dx, dy) pairs

        SIZE textExtent{};
        if ((options & ETO_GLYPH_INDEX) == 0)
        {
            GetTextExtentPoint32W(hdc, lpString, static_cast<int>(count), &textExtent);
        }
        else if (lpDx)
        {
            long sumDx = 0;
            for (UINT i = 0; i < count; ++i)
                sumDx += lpDx[i * dxStep];
            textExtent.cx = sumDx;
            textExtent.cy = tm.tmHeight;
        }
        else
        {
            textExtent.cx = static_cast<LONG>(count * tm.tmAveCharWidth);
            textExtent.cy = tm.tmHeight;
        }

        const UINT textAlign = GetTextAlign(hdc);
        int refX = x;
        int refY = y;
        if (textAlign & TA_UPDATECP)
        {
            POINT cp{};
            if (GetCurrentPositionEx(hdc, &cp))
            {
                refX = cp.x;
                refY = cp.y;
            }
        }

        int leftLogicalX = refX;
        if ((textAlign & (TA_RIGHT | TA_CENTER)) == TA_CENTER)
        {
            leftLogicalX = refX - (textExtent.cx / 2);
        }
        else if (textAlign & TA_RIGHT)
        {
            leftLogicalX = refX - textExtent.cx;
        }

        int baselineLogicalY = refY;
        int topLogicalY = refY;

        if ((textAlign & (TA_BASELINE | TA_BOTTOM)) == TA_BASELINE)
        {
            baselineLogicalY = refY;
            topLogicalY = refY - tm.tmAscent;
        }
        else if (textAlign & TA_BOTTOM)
        {
            baselineLogicalY = refY - tm.tmDescent;
            topLogicalY = refY - tm.tmHeight;
        }
        else // TA_TOP
        {
            baselineLogicalY = refY + tm.tmAscent;
            topLogicalY = refY;
        }

        POINT pts[3] = {
            { leftLogicalX, baselineLogicalY },
            { leftLogicalX, topLogicalY },
            { leftLogicalX + textExtent.cx, topLogicalY + textExtent.cy }
        };

        LPtoDP(hdc, pts, 3);

        const float screenBaselineX = static_cast<float>(pts[0].x + dcOrigin.x);
        const float screenBaselineY = static_cast<float>(pts[0].y + dcOrigin.y);

        float bLeft = static_cast<float>(pts[1].x + dcOrigin.x);
        float bTop = static_cast<float>(pts[1].y + dcOrigin.y);
        float bRight = static_cast<float>(pts[2].x + dcOrigin.x);
        float bBottom = static_cast<float>(pts[2].y + dcOrigin.y);

        float clipLeft = 0.0f, clipTop = 0.0f, clipRight = 0.0f, clipBottom = 0.0f;
        if (lprect && (options & ETO_CLIPPED))
        {
            POINT cPts[2] = {
                { lprect->left, lprect->top },
                { lprect->right, lprect->bottom }
            };
            LPtoDP(hdc, cPts, 2);
            clipLeft = static_cast<float>(cPts[0].x + dcOrigin.x);
            clipTop = static_cast<float>(cPts[0].y + dcOrigin.y);
            clipRight = static_cast<float>(cPts[1].x + dcOrigin.x);
            clipBottom = static_cast<float>(cPts[1].y + dcOrigin.y);
        }

        VectorTextPacket pkt{};
        pkt.ProcessId = GetCurrentProcessId();
        pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
        pkt.FrameId = GetCurrentFrameId(hwnd);
        pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Text);
        pkt.ScreenX = screenBaselineX;
        pkt.ScreenY = screenBaselineY;
        pkt.BoundsLeft = bLeft;
        pkt.BoundsTop = bTop;
        pkt.BoundsRight = bRight;
        pkt.BoundsBottom = bBottom;
        pkt.ClipLeft = clipLeft;
        pkt.ClipTop = clipTop;
        pkt.ClipRight = clipRight;
        pkt.ClipBottom = clipBottom;

        POINT fontSizePts[2] = { { 0, 0 }, { 0, lf.lfHeight < 0 ? -lf.lfHeight : (tm.tmHeight - tm.tmInternalLeading) } };
        LPtoDP(hdc, fontSizePts, 2);
        const float fontPx = static_cast<float>(std::abs(fontSizePts[1].y - fontSizePts[0].y));
        pkt.FontEmSize = fontPx > 1.0f ? fontPx : 14.0f;

        const COLORREF fg = GetTextColor(hdc);
        const COLORREF bg = GetBkColor(hdc);
        pkt.TextColorRGBA = ColorRefToRgba(fg != CLR_INVALID ? fg : RGB(0, 0, 0));
        pkt.BgColorRGBA = ColorRefToRgba(bg != CLR_INVALID ? bg : GetSysColor(COLOR_WINDOW));

        pkt.FontWeight = lf.lfWeight > 0 ? lf.lfWeight : 400;
        pkt.FontStyle = lf.lfItalic ? 2 : 0;

        wchar_t dcFaceName[kMaxFontFamilyLength]{};
        const wchar_t* chosenFace = lf.lfFaceName;
        if (!chosenFace[0] || _wcsicmp(chosenFace, L"System") == 0)
        {
            if (GetTextFaceW(hdc, kMaxFontFamilyLength, dcFaceName) > 0 &&
                dcFaceName[0] && _wcsicmp(dcFaceName, L"System") != 0)
            {
                chosenFace = dcFaceName;
            }
            else
            {
                chosenFace = L"Segoe UI";
            }
        }
        WriteFixedUtf16(pkt.FontFamily, kMaxFontFamilyLength, chosenFace);

        if (options & ETO_GLYPH_INDEX)
        {
            const uint32_t glyphCount = count < static_cast<UINT>(kMaxGlyphCount)
                ? count
                : static_cast<UINT>(kMaxGlyphCount);
            pkt.GlyphCount = static_cast<int32_t>(glyphCount);
            for (uint32_t i = 0; i < glyphCount; ++i)
            {
                pkt.GlyphIndices[i] = static_cast<uint16_t>(lpString[i]);
                if (lpDx)
                {
                    pkt.GlyphAdvances[i] = static_cast<float>(lpDx[i * dxStep]);
                }
            }
        }
        else
        {
            WriteFixedUtf16(pkt.Text, kMaxTextLength, lpString, static_cast<int>(count));

            const uint32_t glyphCount = count < static_cast<UINT>(kMaxGlyphCount)
                ? count
                : static_cast<UINT>(kMaxGlyphCount);
            WORD glyphs[kMaxGlyphCount]{};
            if (GetGlyphIndicesW(hdc, lpString, static_cast<int>(glyphCount), glyphs, GGI_MARK_NONEXISTING_GLYPHS) != GDI_ERROR)
            {
                pkt.GlyphCount = static_cast<int32_t>(glyphCount);
                for (uint32_t i = 0; i < glyphCount; ++i)
                {
                    pkt.GlyphIndices[i] = glyphs[i];
                    if (lpDx)
                    {
                        pkt.GlyphAdvances[i] = static_cast<float>(lpDx[i * dxStep]);
                    }
                    else
                    {
                        INT widthPx = 0;
                        GetCharWidth32W(hdc, lpString[i], lpString[i], &widthPx);
                        pkt.GlyphAdvances[i] = static_cast<float>(widthPx);
                    }
                }
            }
        }

        if (isMemDc)
        {
            StageMemDcPacket(hdc, pkt);
            return origFn(hdc, x, y, options, lprect, lpString, c, lpDx);
        }

        if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
        {
            g_ringWriter.TryWrite(pkt);
        }

        return origFn(hdc, x, y, options, lprect, lpString, c, lpDx);
    }

    BOOL WINAPI Hooked_ExtTextOutW(
        HDC hdc,
        int x,
        int y,
        UINT options,
        const RECT* lprect,
        LPCWSTR lpString,
        UINT c,
        const INT* lpDx)
    {
        return HandleExtTextOutW(g_origExtTextOutW, hdc, x, y, options, lprect, lpString, c, lpDx);
    }

    BOOL WINAPI Hooked_ExtTextOutW_Full(
        HDC hdc,
        int x,
        int y,
        UINT options,
        const RECT* lprect,
        LPCWSTR lpString,
        UINT c,
        const INT* lpDx)
    {
        return HandleExtTextOutW(g_origExtTextOutW_Full, hdc, x, y, options, lprect, lpString, c, lpDx);
    }

    BOOL HandlePolyTextOutW(
        PolyTextOutW_t origFn,
        ExtTextOutW_t origExtFn,
        HDC hdc,
        const POLYTEXTW* pptxt,
        int cStrings)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (!hdc || !pptxt || cStrings <= 0 || !g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            return origFn(hdc, pptxt, cStrings);
        }

        BOOL allOk = TRUE;
        for (int i = 0; i < cStrings; ++i)
        {
            const POLYTEXTW& pt = pptxt[i];
            if (!HandleExtTextOutW(origExtFn, hdc, pt.x, pt.y, pt.uiFlags, &pt.rcl, pt.lpstr, pt.n, pt.pdx))
            {
                allOk = FALSE;
            }
        }
        return allOk;
    }

    BOOL WINAPI Hooked_PolyTextOutW(HDC hdc, const POLYTEXTW* pptxt, int cStrings)
    {
        return HandlePolyTextOutW(g_origPolyTextOutW, g_origExtTextOutW, hdc, pptxt, cStrings);
    }

    BOOL WINAPI Hooked_PolyTextOutW_Full(HDC hdc, const POLYTEXTW* pptxt, int cStrings)
    {
        return HandlePolyTextOutW(
            g_origPolyTextOutW_Full,
            g_origExtTextOutW_Full ? g_origExtTextOutW_Full : g_origExtTextOutW,
            hdc,
            pptxt,
            cStrings);
    }

    void FlushStagedMemDcPackets(HDC destHdc, int x, int y, HDC hdcSrc, int x1, int y1)
    {
        if (g_stagedMemDcPackets.empty() || !destHdc || !hdcSrc)
            return;

        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        HWND destHwnd = nullptr;
        POINT destOrigin{ 0, 0 };
        if (ResolveDcScreenOrigin(destHdc, destHwnd, destOrigin) &&
            g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            POINT ptDest{ x, y };
            LPtoDP(destHdc, &ptDest, 1);

            POINT ptSrc{ x1, y1 };
            LPtoDP(hdcSrc, &ptSrc, 1);

            const float offsetX = static_cast<float>(destOrigin.x + ptDest.x - ptSrc.x);
            const float offsetY = static_cast<float>(destOrigin.y + ptDest.y - ptSrc.y);
            const uint64_t frameId = GetCurrentFrameId(destHwnd);

            for (auto it = g_stagedMemDcPackets.begin(); it != g_stagedMemDcPackets.end();)
            {
                if (it->Hdc == hdcSrc)
                {
                    VectorTextPacket pkt = it->Packet;
                    pkt.Hwnd = reinterpret_cast<uint64_t>(destHwnd);
                    pkt.FrameId = frameId;
                    pkt.ScreenX += offsetX;
                    pkt.ScreenY += offsetY;
                    pkt.BoundsLeft += offsetX;
                    pkt.BoundsTop += offsetY;
                    pkt.BoundsRight += offsetX;
                    pkt.BoundsBottom += offsetY;
                    if (pkt.ClipRight > pkt.ClipLeft)
                    {
                        pkt.ClipLeft += offsetX;
                        pkt.ClipTop += offsetY;
                        pkt.ClipRight += offsetX;
                        pkt.ClipBottom += offsetY;
                    }

                    if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
                    {
                        g_ringWriter.TryWrite(pkt);
                    }
                    it = g_stagedMemDcPackets.erase(it);
                }
                else
                {
                    ++it;
                }
            }
        }
    }

    BOOL WINAPI Hooked_BitBlt(
        HDC hdc,
        int x,
        int y,
        int cx,
        int cy,
        HDC hdcSrc,
        int x1,
        int y1,
        DWORD rop)
    {
        const BOOL res = g_origBitBlt(hdc, x, y, cx, cy, hdcSrc, x1, y1, rop);
        FlushStagedMemDcPackets(hdc, x, y, hdcSrc, x1, y1);
        return res;
    }

    BOOL WINAPI Hooked_BitBlt_Full(
        HDC hdc,
        int x,
        int y,
        int cx,
        int cy,
        HDC hdcSrc,
        int x1,
        int y1,
        DWORD rop)
    {
        const BOOL res = g_origBitBlt_Full(hdc, x, y, cx, cy, hdcSrc, x1, y1, rop);
        FlushStagedMemDcPackets(hdc, x, y, hdcSrc, x1, y1);
        return res;
    }

    BOOL WINAPI Hooked_GdiAlphaBlend(
        HDC hdcDest,
        int xoriginDest,
        int yoriginDest,
        int wDest,
        int hDest,
        HDC hdcSrc,
        int xoriginSrc,
        int yoriginSrc,
        int wSrc,
        int hSrc,
        BLENDFUNCTION ftn)
    {
        const BOOL res = g_origGdiAlphaBlend(
            hdcDest, xoriginDest, yoriginDest, wDest, hDest,
            hdcSrc, xoriginSrc, yoriginSrc, wSrc, hSrc, ftn);
        FlushStagedMemDcPackets(hdcDest, xoriginDest, yoriginDest, hdcSrc, xoriginSrc, yoriginSrc);
        return res;
    }

    BOOL WINAPI Hooked_GdiAlphaBlend_Full(
        HDC hdcDest,
        int xoriginDest,
        int yoriginDest,
        int wDest,
        int hDest,
        HDC hdcSrc,
        int xoriginSrc,
        int yoriginSrc,
        int wSrc,
        int hSrc,
        BLENDFUNCTION ftn)
    {
        const BOOL res = g_origGdiAlphaBlend_Full(
            hdcDest, xoriginDest, yoriginDest, wDest, hDest,
            hdcSrc, xoriginSrc, yoriginSrc, wSrc, hSrc, ftn);
        FlushStagedMemDcPackets(hdcDest, xoriginDest, yoriginDest, hdcSrc, xoriginSrc, yoriginSrc);
        return res;
    }

    BOOL WINAPI Hooked_DrawIconEx(
        HDC hdc,
        int xLeft,
        int yTop,
        HICON hIcon,
        int cxWidth,
        int cyWidth,
        UINT istepIfAniCur,
        HBRUSH hbrFlickerFreeDraw,
        UINT diFlags)
    {
        const BOOL res = g_origDrawIconEx(hdc, xLeft, yTop, hIcon, cxWidth, cyWidth, istepIfAniCur, hbrFlickerFreeDraw, diFlags);

        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (hdc && hIcon && g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            HWND hwnd = nullptr;
            POINT dcOrigin{ 0, 0 };
            if (ResolveDcScreenOrigin(hdc, hwnd, dcOrigin))
            {
                const int w = cxWidth > 0 ? cxWidth : GetSystemMetrics(SM_CXICON);
                const int h = cyWidth > 0 ? cyWidth : GetSystemMetrics(SM_CYICON);
                POINT pts[2] = { { xLeft, yTop }, { xLeft + w, yTop + h } };
                LPtoDP(hdc, pts, 2);

                VectorTextPacket pkt{};
                pkt.ProcessId = GetCurrentProcessId();
                pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
                pkt.FrameId = GetCurrentFrameId(hwnd);
                pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Icon);
                pkt.ScreenX = static_cast<float>(pts[0].x + dcOrigin.x);
                pkt.ScreenY = static_cast<float>(pts[0].y + dcOrigin.y);
                pkt.BoundsLeft = pkt.ScreenX;
                pkt.BoundsTop = pkt.ScreenY;
                pkt.BoundsRight = static_cast<float>(pts[1].x + dcOrigin.x);
                pkt.BoundsBottom = static_cast<float>(pts[1].y + dcOrigin.y);
                pkt.FontEmSize = static_cast<float>(h);

                const uint64_t iconVal = reinterpret_cast<uint64_t>(hIcon);
                pkt.GlyphCount = 4;
                pkt.GlyphIndices[0] = static_cast<uint16_t>(iconVal & 0xFFFFu);
                pkt.GlyphIndices[1] = static_cast<uint16_t>((iconVal >> 16) & 0xFFFFu);
                pkt.GlyphIndices[2] = static_cast<uint16_t>((iconVal >> 32) & 0xFFFFu);
                pkt.GlyphIndices[3] = static_cast<uint16_t>((iconVal >> 48) & 0xFFFFu);

                if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
                {
                    g_ringWriter.TryWrite(pkt);
                }
            }
        }

        return res;
    }

    BOOL WINAPI Hooked_LineTo(HDC hdc, int x, int y)
    {
        POINT startPt{};
        const bool hasStart = hdc && GetCurrentPositionEx(hdc, &startPt);
        const BOOL res = g_origLineTo(hdc, x, y);

        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (hasStart && g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            HWND hwnd = nullptr;
            POINT dcOrigin{ 0, 0 };
            if (ResolveDcScreenOrigin(hdc, hwnd, dcOrigin))
            {
                POINT pts[2] = { startPt, { x, y } };
                LPtoDP(hdc, pts, 2);

                LOGPEN lp{};
                HPEN hPen = static_cast<HPEN>(GetCurrentObject(hdc, OBJ_PEN));
                if (hPen) GetObjectW(hPen, sizeof(LOGPEN), &lp);
                if (lp.lopnStyle != PS_NULL)
                {
                    const float x0 = static_cast<float>(pts[0].x + dcOrigin.x);
                    const float y0 = static_cast<float>(pts[0].y + dcOrigin.y);
                    const float x1 = static_cast<float>(pts[1].x + dcOrigin.x);
                    const float y1 = static_cast<float>(pts[1].y + dcOrigin.y);

                    VectorTextPacket pkt{};
                    pkt.ProcessId = GetCurrentProcessId();
                    pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
                    pkt.FrameId = GetCurrentFrameId(hwnd);
                    pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Line);
                    pkt.ScreenX = x0;
                    pkt.ScreenY = y0;
                    pkt.GlyphAdvances[0] = x1;
                    pkt.GlyphAdvances[1] = y1;
                    pkt.BoundsLeft = (std::min)(x0, x1) - 1.0f;
                    pkt.BoundsTop = (std::min)(y0, y1) - 1.0f;
                    pkt.BoundsRight = (std::max)(x0, x1) + 2.0f;
                    pkt.BoundsBottom = (std::max)(y0, y1) + 2.0f;
                    pkt.FontEmSize = lp.lopnWidth.x > 1 ? static_cast<float>(lp.lopnWidth.x) : 1.0f;
                    pkt.TextColorRGBA = ColorRefToRgba(lp.lopnColor);

                    if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
                    {
                        g_ringWriter.TryWrite(pkt);
                    }
                }
            }
        }
        return res;
    }

    BOOL WINAPI Hooked_Rectangle(HDC hdc, int left, int top, int right, int bottom)
    {
        const BOOL res = g_origRectangle(hdc, left, top, right, bottom);

        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (hdc && g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            HWND hwnd = nullptr;
            POINT dcOrigin{ 0, 0 };
            if (ResolveDcScreenOrigin(hdc, hwnd, dcOrigin))
            {
                POINT pts[2] = { { left, top }, { right, bottom } };
                LPtoDP(hdc, pts, 2);

                LOGPEN lp{};
                HPEN hPen = static_cast<HPEN>(GetCurrentObject(hdc, OBJ_PEN));
                if (hPen) GetObjectW(hPen, sizeof(LOGPEN), &lp);
                if (lp.lopnStyle != PS_NULL)
                {
                    VectorTextPacket pkt{};
                    pkt.ProcessId = GetCurrentProcessId();
                    pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
                    pkt.FrameId = GetCurrentFrameId(hwnd);
                    pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::RectOutline);
                    pkt.BoundsLeft = static_cast<float>(pts[0].x + dcOrigin.x);
                    pkt.BoundsTop = static_cast<float>(pts[0].y + dcOrigin.y);
                    pkt.BoundsRight = static_cast<float>(pts[1].x + dcOrigin.x);
                    pkt.BoundsBottom = static_cast<float>(pts[1].y + dcOrigin.y);
                    pkt.ScreenX = pkt.BoundsLeft;
                    pkt.ScreenY = pkt.BoundsTop;
                    pkt.FontEmSize = lp.lopnWidth.x > 1 ? static_cast<float>(lp.lopnWidth.x) : 1.0f;
                    pkt.TextColorRGBA = ColorRefToRgba(lp.lopnColor);

                    if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
                    {
                        g_ringWriter.TryWrite(pkt);
                    }
                }
            }
        }
        return res;
    }

    // -------------------------------------------------------------------------
    // Direct2D / DirectWrite Hooks
    // -------------------------------------------------------------------------
    using DrawText_t = void(STDMETHODCALLTYPE*)(
        ID2D1RenderTarget* This,
        const WCHAR* string,
        UINT32 stringLength,
        IDWriteTextFormat* textFormat,
        const D2D1_RECT_F* layoutRect,
        ID2D1Brush* defaultFillBrush,
        D2D1_DRAW_TEXT_OPTIONS options,
        DWRITE_MEASURING_MODE measuringMode);

    using DrawTextLayout_t = void(STDMETHODCALLTYPE*)(
        ID2D1RenderTarget* This,
        D2D1_POINT_2F origin,
        IDWriteTextLayout* textLayout,
        ID2D1Brush* defaultFillBrush,
        D2D1_DRAW_TEXT_OPTIONS options);

    using DrawGlyphRun_t = void(STDMETHODCALLTYPE*)(
        ID2D1RenderTarget* This,
        D2D1_POINT_2F baselineOrigin,
        const DWRITE_GLYPH_RUN* glyphRun,
        ID2D1Brush* foregroundBrush,
        DWRITE_MEASURING_MODE measuringMode);

    using DC_DrawGlyphRun_t = void(STDMETHODCALLTYPE*)(
        ID2D1DeviceContext* This,
        D2D1_POINT_2F baselineOrigin,
        const DWRITE_GLYPH_RUN* glyphRun,
        const DWRITE_GLYPH_RUN_DESCRIPTION* glyphRunDescription,
        ID2D1Brush* foregroundBrush,
        DWRITE_MEASURING_MODE measuringMode);

    using BitmapRT_DrawGlyphRun_t = HRESULT(STDMETHODCALLTYPE*)(
        IDWriteBitmapRenderTarget* This,
        FLOAT baselineOriginX,
        FLOAT baselineOriginY,
        DWRITE_MEASURING_MODE measuringMode,
        const DWRITE_GLYPH_RUN* glyphRun,
        IDWriteRenderingParams* renderingParams,
        COLORREF textColor,
        RECT* blackBoxRect);

    DrawText_t g_origDrawText = nullptr;
    DrawTextLayout_t g_origDrawTextLayout = nullptr;
    DrawGlyphRun_t g_origDrawGlyphRun = nullptr;
    DC_DrawGlyphRun_t g_origDCDrawGlyphRun = nullptr;
    BitmapRT_DrawGlyphRun_t g_origBitmapRTDrawGlyphRun = nullptr;

    bool ResolveD2DTargetWindow(ID2D1RenderTarget* This, HWND& outHwnd, POINT& outClientOrigin)
    {
        outHwnd = nullptr;
        outClientOrigin = { 0, 0 };
        bool exactTarget = false;

        ID2D1HwndRenderTarget* hwndTarget = nullptr;
        if (SUCCEEDED(This->QueryInterface(__uuidof(ID2D1HwndRenderTarget), reinterpret_cast<void**>(&hwndTarget))) && hwndTarget)
        {
            outHwnd = hwndTarget->GetHwnd();
            hwndTarget->Release();
            exactTarget = (outHwnd != nullptr);
        }

        if (!outHwnd)
        {
            ID2D1DeviceContext* dc = nullptr;
            if (SUCCEEDED(This->QueryInterface(__uuidof(ID2D1DeviceContext), reinterpret_cast<void**>(&dc))) && dc)
            {
                ID2D1Image* targetImg = nullptr;
                dc->GetTarget(&targetImg);
                if (targetImg)
                {
                    ID2D1Bitmap1* bmp1 = nullptr;
                    if (SUCCEEDED(targetImg->QueryInterface(__uuidof(ID2D1Bitmap1), reinterpret_cast<void**>(&bmp1))) && bmp1)
                    {
                        IDXGISurface* surface = nullptr;
                        if (SUCCEEDED(bmp1->GetSurface(&surface)) && surface)
                        {
                            IDXGISwapChain1* swapChain1 = nullptr;
                            if (SUCCEEDED(surface->GetParent(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&swapChain1))) && swapChain1)
                            {
                                swapChain1->GetHwnd(&outHwnd);
                                swapChain1->Release();
                                exactTarget = (outHwnd != nullptr);
                            }
                            surface->Release();
                        }
                        bmp1->Release();
                    }
                    targetImg->Release();
                }
                dc->Release();
            }
        }

        if (!outHwnd)
        {
            outHwnd = ResolveFallbackThreadHwnd();
        }

        if (outHwnd)
        {
            ClientToScreen(outHwnd, &outClientOrigin);
        }
        return exactTarget;
    }

    void PopulateFontMetadataFromFontFace(IDWriteFontFace* fontFace, VectorTextPacket& pkt)
    {
        pkt.FontWeight = 400;
        pkt.FontStyle = 0;
        WriteFixedUtf16(pkt.FontFamily, kMaxFontFamilyLength, L"Segoe UI");

        if (!fontFace)
            return;

        IDWriteFontFace3* fontFace3 = nullptr;
        if (SUCCEEDED(fontFace->QueryInterface(__uuidof(IDWriteFontFace3), reinterpret_cast<void**>(&fontFace3))) && fontFace3)
        {
            pkt.FontWeight = static_cast<int32_t>(fontFace3->GetWeight());
            pkt.FontStyle = static_cast<int32_t>(fontFace3->GetStyle());

            IDWriteLocalizedStrings* familyNames = nullptr;
            if (SUCCEEDED(fontFace3->GetFamilyNames(&familyNames)) && familyNames)
            {
                wchar_t familyBuf[kMaxFontFamilyLength]{};
                if (SUCCEEDED(familyNames->GetString(0, familyBuf, kMaxFontFamilyLength)) && familyBuf[0])
                {
                    WriteFixedUtf16(pkt.FontFamily, kMaxFontFamilyLength, familyBuf);
                }
                familyNames->Release();
            }
            fontFace3->Release();
        }
    }

    void GetEffectiveD2DDpiScale(ID2D1RenderTarget* rt, float& dpiScaleX, float& dpiScaleY)
    {
        dpiScaleX = 1.0f;
        dpiScaleY = 1.0f;
        if (!rt)
        {
            return;
        }

        ID2D1DeviceContext* dc = nullptr;
        if (SUCCEEDED(rt->QueryInterface(__uuidof(ID2D1DeviceContext), reinterpret_cast<void**>(&dc))) && dc)
        {
            const D2D1_UNIT_MODE unitMode = dc->GetUnitMode();
            dc->Release();
            if (unitMode == D2D1_UNIT_MODE_PIXELS)
            {
                return;
            }
        }

        FLOAT dpiX = 96.0f, dpiY = 96.0f;
        rt->GetDpi(&dpiX, &dpiY);
        dpiScaleX = dpiX > 1.0f ? (dpiX / 96.0f) : 1.0f;
        dpiScaleY = dpiY > 1.0f ? (dpiY / 96.0f) : 1.0f;
    }

    bool TryEmitD2DGlyphRun(
        ID2D1RenderTarget* This,
        D2D1_POINT_2F baselineOrigin,
        const DWRITE_GLYPH_RUN* glyphRun,
        const DWRITE_GLYPH_RUN_DESCRIPTION* glyphRunDesc,
        ID2D1Brush* foregroundBrush,
        int32_t vLeft,
        int32_t vTop,
        int32_t vRight,
        int32_t vBottom)
    {
        D2D1_MATRIX_3X2_F matrix{};
        This->GetTransform(&matrix);

        float dpiScaleX = 1.0f, dpiScaleY = 1.0f;
        GetEffectiveD2DDpiScale(This, dpiScaleX, dpiScaleY);

        const float localX = (baselineOrigin.x * matrix.m11 + baselineOrigin.y * matrix.m21 + matrix.dx) * dpiScaleX;
        const float localY = (baselineOrigin.x * matrix.m12 + baselineOrigin.y * matrix.m22 + matrix.dy) * dpiScaleY;
        const float scaleX = std::sqrt(matrix.m11 * matrix.m11 + matrix.m12 * matrix.m12) * dpiScaleX;
        const float scaleY = std::sqrt(matrix.m21 * matrix.m21 + matrix.m22 * matrix.m22) * dpiScaleY;
        const float emSize = glyphRun->fontEmSize * (scaleY > 0.001f ? scaleY : 1.0f);

        HWND hwnd = nullptr;
        POINT clientOrigin{ 0, 0 };
        const bool exactTarget = ResolveD2DTargetWindow(This, hwnd, clientOrigin);

        const float screenX = localX + static_cast<float>(clientOrigin.x);
        const float screenY = localY + static_cast<float>(clientOrigin.y);

        float totalAdvance = 0.0f;
        const uint32_t glyphCount = glyphRun->glyphCount < static_cast<uint32_t>(kMaxGlyphCount)
            ? glyphRun->glyphCount
            : static_cast<uint32_t>(kMaxGlyphCount);

        VectorTextPacket pkt{};
        pkt.ProcessId = GetCurrentProcessId();
        pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
        pkt.FrameId = GetCurrentFrameId(hwnd);
        pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Text);
        pkt.ScreenX = screenX;
        pkt.ScreenY = screenY;
        pkt.FontEmSize = emSize > 1.0f ? emSize : 14.0f;
        pkt.GlyphCount = static_cast<int32_t>(glyphCount);

        for (uint32_t i = 0; i < glyphCount; ++i)
        {
            if (glyphRun->glyphIndices)
            {
                pkt.GlyphIndices[i] = glyphRun->glyphIndices[i];
            }
            const float adv = glyphRun->glyphAdvances
                ? glyphRun->glyphAdvances[i] * (scaleX > 0.001f ? scaleX : 1.0f)
                : emSize * 0.5f;
            pkt.GlyphAdvances[i] = adv;
            totalAdvance += adv;
        }

        if (glyphRunDesc && glyphRunDesc->string && glyphRunDesc->stringLength > 0)
        {
            WriteFixedUtf16(pkt.Text, kMaxTextLength, glyphRunDesc->string, static_cast<int>(glyphRunDesc->stringLength));
        }

        DWRITE_FONT_METRICS fontMetrics{};
        glyphRun->fontFace->GetMetrics(&fontMetrics);
        const float designScale = pkt.FontEmSize / static_cast<float>(fontMetrics.designUnitsPerEm > 0 ? fontMetrics.designUnitsPerEm : 2048);
        const float ascent = static_cast<float>(fontMetrics.ascent) * designScale;
        const float descent = static_cast<float>(fontMetrics.descent) * designScale;

        pkt.BoundsLeft = screenX - 1.0f;
        pkt.BoundsTop = screenY - ascent - 1.0f;
        pkt.BoundsRight = screenX + totalAdvance + 1.0f;
        pkt.BoundsBottom = screenY + descent + 1.0f;

        PopulateFontMetadataFromFontFace(glyphRun->fontFace, pkt);

        bool hasSolidColor = false;
        pkt.TextColorRGBA = 0x000000FFu;
        if (foregroundBrush)
        {
            ID2D1SolidColorBrush* solidBrush = nullptr;
            if (SUCCEEDED(foregroundBrush->QueryInterface(__uuidof(ID2D1SolidColorBrush), reinterpret_cast<void**>(&solidBrush))) && solidBrush)
            {
                const D2D1_COLOR_F c = solidBrush->GetColor();
                pkt.TextColorRGBA = D2DColorToRgba(c.r, c.g, c.b, c.a);
                solidBrush->Release();
                hasSolidColor = true;
            }
        }

        pkt.BgColorRGBA = 0x00000000u;
        if (!exactTarget || (foregroundBrush && !hasSolidColor))
        {
            return false;
        }
        return IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom) && g_ringWriter.TryWrite(pkt);
    }

    void STDMETHODCALLTYPE Hooked_DrawText(
        ID2D1RenderTarget* This,
        const WCHAR* string,
        UINT32 stringLength,
        IDWriteTextFormat* textFormat,
        const D2D1_RECT_F* layoutRect,
        ID2D1Brush* defaultFillBrush,
        D2D1_DRAW_TEXT_OPTIONS options,
        DWRITE_MEASURING_MODE measuringMode)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (g_d2dHookDepth == 0 && This && string && stringLength > 0 && layoutRect && textFormat &&
            g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            D2D1_MATRIX_3X2_F matrix{};
            This->GetTransform(&matrix);

            float dpiScaleX = 1.0f, dpiScaleY = 1.0f;
            GetEffectiveD2DDpiScale(This, dpiScaleX, dpiScaleY);

            const float scaleY = std::sqrt(matrix.m21 * matrix.m21 + matrix.m22 * matrix.m22) * dpiScaleY;
            const float emSize = textFormat->GetFontSize() * (scaleY > 0.001f ? scaleY : 1.0f);

            HWND hwnd = nullptr;
            POINT clientOrigin{ 0, 0 };
            const bool exactTarget = ResolveD2DTargetWindow(This, hwnd, clientOrigin);

            const float left = (layoutRect->left * matrix.m11 + layoutRect->top * matrix.m21 + matrix.dx) * dpiScaleX + clientOrigin.x;
            const float top = (layoutRect->left * matrix.m12 + layoutRect->top * matrix.m22 + matrix.dy) * dpiScaleY + clientOrigin.y;
            const float right = (layoutRect->right * matrix.m11 + layoutRect->bottom * matrix.m21 + matrix.dx) * dpiScaleX + clientOrigin.x;
            const float bottom = (layoutRect->right * matrix.m12 + layoutRect->bottom * matrix.m22 + matrix.dy) * dpiScaleY + clientOrigin.y;

            VectorTextPacket pkt{};
            pkt.ProcessId = GetCurrentProcessId();
            pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
            pkt.FrameId = GetCurrentFrameId(hwnd);
            pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Text);
            pkt.ScreenX = left;
            pkt.ScreenY = top + emSize * 0.85f;
            pkt.BoundsLeft = left;
            pkt.BoundsTop = top;
            pkt.BoundsRight = right;
            pkt.BoundsBottom = bottom;
            pkt.FontEmSize = emSize > 1.0f ? emSize : 14.0f;
            pkt.FontWeight = static_cast<int32_t>(textFormat->GetFontWeight());
            pkt.FontStyle = static_cast<int32_t>(textFormat->GetFontStyle());

            wchar_t familyBuf[kMaxFontFamilyLength]{};
            if (SUCCEEDED(textFormat->GetFontFamilyName(familyBuf, kMaxFontFamilyLength)) && familyBuf[0])
            {
                WriteFixedUtf16(pkt.FontFamily, kMaxFontFamilyLength, familyBuf);
            }
            else
            {
                WriteFixedUtf16(pkt.FontFamily, kMaxFontFamilyLength, L"Segoe UI");
            }
            WriteFixedUtf16(pkt.Text, kMaxTextLength, string, static_cast<int>(stringLength));

            bool hasSolidColor = false;
            pkt.TextColorRGBA = 0x000000FFu;
            if (defaultFillBrush)
            {
                ID2D1SolidColorBrush* solidBrush = nullptr;
                if (SUCCEEDED(defaultFillBrush->QueryInterface(__uuidof(ID2D1SolidColorBrush), reinterpret_cast<void**>(&solidBrush))) && solidBrush)
                {
                    const D2D1_COLOR_F c = solidBrush->GetColor();
                    pkt.TextColorRGBA = D2DColorToRgba(c.r, c.g, c.b, c.a);
                    solidBrush->Release();
                    hasSolidColor = true;
                }
            }

            if (exactTarget && (!defaultFillBrush || hasSolidColor) && IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
            {
                g_ringWriter.TryWrite(pkt);
            }
        }

        ++g_d2dHookDepth;
        g_origDrawText(This, string, stringLength, textFormat, layoutRect, defaultFillBrush, options, measuringMode);
        --g_d2dHookDepth;
    }

    void STDMETHODCALLTYPE Hooked_DrawTextLayout(
        ID2D1RenderTarget* This,
        D2D1_POINT_2F origin,
        IDWriteTextLayout* textLayout,
        ID2D1Brush* defaultFillBrush,
        D2D1_DRAW_TEXT_OPTIONS options)
    {
        g_origDrawTextLayout(This, origin, textLayout, defaultFillBrush, options);
    }

    void STDMETHODCALLTYPE Hooked_DrawGlyphRun(
        ID2D1RenderTarget* This,
        D2D1_POINT_2F baselineOrigin,
        const DWRITE_GLYPH_RUN* glyphRun,
        ID2D1Brush* foregroundBrush,
        DWRITE_MEASURING_MODE measuringMode)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (g_d2dHookDepth == 0 && This && glyphRun && glyphRun->fontFace && glyphRun->glyphCount > 0 &&
            g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            TryEmitD2DGlyphRun(This, baselineOrigin, glyphRun, nullptr, foregroundBrush, vLeft, vTop, vRight, vBottom);
        }

        g_origDrawGlyphRun(This, baselineOrigin, glyphRun, foregroundBrush, measuringMode);
    }

    void STDMETHODCALLTYPE Hooked_DC_DrawGlyphRun(
        ID2D1DeviceContext* This,
        D2D1_POINT_2F baselineOrigin,
        const DWRITE_GLYPH_RUN* glyphRun,
        const DWRITE_GLYPH_RUN_DESCRIPTION* glyphRunDescription,
        ID2D1Brush* foregroundBrush,
        DWRITE_MEASURING_MODE measuringMode)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (g_d2dHookDepth == 0 && This && glyphRun && glyphRun->fontFace && glyphRun->glyphCount > 0 &&
            g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            TryEmitD2DGlyphRun(This, baselineOrigin, glyphRun, glyphRunDescription, foregroundBrush, vLeft, vTop, vRight, vBottom);
        }

        g_origDCDrawGlyphRun(This, baselineOrigin, glyphRun, glyphRunDescription, foregroundBrush, measuringMode);
    }

    HRESULT STDMETHODCALLTYPE Hooked_BitmapRT_DrawGlyphRun(
        IDWriteBitmapRenderTarget* This,
        FLOAT baselineOriginX,
        FLOAT baselineOriginY,
        DWRITE_MEASURING_MODE measuringMode,
        const DWRITE_GLYPH_RUN* glyphRun,
        IDWriteRenderingParams* renderingParams,
        COLORREF textColor,
        RECT* blackBoxRect)
    {
        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (This && glyphRun && glyphRun->fontFace && glyphRun->glyphCount > 0 &&
            g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            DWRITE_MATRIX matrix{ 1, 0, 0, 1, 0, 0 };
            This->GetCurrentTransform(&matrix);

            const float localX = baselineOriginX * matrix.m11 + baselineOriginY * matrix.m21 + matrix.dx;
            const float localY = baselineOriginX * matrix.m12 + baselineOriginY * matrix.m22 + matrix.dy;
            const float scaleX = std::sqrt(matrix.m11 * matrix.m11 + matrix.m12 * matrix.m12);
            const float scaleY = std::sqrt(matrix.m21 * matrix.m21 + matrix.m22 * matrix.m22);
            const float emSize = glyphRun->fontEmSize * (scaleY > 0.001f ? scaleY : 1.0f);

            HDC hdc = This->GetMemoryDC();
            HWND hwnd = hdc ? WindowFromDC(hdc) : nullptr;
            POINT dcOrigin{ 0, 0 };
            const bool isWindowDc = hdc && ResolveDcScreenOrigin(hdc, hwnd, dcOrigin);
            if (!hwnd)
            {
                hwnd = ResolveFallbackThreadHwnd();
            }

            const float screenX = localX + static_cast<float>(dcOrigin.x);
            const float screenY = localY + static_cast<float>(dcOrigin.y);

            float totalAdvance = 0.0f;
            const uint32_t glyphCount = glyphRun->glyphCount < static_cast<uint32_t>(kMaxGlyphCount)
                ? glyphRun->glyphCount
                : static_cast<uint32_t>(kMaxGlyphCount);

            VectorTextPacket pkt{};
            pkt.ProcessId = GetCurrentProcessId();
            pkt.Hwnd = reinterpret_cast<uint64_t>(hwnd);
            pkt.FrameId = GetCurrentFrameId(hwnd);
            pkt.PacketKind = static_cast<uint32_t>(VectorPacketKind::Text);
            pkt.ScreenX = screenX;
            pkt.ScreenY = screenY;
            pkt.FontEmSize = emSize > 1.0f ? emSize : 14.0f;
            pkt.TextColorRGBA = ColorRefToRgba(textColor);
            pkt.BgColorRGBA = 0x00000000u;
            pkt.GlyphCount = static_cast<int32_t>(glyphCount);

            for (uint32_t i = 0; i < glyphCount; ++i)
            {
                if (glyphRun->glyphIndices) pkt.GlyphIndices[i] = glyphRun->glyphIndices[i];
                const float adv = glyphRun->glyphAdvances
                    ? glyphRun->glyphAdvances[i] * (scaleX > 0.001f ? scaleX : 1.0f)
                    : emSize * 0.5f;
                pkt.GlyphAdvances[i] = adv;
                totalAdvance += adv;
            }

            DWRITE_FONT_METRICS fontMetrics{};
            glyphRun->fontFace->GetMetrics(&fontMetrics);
            const float designScale = pkt.FontEmSize / static_cast<float>(fontMetrics.designUnitsPerEm > 0 ? fontMetrics.designUnitsPerEm : 2048);
            pkt.BoundsLeft = screenX - 1.0f;
            pkt.BoundsTop = screenY - static_cast<float>(fontMetrics.ascent) * designScale - 1.0f;
            pkt.BoundsRight = screenX + totalAdvance + 1.0f;
            pkt.BoundsBottom = screenY + static_cast<float>(fontMetrics.descent) * designScale + 1.0f;

            PopulateFontMetadataFromFontFace(glyphRun->fontFace, pkt);

            if (!isWindowDc && hdc)
            {
                StageMemDcPacket(hdc, pkt);
            }
            else if (IntersectsViewport(pkt, vLeft, vTop, vRight, vBottom))
            {
                g_ringWriter.TryWrite(pkt);
            }
        }

        return g_origBitmapRTDrawGlyphRun(This, baselineOriginX, baselineOriginY, measuringMode, glyphRun, renderingParams, textColor, blackBoxRect);
    }

    void HookVTableEntry(void** vtable, int index, LPVOID detour, LPVOID* original)
    {
        if (vtable && vtable[index])
        {
            MH_CreateHook(vtable[index], detour, original);
        }
    }

    void InstallD2DAndDWriteHooks()
    {
        if (GetModuleHandleW(L"d2d1.dll") != nullptr)
        {
            ID2D1Factory1* factory = nullptr;
            if (SUCCEEDED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), nullptr, reinterpret_cast<void**>(&factory))) && factory)
            {
                D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
                    D2D1_RENDER_TARGET_TYPE_DEFAULT,
                    D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
                    0.0f,
                    0.0f,
                    D2D1_RENDER_TARGET_USAGE_NONE,
                    D2D1_FEATURE_LEVEL_DEFAULT);

                ID2D1DCRenderTarget* dcTarget = nullptr;
                if (SUCCEEDED(factory->CreateDCRenderTarget(&props, &dcTarget)) && dcTarget)
                {
                    void** vtable = *reinterpret_cast<void***>(dcTarget);
                    HookVTableEntry(vtable, 27, reinterpret_cast<LPVOID>(&Hooked_DrawText), reinterpret_cast<LPVOID*>(&g_origDrawText));
                    HookVTableEntry(vtable, 28, reinterpret_cast<LPVOID>(&Hooked_DrawTextLayout), reinterpret_cast<LPVOID*>(&g_origDrawTextLayout));
                    HookVTableEntry(vtable, 29, reinterpret_cast<LPVOID>(&Hooked_DrawGlyphRun), reinterpret_cast<LPVOID*>(&g_origDrawGlyphRun));

                    ID2D1DeviceContext* dcFromTarget = nullptr;
                    if (SUCCEEDED(dcTarget->QueryInterface(__uuidof(ID2D1DeviceContext), reinterpret_cast<void**>(&dcFromTarget))) && dcFromTarget)
                    {
                        void** dcVtable = *reinterpret_cast<void***>(dcFromTarget);
                        HookVTableEntry(dcVtable, 82, reinterpret_cast<LPVOID>(&Hooked_DC_DrawGlyphRun), reinterpret_cast<LPVOID*>(&g_origDCDrawGlyphRun));
                        dcFromTarget->Release();
                    }

                    dcTarget->Release();
                }

                if (!g_origDCDrawGlyphRun && GetModuleHandleW(L"d3d11.dll") != nullptr)
                {
                    ID3D11Device* d3dDevice = nullptr;
                    if (SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &d3dDevice, nullptr, nullptr)) && d3dDevice)
                    {
                        IDXGIDevice* dxgiDevice = nullptr;
                        if (SUCCEEDED(d3dDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice))) && dxgiDevice)
                        {
                            ID2D1Device* d2dDevice = nullptr;
                            if (SUCCEEDED(factory->CreateDevice(dxgiDevice, &d2dDevice)) && d2dDevice)
                            {
                                ID2D1DeviceContext* d2dContext = nullptr;
                                if (SUCCEEDED(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2dContext)) && d2dContext)
                                {
                                    void** dcVtable = *reinterpret_cast<void***>(d2dContext);
                                    HookVTableEntry(dcVtable, 82, reinterpret_cast<LPVOID>(&Hooked_DC_DrawGlyphRun), reinterpret_cast<LPVOID*>(&g_origDCDrawGlyphRun));
                                    d2dContext->Release();
                                }
                                d2dDevice->Release();
                            }
                            dxgiDevice->Release();
                        }
                        d3dDevice->Release();
                    }
                }

                factory->Release();
            }
        }

        if (GetModuleHandleW(L"dwrite.dll") != nullptr)
        {
            IDWriteFactory* dwriteFactory = nullptr;
            if (SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&dwriteFactory))) && dwriteFactory)
            {
                IDWriteGdiInterop* gdiInterop = nullptr;
                if (SUCCEEDED(dwriteFactory->GetGdiInterop(&gdiInterop)) && gdiInterop)
                {
                    IDWriteBitmapRenderTarget* bmpTarget = nullptr;
                    if (SUCCEEDED(gdiInterop->CreateBitmapRenderTarget(nullptr, 16, 16, &bmpTarget)) && bmpTarget)
                    {
                        void** bmpVtable = *reinterpret_cast<void***>(bmpTarget);
                        HookVTableEntry(bmpVtable, 3, reinterpret_cast<LPVOID>(&Hooked_BitmapRT_DrawGlyphRun), reinterpret_cast<LPVOID*>(&g_origBitmapRTDrawGlyphRun));
                        bmpTarget->Release();
                    }
                    gdiInterop->Release();
                }
                dwriteFactory->Release();
            }
        }
    }

    bool IsProcessExcludedOrHost()
    {
        wchar_t exePath[MAX_PATH] = {};
        DWORD len = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        if (len == 0 || len >= MAX_PATH)
            return false;

        const wchar_t* fileName = wcsrchr(exePath, L'\\');
        fileName = fileName ? (fileName + 1) : exePath;

        static constexpr const wchar_t* kExcluded[] = {
            L"ZoomHost.exe",
            L"dwm.exe",
            L"csrss.exe",
            L"winlogon.exe",
            L"services.exe",
            L"lsass.exe",
            L"fontdrvhost.exe",
            L"EasyAntiCheat.exe",
            L"BEService.exe",
            L"vgk.exe"
        };

        for (const wchar_t* ex : kExcluded)
        {
            if (_wcsicmp(fileName, ex) == 0)
                return true;
        }
        return false;
    }

    DWORD WINAPI InitHookThread(LPVOID)
    {
        if (IsProcessExcludedOrHost())
            return 0;

        g_ringWriter.Open();

        if (MH_Initialize() != MH_OK)
            return 1;

        HMODULE hGdi32 = GetModuleHandleW(L"gdi32.dll");
        if (!hGdi32)
            hGdi32 = LoadLibraryW(L"gdi32.dll");

        FARPROC pExtTextOutW = nullptr;
        FARPROC pPolyTextOutW = nullptr;
        FARPROC pBitBlt = nullptr;
        FARPROC pGdiAlphaBlend = nullptr;

        if (hGdi32)
        {
            if ((pExtTextOutW = GetProcAddress(hGdi32, "ExtTextOutW")))
                MH_CreateHook(pExtTextOutW, reinterpret_cast<LPVOID>(&Hooked_ExtTextOutW), reinterpret_cast<LPVOID*>(&g_origExtTextOutW));
            if ((pPolyTextOutW = GetProcAddress(hGdi32, "PolyTextOutW")))
                MH_CreateHook(pPolyTextOutW, reinterpret_cast<LPVOID>(&Hooked_PolyTextOutW), reinterpret_cast<LPVOID*>(&g_origPolyTextOutW));
            if ((pBitBlt = GetProcAddress(hGdi32, "BitBlt")))
                MH_CreateHook(pBitBlt, reinterpret_cast<LPVOID>(&Hooked_BitBlt), reinterpret_cast<LPVOID*>(&g_origBitBlt));
            if ((pGdiAlphaBlend = GetProcAddress(hGdi32, "GdiAlphaBlend")))
                MH_CreateHook(pGdiAlphaBlend, reinterpret_cast<LPVOID>(&Hooked_GdiAlphaBlend), reinterpret_cast<LPVOID*>(&g_origGdiAlphaBlend));
            if (FARPROC p = GetProcAddress(hGdi32, "LineTo"))
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_LineTo), reinterpret_cast<LPVOID*>(&g_origLineTo));
            if (FARPROC p = GetProcAddress(hGdi32, "Rectangle"))
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_Rectangle), reinterpret_cast<LPVOID*>(&g_origRectangle));
        }

        if (HMODULE hGdi32Full = GetModuleHandleW(L"gdi32full.dll"))
        {
            if (FARPROC p = GetProcAddress(hGdi32Full, "ExtTextOutW"); p && p != pExtTextOutW)
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_ExtTextOutW_Full), reinterpret_cast<LPVOID*>(&g_origExtTextOutW_Full));
            if (FARPROC p = GetProcAddress(hGdi32Full, "PolyTextOutW"); p && p != pPolyTextOutW)
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_PolyTextOutW_Full), reinterpret_cast<LPVOID*>(&g_origPolyTextOutW_Full));
            if (FARPROC p = GetProcAddress(hGdi32Full, "BitBlt"); p && p != pBitBlt)
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_BitBlt_Full), reinterpret_cast<LPVOID*>(&g_origBitBlt_Full));
            if (FARPROC p = GetProcAddress(hGdi32Full, "GdiAlphaBlend"); p && p != pGdiAlphaBlend)
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_GdiAlphaBlend_Full), reinterpret_cast<LPVOID*>(&g_origGdiAlphaBlend_Full));
        }

        HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
        if (hUser32)
        {
            if (FARPROC p = GetProcAddress(hUser32, "DrawIconEx"))
                MH_CreateHook(p, reinterpret_cast<LPVOID>(&Hooked_DrawIconEx), reinterpret_cast<LPVOID*>(&g_origDrawIconEx));
        }

        InstallD2DAndDWriteHooks();
        MH_EnableHook(MH_ALL_HOOKS);
        g_hooksInstalled.store(true);

        int32_t vLeft = 0, vTop = 0, vRight = 0, vBottom = 0;
        if (g_ringWriter.TryGetMagnifierViewport(vLeft, vTop, vRight, vBottom))
        {
            const DWORD selfPid = GetCurrentProcessId();
            EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL {
                DWORD pid = 0;
                GetWindowThreadProcessId(hwnd, &pid);
                if (pid == static_cast<DWORD>(lParam) && IsWindowVisible(hwnd))
                {
                    RedrawWindow(hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
                }
                return TRUE;
            }, static_cast<LPARAM>(selfPid));
        }
        return 0;
    }
}

extern "C" __declspec(dllexport) LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0 && lParam != 0)
    {
        const CWPSTRUCT* cwp = reinterpret_cast<const CWPSTRUCT*>(lParam);
        if (cwp->hwnd)
        {
            g_currentMsgHwnd = cwp->hwnd;
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpvReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        g_initThread = CreateThread(nullptr, 0, InitHookThread, nullptr, 0, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        if (lpvReserved != nullptr)
        {
            return TRUE;
        }
        if (g_initThread)
        {
            CloseHandle(g_initThread);
            g_initThread = nullptr;
        }
        if (g_hooksInstalled.load())
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
            g_hooksInstalled.store(false);
        }
        g_ringWriter.Close();
        break;
    }
    return TRUE;
}
