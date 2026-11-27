// Edge-preserving, wave-free vector-style text magnifier (optimised for AMD RDNA 2 / 120Hz+ SDR & HDR).
//
// Pass 0a (PrepColorPS, visible crop + 16px margin):
//   - Converts HDR scRGB (R16G16B16A16_Float) or SDR to perceptual sRGB + Rec.709 luma ONCE per
//     desktop pixel into an R8G8B8A8_UNorm cache texture (PercepTex), eliminating 100% of per-tap
//     pow() calls and halving texture bandwidth across PolePS and PSMain.
// Pass 0b (PrepMetaPS, visible crop + 13px margin):
//   - Evaluates 4-neighbour RGB flatness, horizontal luma step, and ClearType fringe flags ONCE per
//     desktop pixel into MetaTex (R8G8B8A8_UNorm), eliminating 196 redundant neighbour loads per
//     pixel in PolePS.
// Pass 1 (PolePS, desktop resolution, visible crop only):
//   - Zero-spill (no VGPR scratch arrays) 7x7 pole & Scharr structure-tensor pass reading strictly
//     from PercepTex + MetaTex in L0/L1 cache.
// Pass 2 (PSMain, screen resolution):
//   - Early-outs with a single 4-byte bilinear tap on flat regions.
//   - Unpacks 3x horizontal ClearType subpixel resolution for achromatic ClearType text.
//   - Uses ink-connected 2x2 diagonal triangulation for grayscale & coloured text.
//   - Applies anisotropic tangent-aligned smoothing along diagonal & curved contours.

cbuffer Params : register(b0)
{
    float2 Focus;          // world (desktop texel) position shown at the screen centre
    float  Zoom;
    float  Sharpness;      // UI "edge smoothing" 0..1: widens the anti-aliased edge
    float2 ScreenSize;
    float2 DesktopSize;
    float  TextGamma;      // UI "text gamma" 0..1: slightly bolder text
    float  IsHdr;
    float  SdrWhiteScale;
    float  ColorMode;      // 0=Normal, 1=Invert, 2=YellowOnBlack, 3=WhiteOnBlue, 4=Sepia
    float4 CropRectNorm;
    float4 ExtraParams;    // x: MediaMode (0=Auto, 1=AlwaysNative, 2=Off)
};

Texture2D    DesktopTex   : register(t0);
Texture2D    PoleDarkTex  : register(t1);
Texture2D    PoleLightTex : register(t2);
Texture2D    LocalExtTex  : register(t3);
Texture2D    PercepTex    : register(t4);
Texture2D    MetaTex      : register(t5);
Texture2D    MediaMaskTex : register(t6);
SamplerState LinearSamp   : register(s1);

struct VSOut
{
    float4 Pos : SV_POSITION;
    float2 Uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.Pos = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    o.Uv = uv;
    return o;
}

VSOut VSCropMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 t  = float2((float)(id & 1u), (float)((id >> 1u) & 1u));
    float2 uv = lerp(CropRectNorm.xy, CropRectNorm.zw, t);
    o.Pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
    o.Uv = uv;
    return o;
}

static const float3 LumaWeights = float3(0.2126f, 0.7152f, 0.0722f);   // Rec.709
static const float3 MeanWeights = float3(1.0f, 1.0f, 1.0f) / 3.0f;

static const float EdgeContrastLo  = 0.018f;
static const float EdgeContrastHi  = 0.060f;
static const float LocalContrastLo = 0.015f;
static const float LocalContrastHi = 0.065f;
static const int   PoleRadius      = 3;       // 7x7 window: sees both opposite ClearType fringes across stems
static const float TwoToneLo       = 0.54f;
static const float TwoToneHi       = 0.76f;
static const float ExactPoleTol    = 0.035f;
static const float FringeChroma    = 0.05f;
static const float FringeBonus     = 4.0f;
static const float FringeNeutralLo = 0.004f;
static const float FringeNeutralHi = 0.018f;
static const float TintChromaLo    = 0.14f;
static const float TintChromaHi    = 0.24f;
static const float MaxBoldShift    = 0.12f;

// Exact inverse Windows piecewise sRGB OETF for HDR (R16G16B16A16_Float scRGB) capture -> sRGB output:
// Windows DWM renders SDR windows into the HDR desktop as SrgbToLinear(s) * SdrWhiteScale.
// Converting back via LinearToSrgb(c / SdrWhiteScale) into a B8G8R8A8_UNorm sRGB swapchain lets DWM
// display zoomed content with 100% identical gamma, brightness, black/white levels, and colours.
float3 ToPerceptual(float3 c)
{
    if (IsHdr < 0.5f)
    {
        return saturate(c);
    }
    float3 scaled = c / max(SdrWhiteScale, 0.25f);
    float  mn     = min(0.0f, min(scaled.r, min(scaled.g, scaled.b)));
    float  lum    = max(dot(scaled, LumaWeights), 0.0f);
    float  satK   = (mn < 0.0f) ? (lum / max(lum - mn, 1e-5f)) : 1.0f;
    scaled        = lerp(lum.xxx, scaled, satK);
    float  peak   = max(1.0f, max(scaled.r, max(scaled.g, scaled.b)));
    float3 lin    = saturate(scaled / peak);
    float3 lo     = lin * 12.92f;
    float3 hi     = 1.055f * pow(max(lin, 1e-6f), 1.0f / 2.4f) - 0.055f;
    return saturate((lin <= 0.0031308f) ? lo : hi);
}

float3 ToOutput(float3 s)
{
    float3 c = saturate(s);
    int mode = (int)(ColorMode + 0.5f);
    if (mode == 1)
    {
        return 1.0f - c;
    }
    if (mode == 2)
    {
        // High-contrast Yellow on Black (reading mode)
        float invL = 1.0f - dot(c, LumaWeights);
        return lerp(float3(0.03f, 0.03f, 0.04f), float3(1.0f, 0.90f, 0.18f), invL);
    }
    if (mode == 3)
    {
        // High-contrast White on Dark Blue (reading mode)
        float invL = 1.0f - dot(c, LumaWeights);
        return lerp(float3(0.04f, 0.12f, 0.26f), float3(0.96f, 0.98f, 1.0f), invL);
    }
    if (mode == 4)
    {
        // Soft Sepia / Glare Reduction
        return saturate(float3(c.r * 0.96f + 0.02f, c.g * 0.89f + 0.01f, c.b * 0.74f));
    }
    return c;
}

float4 FetchPercep(int2 t)
{
    int2 maxT = int2(DesktopSize) - 1;
    return PercepTex.Load(int3(clamp(t, int2(0, 0), maxT), 0));
}

float4 FetchMeta(int2 t)
{
    int2 maxT = int2(DesktopSize) - 1;
    return MetaTex.Load(int3(clamp(t, int2(0, 0), maxT), 0));
}

float4 CatmullRom(float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return float4(-0.5f * t3 +        t2 - 0.5f * t,
                   1.5f * t3 - 2.5f * t2 + 1.0f,
                  -1.5f * t3 + 2.0f * t2 + 0.5f * t,
                   0.5f * t3 - 0.5f * t2);
}

float3 MakePole(float3 avg, float targetL, float neutralize)
{
    float  avgL   = dot(avg, LumaWeights);
    float3 chroma = avg - avgL;
    float  amount = length(chroma);
    float  keep   = 1.0f - neutralize * (1.0f - smoothstep(TintChromaLo, TintChromaHi, amount));
    return max(targetL + chroma * keep, 0.0f);
}

// Pass 0a: Convert raw desktop texel to perceptual sRGB + Rec.709 luma ONCE per pixel.
float4 PrepColorPS(VSOut input) : SV_TARGET
{
    int2   pos = int2(input.Pos.xy);
    int2   maxT = int2(DesktopSize) - 1;
    float3 c   = ToPerceptual(DesktopTex.Load(int3(clamp(pos, int2(0, 0), maxT), 0)).rgb);
    float  l   = dot(c, LumaWeights);
    return float4(c, l);
}

// Pass 0b: Precompute 4-neighbour RGB flatness, horizontal luma step, and ClearType fringe flags
// ONCE per pixel so PolePS never re-fetches 4 neighbours inside its 7x7 loop.
float4 PrepMetaPS(VSOut input) : SV_TARGET
{
    int2   pos = int2(input.Pos.xy);
    float4 c4  = FetchPercep(pos);
    float4 cL4 = FetchPercep(pos + int2(-1,  0));
    float4 cR4 = FetchPercep(pos + int2( 1,  0));
    float4 cU4 = FetchPercep(pos + int2( 0, -1));
    float4 cD4 = FetchPercep(pos + int2( 0,  1));

    float3 c  = c4.rgb;
    float3 cL = cL4.rgb;
    float3 cR = cR4.rgb;
    float3 cU = cU4.rgb;
    float3 cD = cD4.rgb;

    float3 aL = abs(c - cL); float dL = max(aL.r, max(aL.g, aL.b));
    float3 aR = abs(c - cR); float dR = max(aR.r, max(aR.g, aR.b));
    float3 aU = abs(c - cU); float dU = max(aU.r, max(aU.g, aU.b));
    float3 aD = abs(c - cD); float dD = max(aD.r, max(aD.g, aD.b));
    float  dMax = max(max(dL, dR), max(dU, dD));
    float  sameCount = (dL < 0.022f ? 1.0f : 0.0f) + (dR < 0.022f ? 1.0f : 0.0f)
                     + (dU < 0.022f ? 1.0f : 0.0f) + (dD < 0.022f ? 1.0f : 0.0f);

    float lStepH = abs(cR4.a - cL4.a);
    float rb     = c.r - c.b;
    float rbLs   = cL.r - cL.b;
    float rbRs   = cR.r - cR.b;
    float rbL    = abs(rbLs);
    float rbR    = abs(rbRs);

    float fMonoG = ((c.g >= min(c.r, c.b) - 0.04f) && (c.g <= max(c.r, c.b) + 0.04f)) ? 1.0f : 0.0f;
    float fSand  = (rbL < 0.05f && rbR < 0.05f) ? 2.0f : 0.0f;
    float fEdge  = (rbL < abs(rb) * 0.55f || rbR < abs(rb) * 0.55f) ? 4.0f : 0.0f;
    float fHueH  = (abs(rb - rbLs) < 0.14f && abs(rb - rbRs) < 0.14f) ? 8.0f : 0.0f;

    return float4(saturate(dMax), sameCount * 0.25f, saturate(lStepH), (fMonoG + fSand + fEdge + fHueH) / 15.0f);
}

struct PoleOut
{
    float4 Dark     : SV_TARGET0;   // rgb: dark pole colour, a: two-tone confidence
    float4 Light    : SV_TARGET1;   // rgb: light pole colour, a: share of light texels (polarity)
    float4 LocalExt : SV_TARGET2;   // rg: double-angle tangent, b: ClearType dir (0=BGR, 0.5=None, 1=RGB), a: localC
};

PoleOut PolePS(VSOut input)
{
    int2 center = int2(input.Pos.xy);

    // Pass A: 7x7 extremes + 3x3 local contrast directly from L0/L1-cached PercepTex (zero scratch arrays!).
    float lMin = 1e6f, lMax = -1e6f, mMin = 1e6f, mMax = -1e6f;
    float chanMin = 1e6f, chanMax = -1e6f;
    float3 cDarkest = 1.0f, cLightest = 0.0f;
    float l3Min = 1e6f, l3Max = -1e6f;
    float3 c3Min = 1e6f, c3Max = -1e6f;
    float3 c7Min = 1e6f, c7Max = -1e6f;

    [loop]
    for (int y = -PoleRadius; y <= PoleRadius; ++y)
    {
        [loop]
        for (int x = -PoleRadius; x <= PoleRadius; ++x)
        {
            float4 ca = FetchPercep(center + int2(x, y));
            float3 c  = ca.rgb;
            float  l  = ca.a;
            float  m  = dot(c, MeanWeights);
            if (l < lMin) { lMin = l; cDarkest  = c; }
            if (l > lMax) { lMax = l; cLightest = c; }
            mMin = min(mMin, m);  mMax = max(mMax, m);
            chanMin = min(chanMin, min(c.r, min(c.g, c.b)));
            chanMax = max(chanMax, max(c.r, max(c.g, c.b)));
            c7Min = min(c7Min, c);
            c7Max = max(c7Max, c);
            if (abs(x) <= 1 && abs(y) <= 1)
            {
                l3Min = min(l3Min, l);
                l3Max = max(l3Max, l);
                c3Min = min(c3Min, c);
                c3Max = max(c3Max, c);
            }
        }
    }

    float localC = max(l3Max - l3Min, max(c3Max.r - c3Min.r, max(c3Max.g - c3Min.g, c3Max.b - c3Min.b)));
    float wideC  = lMax - lMin;

    // Fast early-out when the 7x7 window OR the 3x3 local neighbourhood is flat:
    // 0.025 (~6.4/255) early-outs on 6/255 3-row background gradient steps (preventing floating
    // dark contour lines 3px from drop-shadows) while real text edges have localC >> 0.025.
    if (wideC < 0.015f || localC < 0.025f)
    {
        float3 cCenter = FetchPercep(center).rgb;
        PoleOut flatOut;
        flatOut.Dark     = float4(cCenter, 0.0f);
        flatOut.Light    = float4(cCenter, 0.5f);
        flatOut.LocalExt = float4(0.5f, 0.5f, 0.5f, 0.0f);
        return flatOut;
    }

    float invL = 1.0f / max(wideC, 1e-3f);
    float invM = 1.0f / max(mMax - mMin, 1e-3f);

    float3 bestDarkAnchor     = 0.0f;
    float  bestDarkScore      = -1.0f;
    float3 bestLightAnchor    = 0.0f;
    float  bestLightScore     = -1.0f;
    float3 darkPureSum        = 0.0f;
    float  darkPureW          = 0.0f;
    float3 lightPureSum       = 0.0f;
    float  lightPureW         = 0.0f;
    float3 bestDarkInkAnchor  = 0.0f;
    float  bestDarkInkScore   = -1.0f;
    float3 bestLightInkAnchor = 0.0f;
    float  bestLightInkScore  = -1.0f;
    float  darkFlatW          = 0.0f;
    float  lightFlatW         = 0.0f;
    bool   hasPureFlatDarkBg  = false;
    bool   hasPureFlatLightBg = false;

    float3 darkBroadSum = 0.0f, lightBroadSum = 0.0f;
    float  darkBroadW   = 0.0f, lightBroadW   = 0.0f;
    float  fringePairs  = 0.0f, lightShare    = 0.0f;
    bool   hasOppositeFringes = false;
    bool   winHasPosRb        = false;
    bool   winHasNegRb        = false;
    float  edgeMonoCount      = 0.0f;
    bool   hasFlatSatColor    = false;

    // Pass B: Single PercepTex + MetaTex fetch per pixel (no 4-neighbour re-fetches, no scratch arrays!).
    [loop]
    for (int y2 = -PoleRadius; y2 <= PoleRadius; ++y2)
    {
        float prevRb  = 0.0f;
        float prev2Rb = 0.0f;
        [loop]
        for (int x2 = -PoleRadius; x2 <= PoleRadius; ++x2)
        {
            int2   pos  = center + int2(x2, y2);
            float4 ca   = FetchPercep(pos);
            float4 meta = FetchMeta(pos);
            float3 c    = ca.rgb;
            float  l    = ca.a;
            float  m    = dot(c, MeanWeights);
            float  u    = (l - lMin) * invL;
            float  um   = (m - mMin) * invM;
            float  rb   = c.r - c.b;

            float dMax      = meta.r;
            float sameCount = meta.g * 4.0f;
            float lStepH    = meta.b;
            uint  flags     = (uint)(meta.a * 15.0f + 0.5f);
            bool  monoG     = (flags & 1u) != 0u;
            bool  sandNbrs  = (flags & 2u) != 0u;
            bool  edgeNbrs  = (flags & 4u) != 0u;
            bool  sameHueH  = (flags & 8u) != 0u;

            float notTouchingInk = 1.0f - smoothstep(0.16f * wideC, 0.34f * wideC, dMax);
            // Relative flatness fallback for text over steeper gradients or lightly textured backgrounds
            // where absolute dMax >= 0.022 but dMax is still small compared to the text contrast wideC.
            float relFlat        = (wideC > 0.22f) ? (0.45f * (1.0f - smoothstep(0.06f * wideC, 0.13f * wideC, dMax))) : 0.0f;
            float flat2D         = max(smoothstep(2.4f, 3.6f, sameCount), relFlat) * notTouchingInk;
            float distSq         = (float)(x2 * x2 + y2 * y2);
            float distW          = 1.0f / (1.0f + 3.0f * distSq);
            float bgW            = flat2D * distW;

            if (bgW > 0.005f)
            {
                bool pureFlat4 = (sameCount >= 3.5f && dMax < 0.022f);
                float anchorScore = pureFlat4 ? (24.0f / (1.0f + 0.5f * distSq)) : (0.35f * bgW);
                if (u < 0.48f)
                {
                    darkFlatW += bgW;
                    if (pureFlat4)
                    {
                        hasPureFlatDarkBg = true;
                        float pw = 1.0f / (1.0f + 0.35f * distSq);
                        darkPureSum += c * pw;
                        darkPureW   += pw;
                    }
                    if (anchorScore > bestDarkScore)
                    {
                        bestDarkScore  = anchorScore;
                        bestDarkAnchor = c;
                    }
                }
                else if (u > 0.52f)
                {
                    lightFlatW += bgW;
                    if (pureFlat4)
                    {
                        hasPureFlatLightBg = true;
                        float pw = 1.0f / (1.0f + 0.35f * distSq);
                        lightPureSum += c * pw;
                        lightPureW   += pw;
                    }
                    if (anchorScore > bestLightScore)
                    {
                        bestLightScore  = anchorScore;
                        bestLightAnchor = c;
                    }
                }
            }

            float wdB = saturate(1.0f - 2.0f * um);
            float wlB = saturate(2.0f * um - 1.0f);
            darkBroadSum  += c * wdB;  darkBroadW  += wdB;
            lightBroadSum += c * wlB;  lightBroadW += wlB;

            lightShare += (u > 0.5f) ? 1.0f : 0.0f;

            float gm = c.g - 0.5f * (c.r + c.b);
            bool sandwichFringe = monoG && (abs(rb) > FringeChroma) && sandNbrs && (lStepH > 0.25f * wideC);
            bool edgeMonoFringe = monoG && (abs(rb) > FringeChroma) && edgeNbrs && (lStepH > 0.12f * wideC);

            if (sameCount >= 1.5f && dMax < 0.18f * wideC && sameHueH && !edgeMonoFringe && !sandwichFringe)
            {
                float inkScore = (sameCount >= 3.5f ? 3.0f : (sameCount >= 2.5f ? 1.8f : 1.0f)) / (1.0f + 1.5f * distSq);
                if (u < 0.35f && inkScore > bestDarkInkScore)
                {
                    bestDarkInkScore  = inkScore;
                    bestDarkInkAnchor = c;
                }
                else if (u > 0.65f && inkScore > bestLightInkScore)
                {
                    bestLightInkScore  = inkScore;
                    bestLightInkAnchor = c;
                }
            }
            if (edgeMonoFringe)
            {
                if (rb > 0.0f) winHasPosRb = true;
                else           winHasNegRb = true;
                edgeMonoCount += 1.0f;
            }
            float satSpread = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
            if (satSpread > 0.78f || (sameCount >= 2.0f && dMax < 0.14f && satSpread > 0.45f))
            {
                hasFlatSatColor = true;
            }
            if (sandwichFringe)
            {
                fringePairs += 1.0f;
                hasOppositeFringes = true;
            }
            if (x2 > -PoleRadius)
            {
                bool pair1  = (abs(rb) > FringeChroma && abs(prevRb) > FringeChroma && rb * prevRb < 0.0f);
                bool pair2  = (x2 > -PoleRadius + 1) && (abs(rb) > FringeChroma && abs(prev2Rb) > FringeChroma && rb * prev2Rb < 0.0f);
                bool stemGm = (abs(gm) > 0.16f && abs(rb) < 0.05f && (u < 0.35f || u > 0.65f));
                if (pair1 || pair2) hasOppositeFringes = true;
                fringePairs += (pair1 || pair2 || stemGm) ? 1.0f : 0.0f;
            }
            prev2Rb = prevRb;
            prevRb  = rb;
        }
    }
    if (winHasPosRb && winHasNegRb && edgeMonoCount > 0.0f)
    {
        fringePairs += edgeMonoCount;
        hasOppositeFringes = true;
    }

    // Tightly centre-weighted 3x3 Scharr structure tensor + central-difference ClearType order vote.
    // Reading directly from L0-cached PercepTex avoids 50-float VGPR scratch array spills on RDNA 2.
    float Jxx = 0.0f, Jxy = 0.0f, Jyy = 0.0f;
    float subpixelVote = 0.0f;
    [loop]
    for (int gy = -1; gy <= 1; ++gy)
    {
        [loop]
        for (int gx = -1; gx <= 1; ++gx)
        {
            int2 p = center + int2(gx, gy);
            float uTL = (dot(FetchPercep(p + int2(-1, -1)).rgb, MeanWeights) - mMin) * invM;
            float uTC = (dot(FetchPercep(p + int2( 0, -1)).rgb, MeanWeights) - mMin) * invM;
            float uTR = (dot(FetchPercep(p + int2( 1, -1)).rgb, MeanWeights) - mMin) * invM;
            float uML = (dot(FetchPercep(p + int2(-1,  0)).rgb, MeanWeights) - mMin) * invM;
            float3 cMC = FetchPercep(p).rgb;
            float uMR = (dot(FetchPercep(p + int2( 1,  0)).rgb, MeanWeights) - mMin) * invM;
            float uBL = (dot(FetchPercep(p + int2(-1,  1)).rgb, MeanWeights) - mMin) * invM;
            float uBC = (dot(FetchPercep(p + int2( 0,  1)).rgb, MeanWeights) - mMin) * invM;
            float uBR = (dot(FetchPercep(p + int2( 1,  1)).rgb, MeanWeights) - mMin) * invM;

            float dx = (3.0f * uTR + 10.0f * uMR + 3.0f * uBR) - (3.0f * uTL + 10.0f * uML + 3.0f * uBL);
            float dy = (3.0f * uBL + 10.0f * uBC + 3.0f * uBR) - (3.0f * uTL + 10.0f * uTC + 3.0f * uTR);
            float w  = (gx == 0 && gy == 0) ? 4.0f : ((gx == 0 || gy == 0) ? 1.5f : 0.5f);
            Jxx += w * dx * dx;
            Jxy += w * dx * dy;
            Jyy += w * dy * dy;

            float dmCenter = uMR - uML;
            float uMC      = (dot(cMC, MeanWeights) - mMin) * invM;
            float rbBase   = lerp(cDarkest.r - cDarkest.b, cLightest.r - cLightest.b, uMC);
            float rbHere   = (cMC.r - cMC.b) - rbBase;
            if (abs(dmCenter) > 0.10f && abs(rbHere) > FringeChroma)
            {
                subpixelVote += clamp(rbHere * dmCenter * 16.0f, -1.0f, 1.0f);
            }
        }
    }

    if (!hasOppositeFringes && edgeMonoCount >= 3.0f && abs(subpixelVote) >= 1.8f)
    {
        fringePairs += edgeMonoCount;
    }

    float trace = Jxx + Jyy;
    float diff  = Jxx - Jyy;
    float disc  = sqrt(diff * diff + 4.0f * Jxy * Jxy);
    float coh   = (trace > 1e-4f) ? saturate(disc / (trace + 1e-4f)) : 0.0f;
    float2 doubleTang = (disc > 1e-5f) ? (-float2(diff, 2.0f * Jxy) / disc) * coh : float2(0.0f, 0.0f);

    const float window = (2 * PoleRadius + 1) * (2 * PoleRadius + 1);
    float  neutralize  = smoothstep(FringeNeutralLo, FringeNeutralHi, fringePairs / window);
    float  lightFrac   = lightShare / window;

    // Far-offset probe: 1 MetaTex fetch per offset instead of 5 HDR fetches!
    float flatTotal  = darkFlatW + lightFlatW;
    float polDarkW   = darkFlatW;
    float polLightW  = lightFlatW;
    if (!hasPureFlatDarkBg || !hasPureFlatLightBg || flatTotal < 0.35f)
    {
        bool missingPolarity = (darkFlatW < 0.25f || lightFlatW < 0.25f);
        static const int2 FarOffsets[16] = {
            int2( 0, -5), int2( 0,  5),
            int2(-5,  0), int2( 5,  0),
            int2( 0, -9), int2( 0,  9),
            int2(-9,  0), int2( 9,  0),
            int2(-7, -7), int2( 7,  7),
            int2(-7,  7), int2( 7, -7),
            int2( 0,-15), int2( 0, 15),
            int2(-20, 0), int2(20,  0)
        };
        [loop]
        for (int p = 0; p < 16; ++p)
        {
            int2   pt     = center + FarOffsets[p];
            float4 metaPt = FetchMeta(pt);
            if (metaPt.r < 0.022f)
            {
                float4 cf4 = FetchPercep(pt);
                float  uf  = (cf4.a - lMin) * invL;
                float  fDistSq = (float)(FarOffsets[p].x * FarOffsets[p].x + FarOffsets[p].y * FarOffsets[p].y);
                float  fPolW   = (missingPolarity && metaPt.g >= 0.85f)
                    ? (1.50f / (1.0f + 0.002f * fDistSq))
                    : (0.65f / (1.0f + 0.10f * fDistSq));
                float  fScore  = 1.80f / (1.0f + 0.02f * fDistSq);
                if (uf < 0.48f)
                {
                    polDarkW += fPolW;
                    float fpw = 1.0f / (1.0f + 0.35f * fDistSq);
                    darkPureSum += cf4.rgb * fpw;
                    darkPureW   += fpw;
                    if (fScore > bestDarkScore)
                    {
                        bestDarkScore  = fScore;
                        bestDarkAnchor = cf4.rgb;
                    }
                }
                else if (uf > 0.52f)
                {
                    polLightW += fPolW;
                    float fpw = 1.0f / (1.0f + 0.35f * fDistSq);
                    lightPureSum += cf4.rgb * fpw;
                    lightPureW   += fpw;
                    if (fScore > bestLightScore)
                    {
                        bestLightScore  = fScore;
                        bestLightAnchor = cf4.rgb;
                    }
                }
            }
        }
    }

    float polTotal     = polDarkW + polLightW;
    float bgLightShare = (polTotal > 0.01f) ? (polLightW / polTotal) : lightFrac;
    float darkIsInk    = smoothstep(0.47f, 0.53f, bgLightShare);

    float3 darkInkAvg  = darkBroadSum  / max(darkBroadW,  1e-4f);
    float3 lightInkAvg = lightBroadSum / max(lightBroadW, 1e-4f);

    float3 darkBgExact  = (bestDarkScore  > 0.0f) ? bestDarkAnchor  : darkInkAvg;
    float3 lightBgExact = (bestLightScore > 0.0f) ? bestLightAnchor : lightInkAvg;

    // On smooth gradient backgrounds (e.g. browser tabs, titlebars, ribbons), smoothly blend across
    // nearby pure-flat background pixels so there is no abrupt step/box behind letters, while still
    // snapping cleanly to bestAnchor whenever two distinct solid UI panels (> 0.14 apart) meet.
    if (darkPureW > 0.0f && bestDarkScore > 0.0f)
    {
        float3 darkPureAvg = darkPureSum / darkPureW;
        float  gradBlend   = 1.0f - smoothstep(0.09f, 0.16f, length(darkPureAvg - bestDarkAnchor));
        darkBgExact = lerp(bestDarkAnchor, darkPureAvg, gradBlend);
    }
    if (lightPureW > 0.0f && bestLightScore > 0.0f)
    {
        float3 lightPureAvg = lightPureSum / lightPureW;
        float  gradBlend    = 1.0f - smoothstep(0.09f, 0.16f, length(lightPureAvg - bestLightAnchor));
        lightBgExact = lerp(bestLightAnchor, lightPureAvg, gradBlend);
    }

    float darkChroma  = length(darkInkAvg  - dot(darkInkAvg,  LumaWeights));
    float lightChroma = length(lightInkAvg - dot(lightInkAvg, LumaWeights));
    float c7MinMax    = max(c7Min.r, max(c7Min.g, c7Min.b));
    float c7DarkSpread  = c7MinMax - min(c7Min.r, min(c7Min.g, c7Min.b));
    float c7LightSpread = max(c7Max.r, max(c7Max.g, c7Max.b)) - min(c7Max.r, min(c7Max.g, c7Max.b));
    bool  c7IsColoredDarkInk = (c7MinMax > 0.35f && c7DarkSpread > 0.18f);

    float darkIsNeutralInk  = neutralize * darkIsInk * (1.0f - smoothstep(TintChromaLo, TintChromaHi, darkChroma)) * (c7IsColoredDarkInk ? 0.0f : 1.0f);
    float lightIsNeutralInk = neutralize * (1.0f - darkIsInk) * (1.0f - smoothstep(TintChromaLo, TintChromaHi, lightChroma));

    float darkRbDiff  = abs(darkInkAvg.r  - darkInkAvg.b);
    float lightRbDiff = abs(lightInkAvg.r - lightInkAvg.b);
    bool  hasColoredDarkCore  = (bestDarkInkScore  > 0.0f) && (length(bestDarkInkAnchor  - dot(bestDarkInkAnchor,  LumaWeights)) > 0.14f);
    bool  hasColoredLightCore = (bestLightInkScore > 0.0f) && (length(bestLightInkAnchor - dot(bestLightInkAnchor, LumaWeights)) > 0.14f);
    bool   singleEdgeCt      = (edgeMonoCount >= 3.0f && abs(subpixelVote) >= 1.8f);
    if (darkIsInk > 0.5f && neutralize > 0.20f && (hasOppositeFringes || singleEdgeCt) && !hasColoredDarkCore && !c7IsColoredDarkInk)
    {
        float coreMax = max(cDarkest.r, max(cDarkest.g, cDarkest.b));
        bool  coreMono = (cDarkest.g >= min(cDarkest.r, cDarkest.b) - 0.05f) &&
                         (cDarkest.g <= max(cDarkest.r, cDarkest.b) + 0.05f);
        if ((darkRbDiff < 0.24f && darkChroma < 0.20f && darkInkAvg.g <= max(darkInkAvg.r, darkInkAvg.b) + 0.03f) ||
            (chanMin < 0.16f && lMin < 0.16f && darkChroma < 0.16f && (coreMax < 0.72f || coreMono)))
        {
            darkIsNeutralInk = 1.0f;
        }
    }
    if (darkIsInk > 0.5f && darkChroma < 0.12f && c7MinMax < 0.14f && edgeMonoCount > 0.0f && !hasColoredDarkCore && !c7IsColoredDarkInk)
    {
        darkIsNeutralInk = 1.0f;
    }
    if (darkIsInk < 0.5f && neutralize > 0.20f && (hasOppositeFringes || singleEdgeCt) && !hasColoredLightCore)
    {
        float coreMin = min(cLightest.r, min(cLightest.g, cLightest.b));
        if ((lightRbDiff < 0.20f && lightChroma < 0.16f && lightInkAvg.g >= min(lightInkAvg.r, lightInkAvg.b) - 0.03f) ||
            (chanMax > 0.88f && coreMin > 0.68f && lightChroma < 0.14f) ||
            (chanMax > 0.86f && lightRbDiff < 0.26f && lightChroma < 0.18f && abs(subpixelVote) > 0.60f))
        {
            lightIsNeutralInk = 1.0f;
        }
    }
    if (hasFlatSatColor)
    {
        darkIsNeutralInk  = 0.0f;
        lightIsNeutralInk = 0.0f;
    }

    float targetDarkL  = lerp(lMin, min(lMin, chanMin), darkIsNeutralInk  * (darkRbDiff  < 0.03f ? 1.0f : 0.0f));
    float targetLightL = lerp(lMax, max(lMax, chanMax), lightIsNeutralInk * (lightRbDiff < 0.03f ? 1.0f : 0.0f));

    float3 inkDarkColor  = lerp(MakePole(darkInkAvg,  targetDarkL,  neutralize),
                                max(float3(targetDarkL, targetDarkL, targetDarkL), 0.0f),
                                darkIsNeutralInk);
    float3 inkLightColor = lerp(MakePole(lightInkAvg, targetLightL, neutralize),
                                max(float3(targetLightL, targetLightL, targetLightL), 0.0f),
                                lightIsNeutralInk);

    if (bestDarkInkScore > 0.0f)
    {
        float inkL = dot(bestDarkInkAnchor, LumaWeights);
        float inkAnchorChroma = length(bestDarkInkAnchor - inkL);
        if (darkIsNeutralInk < 0.5f || inkAnchorChroma < 0.04f)
        {
            inkDarkColor = lerp(MakePole(bestDarkInkAnchor, inkL, neutralize),
                                max(float3(inkL, inkL, inkL), 0.0f),
                                darkIsNeutralInk);
        }
        else
        {
            inkDarkColor = bestDarkInkAnchor;
        }
    }
    if (bestLightInkScore > 0.0f)
    {
        float inkL = dot(bestLightInkAnchor, LumaWeights);
        float inkAnchorChroma = length(bestLightInkAnchor - inkL);
        if (lightIsNeutralInk < 0.5f || inkAnchorChroma < 0.04f)
        {
            inkLightColor = lerp(MakePole(bestLightInkAnchor, inkL, neutralize),
                                 max(float3(inkL, inkL, inkL), 0.0f),
                                 lightIsNeutralInk);
        }
        else
        {
            inkLightColor = bestLightInkAnchor;
        }
    }

    float3 finalDark  = lerp(darkBgExact,  inkDarkColor,  darkIsInk);
    float3 finalLight = lerp(inkLightColor, lightBgExact, darkIsInk);

    // Re-evaluate two-tone confidence directly from L0-cached PercepTex.a (zero scratch arrays!).
    float finalDarkL  = dot(finalDark,  LumaWeights);
    float finalLightL = dot(finalLight, LumaWeights);
    float invFinalC   = 1.0f / max(finalLightL - finalDarkL, 1e-3f);
    float nearPole    = 0.0f;
    float exactPole   = 0.0f;
    [loop]
    for (int ky = -PoleRadius; ky <= PoleRadius; ++ky)
    {
        [loop]
        for (int kx = -PoleRadius; kx <= PoleRadius; ++kx)
        {
            float lk = FetchPercep(center + int2(kx, ky)).a;
            float uk = saturate((lk - finalDarkL) * invFinalC);
            nearPole += (uk < 0.25f || uk > 0.75f) ? 1.0f : 0.0f;
            bool onDark  = (lk <= finalDarkL  + ExactPoleTol);
            bool onLight = (lk >= finalLightL - ExactPoleTol);
            exactPole += (onDark || onLight) ? 1.0f : 0.0f;
        }
    }

    float ctBonus    = (hasOppositeFringes && abs(subpixelVote) > 0.45f) ? 3.0f : 0.0f;
    float score      = (nearPole + exactPole + FringeBonus * fringePairs + ctBonus) / window;
    float isNeutralPole = max(darkIsNeutralInk, lightIsNeutralInk);
    float ctStrength = saturate(abs(subpixelVote) * 0.6f) * neutralize * isNeutralPole;
    float ctDir      = (subpixelVote < -0.35f) ? 1.0f : (subpixelVote > 0.35f ? -1.0f : 0.0f);
    float ctEncoded  = 0.5f + 0.5f * ctDir * ctStrength;
    float twoTone    = smoothstep(TwoToneLo, TwoToneHi, score);

    float3 ctDarkPole   = (darkIsInk > 0.5f) ? c7Min : darkBgExact;
    float3 ctLightPole  = (darkIsInk > 0.5f) ? lightBgExact : c7Max;
    float3 bgPoleExact   = (darkIsInk > 0.5f) ? lightBgExact : darkBgExact;
    float3 inkPoleExact  = (darkIsInk > 0.5f) ? inkDarkColor : inkLightColor;
    float  bgPoleChroma  = length(bgPoleExact  - dot(bgPoleExact,  LumaWeights));
    float  inkPoleChroma = length(inkPoleExact - dot(inkPoleExact, LumaWeights));
    float3 finalSpan    = ctLightPole - ctDarkPole;
    float3 cW           = smoothstep(0.16f, 0.28f, abs(finalSpan));
    float  strongChans  = cW.r + cW.g + cW.b;

    bool colorCtDetected = false;
    if (isNeutralPole < 0.5f && strongChans >= 1.85f && twoTone > 0.45f &&
        bgPoleChroma < 0.28f && (hasPureFlatDarkBg || hasPureFlatLightBg) &&
        !(hasFlatSatColor && inkPoleChroma < 0.16f))
    {
        float3 invFinalSpan = float3(
            abs(finalSpan.r) > 0.16f ? 1.0f / finalSpan.r : 0.0f,
            abs(finalSpan.g) > 0.16f ? 1.0f / finalSpan.g : 0.0f,
            abs(finalSpan.b) > 0.16f ? 1.0f / finalSpan.b : 0.0f);
        float finalLen2 = max(dot(finalSpan, finalSpan), 1e-5f);
        float wRB = cW.r * cW.b;
        float wRG = cW.r * cW.g * 1.4f;
        float wGB = cW.g * cW.b * 1.4f;
        float pairSum = max(wRB + wRG + wGB, 1e-4f);

        float colorCtVote = 0.0f;
        float colorNeg    = 0.0f;
        float colorPos    = 0.0f;
        [loop]
        for (int cy = -1; cy <= 1; ++cy)
        {
            [loop]
            for (int cx = -2; cx <= 2; ++cx)
            {
                int2   cp = center + int2(cx, cy);
                float3 cL = FetchPercep(cp + int2(-1, 0)).rgb;
                float3 cM = FetchPercep(cp).rgb;
                float3 cR = FetchPercep(cp + int2(1, 0)).rgb;
                float  du = saturate(dot(cR - ctDarkPole, finalSpan) / finalLen2)
                          - saturate(dot(cL - ctDarkPole, finalSpan) / finalLen2);
                if (abs(du) > 0.14f)
                {
                    float uR = saturate((cM.r - ctDarkPole.r) * invFinalSpan.r);
                    float uG = saturate((cM.g - ctDarkPole.g) * invFinalSpan.g);
                    float uB = saturate((cM.b - ctDarkPole.b) * invFinalSpan.b);
                    float phase = (wRB * (uB - uR) + wRG * (uG - uR) + wGB * (uB - uG)) / pairSum;
                    if (abs(phase) > 0.045f)
                    {
                        float vote = clamp(-phase * du * 14.0f, -1.0f, 1.0f);
                        colorCtVote += vote;
                        if (vote < 0.0f) colorNeg += 1.0f;
                        else             colorPos += 1.0f;
                    }
                }
            }
        }

        float colorTot   = colorNeg + colorPos;
        float colorAgree = (colorTot > 0.0f) ? (max(colorNeg, colorPos) / colorTot) : 0.0f;
        if (colorTot >= 2.0f && colorAgree >= 0.72f && abs(colorCtVote) > 0.45f)
        {
            colorCtDetected = true;
            float ctWeight = smoothstep(0.45f, 0.65f, twoTone);
            if (darkIsInk > 0.5f) finalDark  = lerp(finalDark,  c7Min, ctWeight);
            else                  finalLight = lerp(finalLight, c7Max, ctWeight);
            ctStrength = saturate(abs(colorCtVote) * 0.55f) * ctWeight;
            ctDir      = (colorCtVote < -0.45f) ? 1.0f : -1.0f;
            ctEncoded  = 0.5f + 0.5f * ctDir * ctStrength;
        }
    }

    if (!colorCtDetected && isNeutralPole < 0.5f && neutralize > 0.15f && twoTone > 0.50f)
    {
        if (darkIsInk > 0.5f && c7DarkSpread > 0.14f)
        {
            finalDark = lerp(finalDark, c7Min, 0.90f);
        }
        else if (darkIsInk < 0.5f && c7LightSpread > 0.14f)
        {
            finalLight = lerp(finalLight, c7Max, 0.90f);
        }
    }

    PoleOut o;
    o.Dark     = float4(finalDark,  twoTone);
    o.Light    = float4(finalLight, bgLightShare);
    o.LocalExt = float4(doubleTang * 0.5f + 0.5f, ctEncoded, saturate(localC));
    return o;
}

// Projects 2x2 texels onto the true 3D RGB pole vector (wideLight - wideDark) instead of Rec.709
// luma alone, giving coloured text (blue links, red/purple/orange/green syntax highlighting) full
// signal-to-noise ratio across all active RGB channels + ink-connected diagonal triangulation
// (guarded against false horizontal-bar-to-neighbour inter-letter bridges).
float SampleInkAt(int2 coord, float3 wideDark, float3 spanRgb, float invSegLen2, float darkInk)
{
    float u = saturate(dot(FetchPercep(coord).rgb - wideDark, spanRgb) * invSegLen2);
    return lerp(u, 1.0f - u, darkInk);
}

float SampleGrayConnected(float2 pos, float3 wideDark, float3 spanRgb, float invSegLen2, float darkInk)
{
    float2 p  = pos - 0.5f;
    int2   i0 = int2(floor(p));
    float2 f  = p - floor(p);

    float ua = saturate(dot(FetchPercep(i0 + int2(0, 0)).rgb - wideDark, spanRgb) * invSegLen2);
    float ub = saturate(dot(FetchPercep(i0 + int2(1, 0)).rgb - wideDark, spanRgb) * invSegLen2);
    float uc = saturate(dot(FetchPercep(i0 + int2(0, 1)).rgb - wideDark, spanRgb) * invSegLen2);
    float ud = saturate(dot(FetchPercep(i0 + int2(1, 1)).rgb - wideDark, spanRgb) * invSegLen2);

    float uBilin = lerp(lerp(ua, ub, f.x), lerp(uc, ud, f.x), f.y);

    float ka = lerp(ua, 1.0f - ua, darkInk);
    float kb = lerp(ub, 1.0f - ub, darkInk);
    float kc = lerp(uc, 1.0f - uc, darkInk);
    float kd = lerp(ud, 1.0f - ud, darkInk);

    // Require both diagonal endpoints to be genuine ink (not a faint AA halo in an inter-letter gap).
    // Dark-on-light tiny 6-8pt strokes (darkInk=1) have softer downsampled diagonal peaks, so allow 0.28.
    float inkMin   = lerp(0.48f, 0.28f, darkInk);
    float mainDiag = min(ka, kd) - max(kb, kc);
    float antiDiag = min(kb, kc) - max(ka, kd);

    if (mainDiag > 0.02f && min(ka, kd) > inkMin)
    {
        // Guard against bridging an isolated horizontal bar tip (e.g. 't' crossbar right tip) to an
        // adjacent letter ('o' / 'r') across a diagonal gap: a real diagonal stroke does not start
        // from a pure horizontal stub with empty space both above and diagonally behind it.
        bool aHorizTip = (SampleInkAt(i0 + int2(-1, 0), wideDark, spanRgb, invSegLen2, darkInk) > 0.65f)
                      && (SampleInkAt(i0 + int2( 0,-1), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f)
                      && (SampleInkAt(i0 + int2(-1,-1), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f);
        bool dHorizTip = (SampleInkAt(i0 + int2( 2, 1), wideDark, spanRgb, invSegLen2, darkInk) > 0.65f)
                      && (SampleInkAt(i0 + int2( 1, 2), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f)
                      && (SampleInkAt(i0 + int2( 2, 2), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f);
        if (!aHorizTip && !dHorizTip)
        {
            float uTri = (f.x > f.y)
                ? ((1.0f - f.x) * ua + (f.x - f.y) * ub + f.y * ud)
                : ((1.0f - f.y) * ua + (f.y - f.x) * uc + f.x * ud);
            return lerp(uBilin, uTri, smoothstep(0.02f, 0.18f, mainDiag));
        }
    }
    if (antiDiag > 0.02f && min(kb, kc) > inkMin)
    {
        bool bHorizTip = (SampleInkAt(i0 + int2( 2, 0), wideDark, spanRgb, invSegLen2, darkInk) > 0.65f)
                      && (SampleInkAt(i0 + int2( 1,-1), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f)
                      && (SampleInkAt(i0 + int2( 2,-1), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f);
        bool cHorizTip = (SampleInkAt(i0 + int2(-1, 1), wideDark, spanRgb, invSegLen2, darkInk) > 0.65f)
                      && (SampleInkAt(i0 + int2( 0, 2), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f)
                      && (SampleInkAt(i0 + int2(-1, 2), wideDark, spanRgb, invSegLen2, darkInk) < 0.35f);
        if (!bHorizTip && !cHorizTip)
        {
            float uTri = (f.x + f.y < 1.0f)
                ? ((1.0f - f.x - f.y) * ua + f.x * ub + f.y * uc)
                : ((1.0f - f.y) * ub + (1.0f - f.x) * uc + (f.x + f.y - 1.0f) * ud);
            return lerp(uBilin, uTri, smoothstep(0.02f, 0.18f, antiDiag));
        }
    }
    return uBilin;
}

// Zero-transcendental half-angle decoder from encoded double-angle vector in LocalExtTex.rg.
float2 DecodeDoubleTangent(float2 v, out float coh)
{
    float len2 = dot(v, v);
    coh = saturate(sqrt(len2));
    if (len2 <= 1e-6f)
    {
        return float2(1.0f, 0.0f);
    }
    float2 u = v * rsqrt(len2);
    float cx = sqrt(max(0.5f * (1.0f + u.x), 0.0f));
    float sy = sqrt(max(0.5f * (1.0f - u.x), 0.0f));
    return float2(cx, (u.y < 0.0f) ? -sy : sy);
}

float SampleCoverageFast(
    float2 pos,
    float2 invDesktop,
    float3 wideDark,
    float3 spanRgb,
    float3 safeInvSpan,
    float3 chanW,
    float  invSegLen2,
    float  ctDir,
    float  useSubpixel,
    float  darkInk)
{
    if (useSubpixel > 0.5f)
    {
        float x0 = floor(pos.x - 0.5f);
        float fx = (pos.x - 0.5f) - x0;

        float3 c0 = PercepTex.SampleLevel(LinearSamp, float2(x0 + 0.5f, pos.y) * invDesktop, 0).rgb;
        float3 c1 = PercepTex.SampleLevel(LinearSamp, float2(x0 + 1.5f, pos.y) * invDesktop, 0).rgb;

        float3 u0 = saturate((c0 - wideDark) * safeInvSpan);
        float3 u1 = saturate((c1 - wideDark) * safeInvSpan);
        float3 w  = chanW;

        if (ctDir < 0.0f)
        {
            u0 = u0.bgr;
            u1 = u1.bgr;
            w  = w.bgr;
        }

        float s0 = u0.y;
        float s1 = u0.z;
        float s2 = u1.x;
        float s3 = u1.y;
        if (min(w.x, min(w.y, w.z)) < 0.99f)
        {
            s0 = lerp(0.5f * (u0.x + u0.z), s0, w.y);
            s3 = lerp(0.5f * (u1.x + u1.z), s3, w.y);
            s1 = lerp(0.5f * (s0 + u1.x),   s1, w.z);
            s2 = lerp(0.5f * (s1 + s3),     s2, w.x);
        }

        float s = fx * 3.0f;
        if (s < 1.0f)      return lerp(s0, s1, s);
        else if (s < 2.0f) return lerp(s1, s2, s - 1.0f);
        else               return lerp(s2, s3, s - 2.0f);
    }
    else
    {
        return SampleGrayConnected(pos, wideDark, spanRgb, invSegLen2, darkInk);
    }
}

// Pass 1b (half desktop resolution, cropped): Precomputes the GPU Photo/Video Bounding Mask once
// per desktop frame when MediaMode == 0, eliminating 32 texture taps/pixel from full-screen PSMain.
float2 MediaMaskPS(VSOut input) : SV_TARGET
{
    float2 uv = input.Uv;
    float  localC = LocalExtTex.SampleLevel(LinearSamp, uv, 0).a;
    if (localC < 0.008f)
    {
        return float2(0.0f, 0.0f);
    }

    float3 wideDark  = PoleDarkTex.SampleLevel(LinearSamp, uv, 0).rgb;
    float3 wideLight = PoleLightTex.SampleLevel(LinearSamp, uv, 0).rgb;
    float2 world      = uv * DesktopSize;
    float2 invDesktop = 1.0f / DesktopSize;

    static const float2 BoxOff[8] = {
        float2(-7.0f,  0.0f), float2( 7.0f,  0.0f),
        float2( 0.0f, -7.0f), float2( 0.0f,  7.0f),
        float2(-16.0f,-11.0f), float2(16.0f,-11.0f),
        float2(-16.0f, 11.0f), float2(16.0f, 11.0f)
    };
    float photoHits = 0.0f;
    float textHits  = 0.0f;
    float flatHits  = 0.0f;
    float poleShift = 0.0f;
    [unroll]
    for (int k = 0; k < 8; ++k)
    {
        float2 buv   = (world + BoxOff[k]) * invDesktop;
        float4 bD    = PoleDarkTex.SampleLevel(LinearSamp, buv, 0);
        float4 bL    = PoleLightTex.SampleLevel(LinearSamp, buv, 0);
        float  bLC   = LocalExtTex.SampleLevel(LinearSamp, buv, 0).a;
        float  bFlat = (MetaTex.SampleLevel(LinearSamp, buv, 0).r < 0.024f) ? 1.0f : 0.0f;
        float  act   = smoothstep(0.015f, 0.050f, bLC);
        float  nonFlat = smoothstep(0.01f, 0.06f, bD.a);
        photoHits += act * nonFlat * (1.0f - smoothstep(0.28f, 0.54f, bD.a));
        textHits  += act * smoothstep(0.56f, 0.80f, bD.a);
        flatHits  += bFlat;
        float dDark  = length(bD.rgb - wideDark);
        float dLight = length(bL.rgb - wideLight);
        poleShift += act * nonFlat * min(dDark, dLight);
    }
    float boxPhoto   = smoothstep(0.9f, 2.6f, photoHits - textHits * 0.65f - flatHits * 0.25f);
    float shiftPhoto = smoothstep(0.12f, 0.35f, poleShift) * smoothstep(0.2f, 1.2f, photoHits - textHits * 0.70f - flatHits * 0.25f);
    float mediaBase  = saturate(max(boxPhoto, shiftPhoto)) * (1.0f - smoothstep(2.5f, 4.5f, flatHits));
    return float2(mediaBase, boxPhoto);
}

float3 ComputeMagnifiedRgb(float2 screenPos)
{
    float2 world      = (screenPos - ScreenSize * 0.5f) / Zoom + Focus;
    float2 invDesktop = 1.0f / DesktopSize;
    if (Zoom <= 1.05f)
    {
        return ToOutput(ToPerceptual(DesktopTex.SampleLevel(LinearSamp, world * invDesktop, 0).rgb));
    }

    float2 poleUv     = world * invDesktop;
    float4 wideDarkA  = PoleDarkTex.SampleLevel(LinearSamp, poleUv, 0);
    float4 wideLightA = PoleLightTex.SampleLevel(LinearSamp, poleUv, 0);
    float4 localInfo  = LocalExtTex.SampleLevel(LinearSamp, poleUv, 0);

    float  localC    = localInfo.a;
    // Ultra-fast flat-pixel early-out (desktop background, icon fills, flat UI cards):
    // 1 hardware 4-byte tap from PercepTex, exact sRGB colour, zero HDR pow() overhead!
    if (localC < 0.008f)
    {
        return ToOutput(PercepTex.SampleLevel(LinearSamp, poleUv, 0).rgb);
    }

    float3 wideDark  = wideDarkA.rgb;
    float3 wideLight = wideLightA.rgb;
    float  twoTone   = wideDarkA.a;
    float  lWideLo   = dot(wideDark,  LumaWeights);
    float  lWideHi   = dot(wideLight, LumaWeights);
    float  wideC     = lWideHi - lWideLo;

    float invWideC   = 1.0f / max(wideC, 1e-3f);
    float3 spanRgb   = wideLight - wideDark;
    float  segLen2   = max(dot(spanRgb, spanRgb), 1e-5f);
    float  invSegLen2 = 1.0f / segLen2;
    float minChanC     = min(spanRgb.r, min(spanRgb.g, spanRgb.b));
    float maxChanC     = max(spanRgb.r, max(spanRgb.g, spanRgb.b));
    float3 absSpan     = abs(spanRgb);
    float3 chanW       = smoothstep(0.16f, 0.28f, absSpan);
    float  chanSum     = chanW.r + chanW.g + chanW.b;
    float3 safeInvSpan = float3(
        absSpan.r > 0.16f ? 1.0f / spanRgb.r : 0.0f,
        absSpan.g > 0.16f ? 1.0f / spanRgb.g : 0.0f,
        absSpan.b > 0.16f ? 1.0f / spanRgb.b : 0.0f);

    float ctSigned      = localInfo.b * 2.0f - 1.0f;
    bool  isNeutralSpan = (minChanC > 0.15f && minChanC > 0.70f * maxChanC);
    float useSubpixel   = (abs(ctSigned) > 0.25f && (isNeutralSpan || chanSum >= 1.85f)) ? 1.0f : 0.0f;
    float ctDir         = (ctSigned >= 0.0f) ? 1.0f : -1.0f;

    float2 v         = localInfo.rg * 2.0f - 1.0f;
    float  coherence;
    float2 tang      = DecodeDoubleTangent(v, coherence);
    float  diagFactor = sqrt(abs(2.0f * tang.x * tang.y));

    float2 p2    = world - 0.5f;
    float2 cell2 = floor(p2);
    float2 f2    = p2 - cell2;
    int2   i02   = int2(cell2);
    float4 c00_4 = FetchPercep(i02 + int2(0, 0));
    float4 c10_4 = FetchPercep(i02 + int2(1, 0));
    float4 c01_4 = FetchPercep(i02 + int2(0, 1));
    float4 c11_4 = FetchPercep(i02 + int2(1, 1));
    float3 c00 = c00_4.rgb;
    float3 c10 = c10_4.rgb;
    float3 c01 = c01_4.rgb;
    float3 c11 = c11_4.rgb;

    float onSegment = 1.0f;
    if (abs(ctSigned) <= 0.08f)
    {
        // Allow ClearType LCD subpixel sharpening undershoot (e.g. G or B dipping to 0 on coloured
        // text over dark backgrounds like blue GitHub links) while keeping solid 3rd-colour icons
        // outside [boxLo, boxHi] or caught by solidOffSegH.
        float3 boxLo   = min(wideDark, wideLight) - 0.16f;
        float3 boxHi   = max(wideDark, wideLight) + 0.05f;

        float3 p00 = wideDark + spanRgb * (dot(c00 - wideDark, spanRgb) * invSegLen2);
        float3 p10 = wideDark + spanRgb * (dot(c10 - wideDark, spanRgb) * invSegLen2);
        float3 p01 = wideDark + spanRgb * (dot(c01 - wideDark, spanRgb) * invSegLen2);
        float3 p11 = wideDark + spanRgb * (dot(c11 - wideDark, spanRgb) * invSegLen2);

        float3 r00 = c00 - p00;
        float3 r10 = c10 - p10;
        float3 r01 = c01 - p01;
        float3 r11 = c11 - p11;

        float lenR00 = length(r00), lenR10 = length(r10), lenR01 = length(r01), lenR11 = length(r11);
        bool allowSolidBox = (minChanC > 0.55f * maxChanC);
        bool solidOffSegH = (dot(r00, r10) > 0.05f && min(lenR00, lenR10) > 0.22f)
                         || (dot(r01, r11) > 0.05f && min(lenR01, lenR11) > 0.22f);

        bool inBox00 = all(c00 >= boxLo) && all(c00 <= boxHi);
        bool inBox10 = all(c10 >= boxLo) && all(c10 <= boxHi);
        bool inBox01 = all(c01 >= boxLo) && all(c01 <= boxHi);
        bool inBox11 = all(c11 >= boxLo) && all(c11 <= boxHi);
        bool neut00  = (max(c00.r, max(c00.g, c00.b)) - min(c00.r, min(c00.g, c00.b))) < 0.06f;
        bool neut10  = (max(c10.r, max(c10.g, c10.b)) - min(c10.r, min(c10.g, c10.b))) < 0.06f;
        bool neut01  = (max(c01.r, max(c01.g, c01.b)) - min(c01.r, min(c01.g, c01.b))) < 0.06f;
        bool neut11  = (max(c11.r, max(c11.g, c11.b)) - min(c11.r, min(c11.g, c11.b))) < 0.06f;
        bool mono00  = (c00.g >= min(c00.r, c00.b) - 0.04f) && (c00.g <= max(c00.r, c00.b) + 0.04f);
        bool mono10  = (c10.g >= min(c10.r, c10.b) - 0.04f) && (c10.g <= max(c10.r, c10.b) + 0.04f);
        bool mono01  = (c01.g >= min(c01.r, c01.b) - 0.04f) && (c01.g <= max(c01.r, c01.b) + 0.04f);
        bool mono11  = (c11.g >= min(c11.r, c11.b) - 0.04f) && (c11.g <= max(c11.r, c11.b) + 0.04f);

        bool ct00 = (!solidOffSegH && (inBox00 || neut00 || mono00)) || (!allowSolidBox && inBox00);
        bool ct10 = (!solidOffSegH && (inBox10 || neut10 || mono10)) || (!allowSolidBox && inBox10);
        bool ct01 = (!solidOffSegH && (inBox01 || neut01 || mono01)) || (!allowSolidBox && inBox01);
        bool ct11 = (!solidOffSegH && (inBox11 || neut11 || mono11)) || (!allowSolidBox && inBox11);

        float e00 = ct00 ? 0.0f : length(r00);
        float e10 = ct10 ? 0.0f : length(r10);
        float e01 = ct01 ? 0.0f : length(r01);
        float e11 = ct11 ? 0.0f : length(r11);

        float maxSegErr = max(max(e00, e10), max(e01, e11));
        onSegment = 1.0f - smoothstep(0.08f, 0.18f, maxSegErr);

        // BiBox / Word 3-colour highlighter & grammar-underline fallback: when text crosses a yellow/green/blue
        // highlighter or blue/red squiggle boundary, the 7x7 poles span text->bg while the 2x2 cell spans
        // text->underline (or underline->bg). If the 2x2 corners cleanly lie on their local min->max segment
        // and one pole matches a wide pole, switch to the local 2x2 poles so text & underlines stay sharp.
        if (maxSegErr > 0.08f && twoTone > 0.25f)
        {
            float3 cLo2 = c00; float lLo2 = c00_4.a;
            if (c10_4.a < lLo2) { cLo2 = c10; lLo2 = c10_4.a; }
            if (c01_4.a < lLo2) { cLo2 = c01; lLo2 = c01_4.a; }
            if (c11_4.a < lLo2) { cLo2 = c11; lLo2 = c11_4.a; }

            float3 cHi2 = c00; float lHi2 = c00_4.a;
            if (c10_4.a > lHi2) { cHi2 = c10; lHi2 = c10_4.a; }
            if (c01_4.a > lHi2) { cHi2 = c01; lHi2 = c01_4.a; }
            if (c11_4.a > lHi2) { cHi2 = c11; lHi2 = c11_4.a; }

            float3 locSpan = cHi2 - cLo2;
            float  locLen2 = max(dot(locSpan, locSpan), 1e-5f);
            float3 le00 = c00 - (cLo2 + locSpan * saturate(dot(c00 - cLo2, locSpan) / locLen2));
            float3 le10 = c10 - (cLo2 + locSpan * saturate(dot(c10 - cLo2, locSpan) / locLen2));
            float3 le01 = c01 - (cLo2 + locSpan * saturate(dot(c01 - cLo2, locSpan) / locLen2));
            float3 le11 = c11 - (cLo2 + locSpan * saturate(dot(c11 - cLo2, locSpan) / locLen2));
            float  locErr = max(max(length(le00), length(le10)), max(length(le01), length(le11)));
            bool   poleMatches = (length(cLo2 - wideDark) < 0.10f) || (length(cHi2 - wideLight) < 0.10f);

            if (poleMatches && (lHi2 - lLo2) > 0.25f && locErr < 0.06f)
            {
                wideDark   = cLo2;
                wideLight  = cHi2;
                lWideLo    = lLo2;
                lWideHi    = lHi2;
                wideC      = lWideHi - lWideLo;
                invWideC   = 1.0f / max(wideC, 1e-3f);
                spanRgb    = locSpan;
                segLen2    = locLen2;
                invSegLen2 = 1.0f / segLen2;
                minChanC    = min(spanRgb.r, min(spanRgb.g, spanRgb.b));
                maxChanC    = max(spanRgb.r, max(spanRgb.g, spanRgb.b));
                absSpan     = abs(spanRgb);
                chanW       = smoothstep(0.16f, 0.28f, absSpan);
                safeInvSpan = float3(
                    absSpan.r > 0.16f ? 1.0f / spanRgb.r : 0.0f,
                    absSpan.g > 0.16f ? 1.0f / spanRgb.g : 0.0f,
                    absSpan.b > 0.16f ? 1.0f / spanRgb.b : 0.0f);
                onSegment   = 1.0f;
            }
        }
    }

    // Use max(wideC, maxChanC) so saturated coloured text (e.g. pure blue or red where Rec.709 luma
    // contrast wideC is small even though RGB channel contrast maxChanC is high) AND low-contrast
    // fading tab text both receive full vector contour sharpening.
    float effectiveC = max(wideC, maxChanC * 0.65f);

    // GPU Photo/Video Bounding Mask ("mediaMask"):
    // Samples a ±7..18 px box in PoleDarkTex (dark pole + twoTone), PoleLightTex (light pole),
    // LocalExtTex (localC), and MetaTex (flatness) using standard D3D11 taps (AMD, NVIDIA, Intel).
    // MediaMode: 0=Auto, 1=AlwaysNative (Sharp-Bilinear for non-text), 2=Off.
    int   mediaMode = (int)(ExtraParams.x + 0.5f);
    float mediaMask = 0.0f;
    if (mediaMode == 1 && Zoom > 1.15f && abs(ctSigned) <= 0.25f)
    {
        float rawEdge = twoTone * onSegment * smoothstep(EdgeContrastLo, EdgeContrastHi, effectiveC);
        mediaMask = 1.0f - smoothstep(0.25f, 0.65f, rawEdge);
    }
    else if (mediaMode == 0 && Zoom > 1.15f)
    {
        float2 mm = MediaMaskTex.SampleLevel(LinearSamp, poleUv, 0).rg;
        if (abs(ctSigned) <= 0.25f)
        {
            // Preserve ultra-crisp high-contrast UI/subtitle edges if they have very strong twoTone
            // and high local contrast unless the surrounding box is overwhelmingly photo/video.
            float ultraCrispText = smoothstep(0.84f, 0.96f, twoTone) * smoothstep(0.45f, 0.70f, effectiveC);
            mediaMask = mm.r * (1.0f - ultraCrispText * (1.0f - mm.g));
        }
        else if (mm.r > 0.68f && mm.g > 0.42f)
        {
            // Inline multi-colour emoji/photo graphics can accidentally look like RGB ClearType along borders;
            // restore Sharp-Bilinear media protection so emojis never get vertical stripe artifacts.
            mediaMask = mm.r;
            useSubpixel = 0.0f;
        }
    }

    float edgeMask = twoTone
                   * onSegment
                   * smoothstep(EdgeContrastLo, EdgeContrastHi, effectiveC)
                   * (1.0f - mediaMask);

    float3 smoothRgb = 0.0f;
    if (edgeMask < 0.999f)
    {
        float3 inLo = min(min(c00, c10), min(c01, c11));
        float3 inHi = max(max(c00, c10), max(c01, c11));

        int2   base = i02 - 1;
        float4 wx = CatmullRom(f2.x);
        float4 wy = CatmullRom(f2.y);
        float3 cSum = 0.0f;
        [unroll]
        for (int y = 0; y < 4; ++y)
        {
            [unroll]
            for (int x = 0; x < 4; ++x)
            {
                float3 c = FetchPercep(base + int2(x, y)).rgb;
                cSum += c * (wx[x] * wy[y]);
            }
        }
        smoothRgb = clamp(cSum, inLo, inHi);

        float l00 = c00_4.a;
        float l10 = c10_4.a;
        float l01 = c01_4.a;
        float l11 = c11_4.a;
        float lLo = l00; float3 cLo = c00;
        if (l10 < lLo) { lLo = l10; cLo = c10; }
        if (l01 < lLo) { lLo = l01; cLo = c01; }
        if (l11 < lLo) { lLo = l11; cLo = c11; }
        float lHi = l00; float3 cHi = c00;
        if (l10 > lHi) { lHi = l10; cHi = c10; }
        if (l01 > lHi) { lHi = l01; cHi = c01; }
        if (l11 > lHi) { lHi = l11; cHi = c11; }
        float3 c2Diff = cHi - cLo;
        float  c2Len  = length(c2Diff);
        float  c2Span = lHi - lLo;

        if (coherence > 0.25f && c2Len > 0.04f && mediaMask < 0.95f)
        {
            float iconSpan = coherence * lerp(0.0f, 0.80f, diagFactor);
            float3 cT1 = PercepTex.SampleLevel(LinearSamp, world * invDesktop, 0).rgb;
            float3 cTang = cT1;
            if (iconSpan > 0.04f)
            {
                float3 cT0 = PercepTex.SampleLevel(LinearSamp, (world - tang * iconSpan) * invDesktop, 0).rgb;
                float3 cT2 = PercepTex.SampleLevel(LinearSamp, (world + tang * iconSpan) * invDesktop, 0).rgb;
                cTang = clamp(0.25f * (cT0 + cT2) + 0.50f * cT1, inLo, inHi);
            }

            float3 axis   = c2Diff / max(c2Len, 1e-4f);
            float  uIcon  = saturate(dot(cTang - cLo, axis) / max(c2Len, 1e-3f));
            float2 norm   = float2(-tang.y, tang.x);
            float3 cPos   = PercepTex.SampleLevel(LinearSamp, (world + norm * 0.35f) * invDesktop, 0).rgb;
            float3 cNeg   = PercepTex.SampleLevel(LinearSamp, (world - norm * 0.35f) * invDesktop, 0).rgb;
            float  gradIcon = abs(dot(cPos - cNeg, axis)) / (0.70f * max(c2Len, 1e-3f));
            float  aaIcon = max(gradIcon / Zoom, 1e-4f) * lerp(0.78f, 0.72f, saturate(Sharpness));
            float  tIcon  = saturate((uIcon - 0.5f) / aaIcon + 0.5f);

            float3 cVecIcon = lerp(cLo, cHi, tIcon);
            float  colErr   = length(cTang - lerp(cLo, cHi, uIcon));
            float  c2RgbMax = max(abs(c2Diff.r), max(abs(c2Diff.g), abs(c2Diff.b)));
            float  realEdge = max(
                smoothstep(0.35f, 0.65f, c2Span / max(wideC, 1e-3f)),
                smoothstep(0.14f, 0.26f, c2RgbMax) * (1.0f - twoTone * onSegment));
            float  iconMask = smoothstep(0.28f, 0.55f, coherence)
                            * smoothstep(0.04f, 0.10f, c2Len)
                            * realEdge
                            * (1.0f - smoothstep(0.08f, 0.20f, colErr))
                            * (1.0f - mediaMask);
            smoothRgb = lerp(smoothRgb, cVecIcon, iconMask);
        }

        // Inside detected Photo/Video bounding regions: render in natural Sharp-Bilinear quality
        // (preserves 100% original pixel crispness with a 1-screen-px AA transition, zero smearing).
        if (mediaMask > 0.001f)
        {
            float2 sf = saturate((f2 - 0.5f) * min(Zoom, 8.0f) + 0.5f);
            float3 sharpBilin = lerp(lerp(c00, c10, sf.x), lerp(c01, c11, sf.x), sf.y);
            smoothRgb = lerp(smoothRgb, sharpBilin, mediaMask);
        }

        // Mild CAS for non-media UI graphics (disabled inside mediaMask where Sharp-Bilinear is pure).
        float sharpDrive = saturate(Sharpness) * saturate((Zoom - 1.15f) * 0.65f) * (1.0f - mediaMask);
        if (sharpDrive > 0.01f)
        {
            float3 cN = PercepTex.SampleLevel(LinearSamp, (world - float2(0.0f, 0.90f)) * invDesktop, 0).rgb;
            float3 cS = PercepTex.SampleLevel(LinearSamp, (world + float2(0.0f, 0.90f)) * invDesktop, 0).rgb;
            float3 cW = PercepTex.SampleLevel(LinearSamp, (world - float2(0.90f, 0.0f)) * invDesktop, 0).rgb;
            float3 cE = PercepTex.SampleLevel(LinearSamp, (world + float2(0.90f, 0.0f)) * invDesktop, 0).rgb;

            float3 mnRGB = min(min(min(cN, cS), min(cW, cE)), smoothRgb);
            float3 mxRGB = max(max(max(cN, cS), max(cW, cE)), smoothRgb);
            float3 rangeRGB = mxRGB - mnRGB;
            float  maxRange = max(rangeRGB.r, max(rangeRGB.g, rangeRGB.b));

            float detailMask = smoothstep(0.025f, 0.085f, maxRange) * (1.0f - smoothstep(0.20f, 0.50f, twoTone));
            if (detailMask > 0.001f)
            {
                float3 amp    = sqrt(saturate(min(mnRGB, 1.0f - mxRGB) / max(mxRGB, 1e-4f)));
                float3 wCas   = amp * (-1.0f / lerp(9.0f, 4.6f, sharpDrive * detailMask));
                float3 casRgb = (smoothRgb + wCas * (cN + cS + cW + cE)) / (1.0f + 4.0f * wCas);
                casRgb = clamp(casRgb, mnRGB, mxRGB);
                smoothRgb = lerp(smoothRgb, casRgb, detailMask * (1.0f - edgeMask));
            }
        }

        // Clamp sharpening undershoots/halos (e.g. Chrome ClearType dark rim on dark backgrounds)
        // cleanly into the true surrounding poles near two-tone text letters without touching media.
        float haloClamp = smoothstep(0.32f, 0.62f, twoTone) * smoothstep(0.25f, 0.55f, onSegment) * (1.0f - mediaMask);
        if (haloClamp > 0.001f)
        {
            smoothRgb = lerp(smoothRgb, clamp(smoothRgb, wideDark, wideLight), haloClamp);
        }

        if (edgeMask <= 0.0f)
        {
            return ToOutput(smoothRgb);
        }
    }

    float darkInk = smoothstep(0.47f, 0.53f, wideLightA.a);
    float inkSign = darkInk * 2.0f - 1.0f;

    // Fast 2x2 solid-cell early-out on pure text interiors and adjacent whitespace:
    // reuses already-loaded c00..c11 to skip tangent smoothing, gradient taps, pow(), and ridge taps.
    if (edgeMask >= 0.999f && useSubpixel < 0.5f)
    {
        float q00 = saturate(dot(c00 - wideDark, spanRgb) * invSegLen2);
        float q10 = saturate(dot(c10 - wideDark, spanRgb) * invSegLen2);
        float q01 = saturate(dot(c01 - wideDark, spanRgb) * invSegLen2);
        float q11 = saturate(dot(c11 - wideDark, spanRgb) * invSegLen2);
        if (max(max(q00, q10), max(q01, q11)) < 0.03f)
        {
            return ToOutput(wideDark);
        }
        if (min(min(q00, q10), min(q01, q11)) > 0.97f)
        {
            return ToOutput(wideLight);
        }
    }

    float smoothScale = lerp(0.70f, 1.0f, saturate(Sharpness));
    float maxSpan = ((useSubpixel > 0.5f) ? 0.90f : 1.15f) * smoothScale;
    float span    = coherence * lerp(0.0f, maxSpan, diagFactor);

    float uCenter = SampleCoverageFast(world, invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, useSubpixel, darkInk);
    bool onContourBand = (uCenter > 0.22f && uCenter < 0.82f);

    float u = uCenter;
    if (span > 0.05f)
    {
        float2 pM1 = world - tang * (span * 0.5f);
        float2 pP1 = world + tang * (span * 0.5f);
        float2 pM2 = world - tang * span;
        float2 pP2 = world + tang * span;
        float  cornerDamp = 1.0f;

        if (Zoom >= 3.0f && onContourBand)
        {
            float  cohM1;
            float2 tM1    = DecodeDoubleTangent(LocalExtTex.SampleLevel(LinearSamp, pM1 * invDesktop, 0).rg * 2.0f - 1.0f, cohM1);
            if (dot(tM1, tang) < 0.0f) tM1 = -tM1;
            float  alignM = smoothstep(0.35f, 0.70f, cohM1) * smoothstep(0.45f, 0.85f, dot(tM1, tang));
            float2 stepM  = normalize(lerp(tang, tM1, alignM));

            float  cohP1;
            float2 tP1    = DecodeDoubleTangent(LocalExtTex.SampleLevel(LinearSamp, pP1 * invDesktop, 0).rg * 2.0f - 1.0f, cohP1);
            if (dot(tP1, tang) < 0.0f) tP1 = -tP1;
            float  alignP = smoothstep(0.35f, 0.70f, cohP1) * smoothstep(0.45f, 0.85f, dot(tP1, tang));
            float2 stepP  = normalize(lerp(tang, tP1, alignP));

            pM2 = pM1 - stepM * (span * 0.5f);
            pP2 = pP1 + stepP * (span * 0.5f);

            // Preserve sharp acute glyph corners/apices (A, V, W, M, K, Z, 7) using already-loaded RK2 tangents.
            float tangAgree = dot(tM1, tP1);
            float neighCoh  = min(cohM1, cohP1);
            float sharpTurn = smoothstep(0.55f, 0.15f, tangAgree) * smoothstep(0.42f, 0.72f, neighCoh);
            cornerDamp      = 1.0f - 0.50f * sharpTurn;
        }

        float uM1 = SampleCoverageFast(pM1, invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, useSubpixel, darkInk);
        float uP1 = SampleCoverageFast(pP1, invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, useSubpixel, darkInk);
        float uM2, uP2;
        if (useSubpixel > 0.5f)
        {
            uM2 = SampleCoverageFast(pM2, invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
            uP2 = SampleCoverageFast(pP2, invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
        }
        else
        {
            uM2 = saturate(dot(PercepTex.SampleLevel(LinearSamp, pM2 * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
            uP2 = saturate(dot(PercepTex.SampleLevel(LinearSamp, pP2 * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        }
        float uSmooth = 0.14f * (uM2 + uP2) + 0.22f * (uM1 + uP1) + 0.28f * uCenter;

        // Guard narrow inter-letter / counter background gaps (bgGap) AND tight 1px grayscale dark-ink
        // corners/arches (inkPeak, e.g. 6pt downsampled text) so tangent smoothing keeps normal curves
        // wave-free without fusing adjacent letters or erasing thin arcs.
        float bgGap1 = (darkInk > 0.5f)
            ? ((uCenter > 0.28f) ? max(0.0f, uCenter - max(uM1, uP1)) : 0.0f)
            : ((uCenter < 0.72f) ? max(0.0f, min(uM1, uP1) - uCenter) : 0.0f);
        float bgGap2 = (darkInk > 0.5f)
            ? ((uCenter > 0.28f) ? max(0.0f, uCenter - max(uM2, uP2)) : 0.0f)
            : ((uCenter < 0.72f) ? max(0.0f, min(uM2, uP2) - uCenter) : 0.0f);
        float bgGap = max(bgGap1, bgGap2 * 0.55f);

        float inkPeak = 0.0f;
        if (useSubpixel < 0.5f)
        {
            inkPeak = (darkInk > 0.5f)
                ? ((uCenter < 0.62f) ? max(0.0f, min(uM1, uP1) - uCenter) : 0.0f)
                : ((uCenter > 0.38f) ? max(0.0f, uCenter - max(uM1, uP1)) : 0.0f);
        }

        // On tiny 6-8pt tight loops ('e', 'a', 's', 'g'), outer tangent taps (±span) curve sharply
        // away from uCenter; damping span smoothing preserves small inner openings without waves.
        float tightCurve = max(abs(uM2 - uCenter), abs(uP2 - uCenter));
        float keepSmooth = (1.0f - smoothstep(0.025f, 0.10f, bgGap))
                         * (1.0f - smoothstep(0.14f,  0.30f, inkPeak))
                         * (1.0f - 0.55f * smoothstep(0.28f, 0.52f, tightCurve))
                         * cornerDamp;
        u = lerp(uCenter, uSmooth, keepSmooth);
    }

    float epsX = (useSubpixel > 0.5f) ? 0.16f : 0.22f;
    float epsY = 0.22f;
    float uNxP, uNxM, uNyP, uNyM;
    if (useSubpixel > 0.5f)
    {
        uNxP = SampleCoverageFast(world + float2(epsX, 0.0f), invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
        uNxM = SampleCoverageFast(world - float2(epsX, 0.0f), invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
        uNyP = SampleCoverageFast(world + float2(0.0f, epsY), invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
        uNyM = SampleCoverageFast(world - float2(0.0f, epsY), invDesktop, wideDark, spanRgb, safeInvSpan, chanW, invSegLen2, ctDir, 1.0f, darkInk);
    }
    else
    {
        // Hardware bilinear PercepTex taps projected onto 3D pole vector: 1 tap each vs 4-10 loads!
        uNxP = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(epsX, 0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        uNxM = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(epsX, 0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        uNyP = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.0f, epsY)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        uNyM = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.0f, epsY)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
    }

    // Ridge-safe gradient magnitude keeps 1px Excel gridlines & thin H/V hairlines consistent across zoom steps.
    float gx = max(abs(uNxP - uNxM) / (2.0f * epsX), 0.72f * max(abs(uNxP - uCenter), abs(uCenter - uNxM)) / epsX);
    float gy = max(abs(uNyP - uNyM) / (2.0f * epsY), 0.72f * max(abs(uNyP - uCenter), abs(uCenter - uNyM)) / epsY);
    float gradMag = length(float2(gx, gy));
    if (useSubpixel < 0.5f && diagFactor < 0.20f)
    {
        gradMag = max(gradMag, 0.85f);
    }
    float uPerScreenPx = gradMag / Zoom;
    float highZoomTighten = lerp(1.0f, 0.86f, smoothstep(6.0f, 20.0f, Zoom));
    float aa = max(uPerScreenPx, 1e-4f) * lerp(0.80f, 0.74f, saturate(Sharpness)) * highZoomTighten;

    // Grayscale AA & downsampled text (Chrome/Skia/DirectWrite/Google Docs) is blended in gamma-corrected
    // space, so the true 50% geometric coverage contour sits above 0.50 in sRGB perceptual space:
    // - For white-on-dark (darkInk=0), raising threshold thins bloated white strokes and separates letters.
    // - For dark-on-light (darkInk=1), raising threshold restores eroded sub-pixel dark strokes (50% docs).
    float baseThresh = 0.50f + 0.04f * (1.0f - darkInk);
    if (useSubpixel < 0.5f)
    {
        float grayMid = pow(0.5f * (pow(max(lWideLo, 1e-4f), 1.48f) + pow(max(lWideHi, 1e-4f), 1.48f)), 1.0f / 1.48f);
        baseThresh = clamp((grayMid - lWideLo) * invWideC, 0.50f, 0.62f);
    }
    float threshold = baseThresh + inkSign * MaxBoldShift * clamp(TextGamma, -0.5f, 1.0f);

    // Recover sub-pixel-thin (< 1px) strokes on 6-8pt text ONLY where uCenter sits near the threshold
    // boundary, and sample diagonal taps lazily only when H/V doesn't already confirm a thin ridge.
    bool needThin = (darkInk > 0.5f)
        ? (uCenter > threshold - 0.18f && uCenter < 0.82f)
        : (uCenter > 0.20f && uCenter < threshold + 0.18f);
    if (needThin)
    {
        float hx = (useSubpixel > 0.5f) ? 0.55f : 0.85f;
        float uL = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(hx,    0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uR = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(hx,    0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uU = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.0f, 0.85f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uD = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.0f, 0.85f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);

        if (darkInk > 0.5f)
        {
            float maxRidge = max(max(0.0f, min(uL, uR) - uCenter), max(0.0f, min(uU, uD) - uCenter));
            if (maxRidge < 0.23f)
            {
                float uD1 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.68f, 0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD2 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.68f, 0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD3 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.68f,-0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD4 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.68f,-0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                maxRidge = max(maxRidge, max(max(0.0f, min(uD1, uD2) - uCenter), max(0.0f, min(uD3, uD4) - uCenter)));
            }
            float thinDark = smoothstep(0.07f, 0.23f, maxRidge) * smoothstep(0.80f, 0.48f, uCenter);
            threshold += lerp(0.13f, 0.16f, useSubpixel) * thinDark;
        }
        else
        {
            float maxRidge = max(max(0.0f, uCenter - max(uL, uR)), max(0.0f, uCenter - max(uU, uD)));
            if (maxRidge < 0.26f)
            {
                float uD1 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.68f, 0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD2 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.68f, 0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD3 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.68f,-0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                float uD4 = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.68f,-0.68f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
                maxRidge = max(maxRidge, max(max(0.0f, uCenter - max(uD1, uD2)), max(0.0f, uCenter - max(uD3, uD4))));
            }
            float thinLight = smoothstep(0.10f, 0.26f, maxRidge) * smoothstep(0.22f, 0.52f, uCenter);
            threshold -= 0.15f * thinLight;
        }
    }
    float t = saturate((u - threshold) / aa + 0.5f);

    float3 contourRgb = lerp(wideDark, wideLight, t);
    if (t > 0.002f && t < 0.998f)
    {
        float3 linRgb = sqrt(lerp(wideDark * wideDark, wideLight * wideLight, t));
        contourRgb = lerp(contourRgb, linRgb, 0.45f);
    }
    return ToOutput(edgeMask >= 0.999f ? contourRgb : lerp(smoothRgb, contourRgb, edgeMask));
}

float4 PSMain(VSOut input) : SV_TARGET
{
    return float4(ComputeMagnifiedRgb(input.Pos.xy), 1.0f);
}

// -----------------------------------------------------------------------------
// Pure-D3D11 analytical vector cursor pass (eliminates per-frame D3D11<->D2D1
// interop flush on AMD/NVIDIA GPUs while keeping crisp anti-aliased scaling).
// -----------------------------------------------------------------------------
cbuffer CursorParams : register(b1)
{
    float  CursorX;
    float  CursorY;
    float  CursorScale;
    float  StrokeHalfWidth;
    float  CursorScreenW;
    float  CursorScreenH;
    float  CursorShape;    // 0=Arrow, 1=IBeam, 2=Hand, 3=SizeWE, 4=SizeNS, 5=SizeAll
    float  _cPad1;
    float4 CursorFillColor;
    float4 CursorStrokeColor;
};

struct CursorVSOut
{
    float4 Pos   : SV_POSITION;
    float2 Local : TEXCOORD0;
};

CursorVSOut VSCursor(uint vid : SV_VertexID)
{
    const float2 lo = float2(-13.0f, -13.0f);
    const float2 hi = float2(15.5f, 23.5f);
    float2 local = float2((vid & 1u) != 0u ? hi.x : lo.x,
                          (vid & 2u) != 0u ? hi.y : lo.y);
    float2 screen = float2(CursorX, CursorY) + local * CursorScale;
    float2 ndc = float2((screen.x / max(CursorScreenW, 1.0f)) * 2.0f - 1.0f,
                        1.0f - (screen.y / max(CursorScreenH, 1.0f)) * 2.0f);
    CursorVSOut o;
    o.Pos = float4(ndc, 0.0f, 1.0f);
    o.Local = local;
    return o;
}

float SdSegment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-5f));
    return length(pa - ba * h);
}

float SdBox(float2 p, float2 b)
{
    float2 d = abs(p) - b;
    return length(max(d, 0.0f)) + min(max(d.x, d.y), 0.0f);
}

float SdTriangle(float2 p, float2 p0, float2 p1, float2 p2)
{
    float2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
    float2 v0 = p  - p0, v1 = p  - p1, v2 = p  - p2;
    float2 pq0 = v0 - e0 * saturate(dot(v0, e0) / dot(e0, e0));
    float2 pq1 = v1 - e1 * saturate(dot(v1, e1) / dot(e1, e1));
    float2 pq2 = v2 - e2 * saturate(dot(v2, e2) / dot(e2, e2));
    float s = sign(e0.x * e2.y - e0.y * e2.x);
    float2 d = min(min(float2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                       float2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                       float2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

float SdCursorArrow(float2 p)
{
    static const float2 v[7] = {
        float2( 0.0f,  0.0f),
        float2( 0.0f, 17.0f),
        float2( 4.5f, 13.0f),
        float2( 7.8f, 20.5f),
        float2(10.5f, 19.2f),
        float2( 7.2f, 11.8f),
        float2(12.5f, 11.8f)
    };
    float d = dot(p - v[0], p - v[0]);
    float s = 1.0f;
    [unroll]
    for (int i = 0, j = 6; i < 7; j = i, i++)
    {
        float2 e = v[j] - v[i];
        float2 w = p - v[i];
        float2 b = w - e * saturate(dot(w, e) / dot(e, e));
        d = min(d, dot(b, b));
        bool3 cond = bool3(p.y >= v[i].y, p.y < v[j].y, e.x * w.y > e.y * w.x);
        if (all(cond) || all(!cond))
        {
            s = -s;
        }
    }
    return s * sqrt(d);
}

float SdCursorIBeam(float2 p)
{
    float stem = SdSegment(p, float2(0.0f, -8.5f), float2(0.0f, 8.5f)) - 0.85f;
    float topS = SdSegment(p, float2(-3.8f, -8.5f), float2(3.8f, -8.5f)) - 0.80f;
    float botS = SdSegment(p, float2(-3.8f,  8.5f), float2(3.8f,  8.5f)) - 0.80f;
    return min(stem, min(topS, botS));
}

float SdCursorHand(float2 p)
{
    float d = SdSegment(p, float2(0.0f, 1.3f), float2(0.0f, 10.0f)) - 1.35f;
    d = min(d, SdSegment(p, float2(2.6f, 5.5f), float2(2.6f, 10.5f)) - 1.25f);
    d = min(d, SdSegment(p, float2(5.0f, 6.5f), float2(5.0f, 11.0f)) - 1.20f);
    d = min(d, SdSegment(p, float2(7.2f, 8.0f), float2(7.2f, 11.5f)) - 1.10f);
    d = min(d, SdSegment(p, float2(-4.2f, 9.2f), float2(-0.5f, 13.5f)) - 1.35f);
    d = min(d, SdBox(p - float2(3.2f, 12.2f), float2(4.4f, 3.6f)) - 0.8f);
    d = min(d, SdBox(p - float2(3.0f, 16.0f), float2(3.2f, 1.6f)) - 0.4f);
    return d;
}

float SdCursorSizeWE(float2 p)
{
    float shaft = SdSegment(p, float2(-6.2f, 0.0f), float2(6.2f, 0.0f)) - 1.0f;
    float leftH = SdTriangle(p, float2(-10.5f, 0.0f), float2(-5.5f, -4.0f), float2(-5.5f, 4.0f));
    float rightH = SdTriangle(p, float2(10.5f, 0.0f), float2(5.5f, 4.0f), float2(5.5f, -4.0f));
    return min(shaft, min(leftH, rightH));
}

float SdCursorSizeAll(float2 p)
{
    return min(SdCursorSizeWE(p), SdCursorSizeWE(p.yx));
}

float4 PSCursor(CursorVSOut input) : SV_TARGET
{
    int shape = (int)(CursorShape + 0.5f);
    float sd;
    if (shape == 1)      sd = SdCursorIBeam(input.Local);
    else if (shape == 2) sd = SdCursorHand(input.Local);
    else if (shape == 3) sd = SdCursorSizeWE(input.Local);
    else if (shape == 4) sd = SdCursorSizeWE(input.Local.yx);
    else if (shape == 5) sd = SdCursorSizeAll(input.Local);
    else                 sd = SdCursorArrow(input.Local);

    float pxLocal = 1.0f / max(CursorScale, 0.25f);
    float outerAlpha = saturate(0.5f - (sd - StrokeHalfWidth) / pxLocal);
    if (outerAlpha <= 0.001f)
    {
        discard;
    }
    float fillMask = saturate(0.5f - (sd + StrokeHalfWidth) / pxLocal);
    float3 rgb = lerp(CursorStrokeColor.rgb, CursorFillColor.rgb, fillMask);
    return float4(rgb, outerAlpha);
}
