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
    float  InvertColors;   // 1.0 = invert output sRGB colours (0 -> 1, 1 -> 0)
    float4 CropRectNorm;
};

Texture2D    DesktopTex   : register(t0);
Texture2D    PoleDarkTex  : register(t1);
Texture2D    PoleLightTex : register(t2);
Texture2D    LocalExtTex  : register(t3);
Texture2D    PercepTex    : register(t4);
Texture2D    MetaTex      : register(t5);
SamplerState LinearSamp   : register(s1);

struct VSOut
{
    float4 Pos : SV_POSITION;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.Pos = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return o;
}

VSOut VSCropMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 t  = float2((float)(id & 1u), (float)((id >> 1u) & 1u));
    float2 uv = lerp(CropRectNorm.xy, CropRectNorm.zw, t);
    o.Pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
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
    float3 clamped = saturate(s);
    return (InvertColors > 0.5f) ? (1.0f - clamped) : clamped;
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
            float rbHere   = cMC.r - cMC.b;
            if (abs(dmCenter) > 0.10f && abs(rbHere) > FringeChroma)
            {
                subpixelVote += clamp(rbHere * dmCenter * 16.0f, -1.0f, 1.0f);
            }
        }
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
            int2 pt = center + FarOffsets[p];
            if (FetchMeta(pt).r < 0.022f)
            {
                float4 cf4 = FetchPercep(pt);
                float  uf  = (cf4.a - lMin) * invL;
                float  fDistSq = (float)(FarOffsets[p].x * FarOffsets[p].x + FarOffsets[p].y * FarOffsets[p].y);
                float  fPolW   = 0.65f / (1.0f + 0.10f * fDistSq);
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
    float darkIsNeutralInk  = neutralize * darkIsInk * (1.0f - smoothstep(TintChromaLo, TintChromaHi, darkChroma));
    float lightIsNeutralInk = neutralize * (1.0f - darkIsInk) * (1.0f - smoothstep(TintChromaLo, TintChromaHi, lightChroma));

    float darkRbDiff  = abs(darkInkAvg.r  - darkInkAvg.b);
    float lightRbDiff = abs(lightInkAvg.r - lightInkAvg.b);
    bool  hasColoredDarkCore  = (bestDarkInkScore  > 0.0f) && (length(bestDarkInkAnchor  - dot(bestDarkInkAnchor,  LumaWeights)) > 0.14f);
    bool  hasColoredLightCore = (bestLightInkScore > 0.0f) && (length(bestLightInkAnchor - dot(bestLightInkAnchor, LumaWeights)) > 0.14f);
    if (darkIsInk > 0.5f && neutralize > 0.20f && hasOppositeFringes && !hasColoredDarkCore)
    {
        float coreMax = max(cDarkest.r, max(cDarkest.g, cDarkest.b));
        bool  coreMono = (cDarkest.g >= min(cDarkest.r, cDarkest.b) - 0.05f) &&
                         (cDarkest.g <= max(cDarkest.r, cDarkest.b) + 0.05f);
        if ((darkRbDiff < 0.24f && darkChroma < 0.20f && darkInkAvg.g <= max(darkInkAvg.r, darkInkAvg.b) + 0.03f) ||
            (chanMin < 0.16f && lMin < 0.16f && (coreMax < 0.72f || coreMono)))
        {
            darkIsNeutralInk = 1.0f;
        }
    }
    if (darkIsInk < 0.5f && neutralize > 0.20f && hasOppositeFringes && !hasColoredLightCore)
    {
        float coreMin = min(cLightest.r, min(cLightest.g, cLightest.b));
        if ((lightRbDiff < 0.20f && lightChroma < 0.16f && lightInkAvg.g >= min(lightInkAvg.r, lightInkAvg.b) - 0.03f) ||
            (chanMax > 0.88f && coreMin > 0.68f) ||
            (chanMax > 0.86f && lightRbDiff < 0.26f && lightChroma < 0.18f && abs(subpixelVote) > 0.60f))
        {
            lightIsNeutralInk = 1.0f;
        }
    }

    float targetDarkL  = lerp(lMin, min(lMin, chanMin), darkIsNeutralInk);
    float targetLightL = lerp(lMax, max(lMax, chanMax), lightIsNeutralInk);
    if (darkIsInk > 0.5f && darkChroma < 0.04f && lMin < 0.08f)
    {
        targetDarkL = min(targetDarkL, chanMin);
    }
    if (darkIsInk < 0.5f && lightChroma < 0.04f && lMax > 0.92f)
    {
        targetLightL = max(targetLightL, chanMax);
    }

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
    float inkMin   = lerp(0.48f, 0.38f, darkInk);
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

float SampleCoverageFast(
    float2 pos,
    float2 invDesktop,
    float3 wideDark,
    float3 spanRgb,
    float3 invSpan,
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

        float3 u0 = saturate((c0 - wideDark) * invSpan);
        float3 u1 = saturate((c1 - wideDark) * invSpan);

        if (ctDir < 0.0f)
        {
            u0 = u0.bgr;
            u1 = u1.bgr;
        }

        float s = fx * 3.0f;
        if (s < 1.0f)      return lerp(u0.g, u0.b, s);
        else if (s < 2.0f) return lerp(u0.b, u1.r, s - 1.0f);
        else               return lerp(u1.r, u1.g, s - 2.0f);
    }
    else
    {
        return SampleGrayConnected(pos, wideDark, spanRgb, invSegLen2, darkInk);
    }
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
    float minChanC   = min(spanRgb.r, min(spanRgb.g, spanRgb.b));
    float maxChanC   = max(spanRgb.r, max(spanRgb.g, spanRgb.b));
    float3 invSpan   = 1.0f / max(spanRgb, 1e-3f);

    float ctSigned    = localInfo.b * 2.0f - 1.0f;
    float useSubpixel = (abs(ctSigned) > 0.25f && minChanC > 0.15f && minChanC > 0.70f * maxChanC) ? 1.0f : 0.0f;
    float ctDir       = (ctSigned >= 0.0f) ? 1.0f : -1.0f;

    float2 v         = localInfo.rg * 2.0f - 1.0f;
    float  coherence = saturate(length(v));
    float  theta     = 0.5f * atan2(v.y, v.x);
    float2 tang      = float2(cos(theta), sin(theta));
    float  diagFactor = pow(abs(2.0f * tang.x * tang.y), 0.45f);

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
    }

    // Use max(wideC, maxChanC) so saturated coloured text (e.g. pure blue or red where Rec.709 luma
    // contrast wideC is small even though RGB channel contrast maxChanC is high) AND low-contrast
    // fading tab text both receive full vector contour sharpening.
    float effectiveC = max(wideC, maxChanC * 0.65f);
    float edgeMask = twoTone
                   * onSegment
                   * smoothstep(EdgeContrastLo, EdgeContrastHi, effectiveC);

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

        if (coherence > 0.25f && c2Len > 0.04f)
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
            float  aaIcon = max(gradIcon / Zoom, 1e-4f) * lerp(0.62f, 0.78f, saturate(Sharpness));
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
                            * (1.0f - smoothstep(0.08f, 0.20f, colErr));
            smoothRgb = lerp(smoothRgb, cVecIcon, iconMask);
        }

        if (edgeMask <= 0.0f)
        {
            return ToOutput(smoothRgb);
        }
    }

    float darkInk = smoothstep(0.47f, 0.53f, wideLightA.a);
    float inkSign = darkInk * 2.0f - 1.0f;

    float smoothScale = lerp(0.70f, 1.0f, saturate(Sharpness));
    float maxSpan = ((useSubpixel > 0.5f) ? 0.90f : 1.15f) * smoothScale;
    float span    = coherence * lerp(0.0f, maxSpan, diagFactor);

    float uCenter = SampleCoverageFast(world, invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
    float u = uCenter;
    if (span > 0.05f)
    {
        float uM2 = SampleCoverageFast(world - tang * span,          invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
        float uM1 = SampleCoverageFast(world - tang * (span * 0.5f), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
        float uP1 = SampleCoverageFast(world + tang * (span * 0.5f), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
        float uP2 = SampleCoverageFast(world + tang * span,          invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
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

        float inkPeak = (useSubpixel < 0.5f && darkInk > 0.5f && uCenter < 0.58f)
            ? max(0.0f, min(uM1, uP1) - uCenter)
            : 0.0f;

        float keepSmooth = (1.0f - smoothstep(0.025f, 0.10f, bgGap))
                         * (1.0f - smoothstep(0.18f,  0.34f, inkPeak));
        u = lerp(uCenter, uSmooth, keepSmooth);
    }

    float epsX = (useSubpixel > 0.5f) ? 0.16f : 0.22f;
    float epsY = 0.22f;
    float uNxP = SampleCoverageFast(world + float2(epsX, 0.0f), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
    float uNxM = SampleCoverageFast(world - float2(epsX, 0.0f), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
    float uNyP = SampleCoverageFast(world + float2(0.0f, epsY), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
    float uNyM = SampleCoverageFast(world - float2(0.0f, epsY), invDesktop, wideDark, spanRgb, invSpan, invSegLen2, ctDir, useSubpixel, darkInk);
    float2 uGrad = float2((uNxP - uNxM) / (2.0f * epsX), (uNyP - uNyM) / (2.0f * epsY));

    float uPerScreenPx = length(uGrad) / Zoom;
    float aa = max(uPerScreenPx, 1e-4f) * lerp(0.58f, 0.72f, saturate(Sharpness));

    // Grayscale AA & downsampled text (Chrome/Skia/DirectWrite/Google Docs) is blended in gamma-corrected
    // space, so the true 50% geometric coverage contour sits above 0.50 in sRGB perceptual space:
    // - For white-on-dark (darkInk=0), raising threshold thins bloated white strokes and separates letters.
    // - For dark-on-light (darkInk=1), raising threshold restores eroded sub-pixel dark strokes (50% docs).
    float grayMid    = pow(0.5f * (pow(max(lWideLo, 1e-4f), 1.48f) + pow(max(lWideHi, 1e-4f), 1.48f)), 1.0f / 1.48f);
    float uGray50    = clamp((grayMid - lWideLo) * invWideC, 0.50f, 0.62f);
    float baseThresh = (useSubpixel > 0.5f)
        ? (0.50f + 0.04f * (1.0f - darkInk))
        : uGray50;
    float threshold  = baseThresh + inkSign * MaxBoldShift * saturate(TextGamma);

    // Recover sub-pixel-thin (< 1px) downsampled dark strokes (e.g. 50%-zoomed 6pt documents):
    // fires only on narrow 1D dark ridges surrounded on BOTH sides by brighter background, so it
    // never thickens normal >= 1.5px stems, never closes inter-letter gaps, and ignores drop shadows.
    if (useSubpixel < 0.5f && darkInk > 0.5f)
    {
        float uL = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.85f, 0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uR = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.85f, 0.0f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uU = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world - float2(0.0f, 0.85f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float uD = saturate(dot(PercepTex.SampleLevel(LinearSamp, (world + float2(0.0f, 0.85f)) * invDesktop, 0).rgb - wideDark, spanRgb) * invSegLen2);
        float ridgeH   = max(0.0f, min(uL, uR) - uCenter);
        float ridgeV   = max(0.0f, min(uU, uD) - uCenter);
        float thinDark = smoothstep(0.10f, 0.28f, max(ridgeH, ridgeV))
                       * smoothstep(0.76f, 0.50f, uCenter);
        threshold += 0.10f * thinDark;
    }
    float t = saturate((u - threshold) / aa + 0.5f);

    float3 contourRgb = lerp(wideDark, wideLight, t);
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
    float  _cPad0;
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
    const float2 lo = float2(-2.0f, -2.0f);
    const float2 hi = float2(14.5f, 22.5f);
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

float4 PSCursor(CursorVSOut input) : SV_TARGET
{
    float sd = SdCursorArrow(input.Local);
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
