#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Fog application and far sky-fade structure adapted from Farseer's region.fsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson). Unlike Farseer we
// have real per-vertex surface colors, shaded with screen-space-derivative normals.

in vec4 worldPos;
in vec4 vertexColor;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float fogAmount;
in float edgeFade;
in float nearFade;
in vec3 tint;
in vec2 localXZ;
in float tintBlend;

// Gap fill: a coarser parent mesh drawn only inside one child footprint that
// nothing finer covered this frame (minX, minZ, maxX, maxZ in section-local
// blocks). Whole-section draws pass a rectangle far larger than any section.
uniform vec4 clipRect;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform float skyFadeStart;
uniform float disableLodFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;
uniform vec3 rgbaAmbientIn;

// Live tint table. The alpha byte carries a tint SLOT plus a blend band:
//   0..63    opaque,     slot = alpha
//   64..127  water,      slot = alpha - 64
//   128..191 thin plant, slot = alpha - 128
// Slot 0 is the identity tint. One slot per distinct (climate map, season map) pair,
// because leaves pick a seasonal map per species and water has its own -- a single
// shared foliage tint left every tree the same colour and water untinted grey.
// Sampled at two heights and blended by vertex height: the climate maps are indexed
// by temperature, which drops with altitude, so one sample at the player's feet gave
// mountaintops the same lush green as the valley floor.
// Must equal LodTintRegistry.MaxSlots; LoadShader logs an error if it does not.
const int TINT_SLOTS = 64;
const int CLIMATE_GRID = 4;
uniform float snowLineY;
uniform float sectionOriginX;
uniform float sectionOriginZ;
uniform float climateGridOriginX;
uniform float climateGridOriginZ;
uniform float climateGridStep;
uniform vec4 climateLow[16];
uniform vec4 climateHigh[16];
uniform vec4 keepClimateLow;
uniform vec4 keepClimateHigh;
uniform vec4 seasonTints[TINT_SLOTS];
uniform float seasonRel;
uniform vec4 fallbackSeasonTint;

// Blend factors per band, now that alpha carries the slot instead of an opacity.
// Flowers are crossed quads in vanilla; as a solid cube they read as a grey blob, so
// they are drawn mostly see-through and the ground shows through them.
const float WATER_ALPHA = 0.66;
const float THIN_ALPHA = 0.50;

// Blocks per column in the section being drawn (1 at level 0, doubling per level).
// Coarse sections merge whole neighbourhoods into one colour, and greedy meshing
// then fuses them into large single-colour quads; a little world-space variation
// scaled to the column size breaks those plates up without inventing detail.
// Scaling by column size is what keeps the pattern roughly constant on screen
// instead of aliasing into shimmer at distance.
uniform float columnBlocks;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

vec4 sampleClimateField(vec2 worldXZ)
{
    // 40-block blobs (cheap 4x4). Warp straddles 64-tile seams; plate kill is
    // greener bake + grassLike plant pull below, not denser climate uploads.
    float n0 = valuenoise(vec3(worldXZ.x, 19.0, worldXZ.y) / 40.0);
    float n1 = valuenoise(vec3(worldXZ.x, 71.0, worldXZ.y) / 40.0);
    vec2 warped = worldXZ + (vec2(n0, n1) - 0.5) * 28.0;
    vec2 g = (warped - vec2(climateGridOriginX, climateGridOriginZ)) / max(1.0, climateGridStep);
    g = clamp(g, vec2(0.0), vec2(float(CLIMATE_GRID) - 1.001));
    vec2 cell = floor(g);
    vec2 f = g - cell;
    f = f * f * (3.0 - 2.0 * f);
    int x0 = int(cell.x);
    int z0 = int(cell.y);
    int i00 = x0 + z0 * CLIMATE_GRID;
    int i10 = i00 + 1;
    int i01 = i00 + CLIMATE_GRID;
    int i11 = i01 + 1;
    vec4 lo = mix(mix(climateLow[i00], climateLow[i10], f.x), mix(climateLow[i01], climateLow[i11], f.x), f.y);
    vec4 hi = mix(mix(climateHigh[i00], climateHigh[i10], f.x), mix(climateHigh[i01], climateHigh[i11], f.x), f.y);
    return mix(lo, hi, tintBlend);
}

float seasonAmount(float tempByte, float mapAlpha)
{
    float x = tempByte * 255.0;
    float seasonWeight = clamp(0.5 - cos(x / 42.0) / 2.3 + max(0.0, 128.0 - x) / 512.0 - max(0.0, x - 130.0) / 200.0, 0.0, 1.0);
    float amt = clamp(mapAlpha * seasonWeight, 0.0, 1.0);
    return amt * step(0.0, seasonRel + 1.0);
}

void main()
{
    if (dist > 1.0) discard;
    if (localXZ.x < clipRect.x || localXZ.y < clipRect.y
        || localXZ.x > clipRect.z || localXZ.y > clipRect.w) discard;

    // Flat-shaded facet normal from position derivatives - no normals in the mesh.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    // sunPosition arrives as Calendar.SunPositionNormalized, so it is already a unit
    // vector. The call below passes it to getSkyColorAt unnormalized for the same reason.
    float sunAngle = max(0.0, dot(normal, sunPosition));
    float shade = 0.55 + 0.45 * sunAngle;
    // Flat tops: match vanilla up-face light so grass/snow in the overdraw ring
    // is not a darker plate against the chunks in front. Near ring goes to 1.0.
    float up = clamp(normal.y, 0.0, 1.0);
    if (up > 0.55) shade = max(shade, up * mix(0.95, 1.0, nearFade));

    // Decode the tint slot, then snow line on up-facing terrain.
    // Only the blend band is needed here; the tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin, 3 baked
    bool translucent = band == 1 || band == 2;
    int slotRaw = int(vertexColor.a * 255.0 + 0.5);
    int slot = clamp(slotRaw - band * TINT_SLOTS, 0, TINT_SLOTS - 1);
    bool bakedAlbedo = band == 3;

    vec3 liveTint = tint;
    vec4 localCl = sampleClimateField(vec2(sectionOriginX + localXZ.x, sectionOriginZ + localXZ.y));
    vec4 keepCl = mix(keepClimateLow, keepClimateHigh, tintBlend);
    if (!bakedAlbedo && slot > 0 && band != 1) {
        vec3 keepRgb = max(keepCl.rgb, vec3(0.04));
        liveTint *= clamp(localCl.rgb / keepRgb, vec3(0.25), vec3(4.0));
        if (seasonTints[slot].a > 0.0) {
            float seasonAmt = seasonAmount(localCl.a, seasonTints[slot].a);
            if (seasonAmt > 0.0)
                liveTint = mix(liveTint, seasonTints[slot].rgb, seasonAmt);
        }
    }

    vec3 albedo = vertexColor.rgb * liveTint;
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);
    // Foam / missing-tex water stores near-white and then looks like ice.
    // Force a water blue so streams stay streams without a remesh.
    if (band == 1 && (albedo.r + albedo.g + albedo.b) > 1.65)
        albedo = vec3(0.18, 0.38, 0.50);

    bool brightSnow = false;
    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float lum = (albedo.r + albedo.g + albedo.b) * (1.0 / 3.0);
        // Chromatic green is grass/leaves. Dirt-washing that toward brown is why
        // FlagBaked foliage went mud. Thin plants (band 2) already skipped this.
        bool chromaGreen = albedo.g > albedo.r + 0.02 && albedo.g > albedo.b && lum < 0.78;
        // Real snow / ice tops are bright white FlagSnow albedo - leave them alone.
        brightSnow = lum > 0.72;

        // Mid/far brown plates: pull grassLike toward world-space plant colour.
        // 0.8.46: near ring (columnBlocks~1, nearFade high) stays closer to 0.7.76
        // vertex*tint continuity — hard plant-pull + climate lattice made foreground
        // micro-squares. Coarse columns still get a strong pull.
        if (!brightSnow && upness > 0.55 && band != 1) {
            bool rockGrey = abs(albedo.r - albedo.g) < 0.06
                && abs(albedo.g - albedo.b) < 0.06
                && lum > 0.28;
            bool grassLike = !rockGrey && (
                chromaGreen
                || (lum > 0.10 && lum < 0.72
                    && albedo.g >= albedo.b
                    && albedo.g >= albedo.r * 0.55)
                || (upness > 0.82 && lum > 0.12 && lum < 0.68
                    && albedo.b < max(albedo.r, albedo.g) + 0.03));
            float pullGate = smoothstep(1.0, 2.75, columnBlocks) * (1.0 - nearFade * 0.90);
            if (grassLike && pullGate > 0.04) {
                vec3 plant = clamp(localCl.rgb, vec3(0.05), vec3(1.0));
                if (fallbackSeasonTint.a > 0.0) {
                    float amt = seasonAmount(localCl.a, fallbackSeasonTint.a);
                    if (amt > 0.0) plant = mix(plant, fallbackSeasonTint.rgb, amt);
                }
                float plantLuma = max((plant.r + plant.g + plant.b) * (1.0 / 3.0), 0.08);
                float mixAmt = (bakedAlbedo ? 0.90 : 0.78) * pullGate;
                albedo = mix(albedo, plant * (lum / plantLuma), mixAmt);
                lum = (albedo.r + albedo.g + albedo.b) * (1.0 / 3.0);
                chromaGreen = albedo.g > albedo.r + 0.02 && albedo.g > albedo.b && lum < 0.78;
            }
        }

        // TopoHorizon-style snowline: whiten up-facing soil/grass above the live
        // freeze line. White, not a 0.45 gray hat. Skip thin canopy (band 2).
        float groundSnowline = 0.0;
        if (!brightSnow && band != 2 && upness > 0.55 && snowLineY < 90000.0) {
            groundSnowline = smoothstep(snowLineY - 12.0, snowLineY + 20.0, yLevel) * upness;
            albedo = mix(albedo, vec3(0.93, 0.95, 0.98), groundSnowline);
            lum = (albedo.r + albedo.g + albedo.b) * (1.0 / 3.0);
            brightSnow = lum > 0.72 || groundSnowline > 0.65;
            chromaGreen = chromaGreen && groundSnowline < 0.15;
        }

        // Mild dirt wash on brown soil/rock SIDES only. Never chromatic green,
        // never snow-white / FlagSnow-bright tops, never grass tops.
        if (!brightSnow && !chromaGreen && upness < 0.70) {
            vec3 dirtBrown = vec3(0.46, 0.33, 0.20);
            float sideAmt = (1.0 - smoothstep(0.18, 0.70, upness))
                * (1.0 - smoothstep(0.58, 0.80, lum));
            albedo = mix(albedo, dirtBrown, sideAmt * mix(0.40, 0.18, nearFade));
        }

        // Break greedy-mesh plates. World-space, scaled by column size.
        if (!brightSnow) {
            float period = max(4.0, columnBlocks * 6.0);
            float n = valuenoise(worldPos.xyz / period);
            albedo *= 1.0 + 0.16 * (n - 0.5);
        }
    }

    vec4 terraColor = vec4(albedo, outAlpha);

    // One clock for the whole horizon: vanilla's live ambient, the same rgbaAmbientIn
    // chunk shaders feed applyLight. Captured albedo is daytime-bright; this is what
    // actually goes purple/dark at night. Calendar.SunColor stays sunset orange after
    // the near ground has already gone dark, which is why far sand was a glowing
    // yellow band. DisableLodFog only skips extra pastViewHaze, not this.
    terraColor.rgb *= shade * rgbaAmbientIn;
    // Modest near-ring exposure (1.10). Skip already-bright snow so caps do not blow.
    if (!brightSnow)
        terraColor.rgb *= mix(1.0, 1.10, nearFade);
    terraColor.rgb = clamp(terraColor.rgb, 0.0, 1.0);

    // Same applyFog path as vanilla chunks. Skip applySpheresFog (height fog punches
    // mountain faces). DisableLodFog only skipped extra pastViewHaze in the vsh.
    if (fogAmount > 0.001) {
        terraColor = applyFog(terraColor, fogAmount);
    }

    // Dissolve both the far edge of the cache and the edges of the explored area
    // into the sky, so neither ends in a visible wall. With DisableLodFog push the
    // dissolve to the true far rim so mid-band LODs stay opaque (no orange/grey wash).
    float fadeStart = disableLodFog > 0.5 ? max(skyFadeStart, 0.88) : skyFadeStart;
    float fade = max(smoothstep(clamp(fadeStart, 0.05, 0.98), 1.0, dist), pow(edgeFade, 0.75));


    // Only work out the sky where it is actually mixed in. mix(x, y, 0.0) is x, so
    // skipping this where fade is zero cannot change a pixel, and fade is zero across
    // the inner part of the band, which is most of the terrain on screen.
    //
    // It is worth skipping. getSkyColorAt costs several texture fetches, three
    // normalize calls, a pow, and a noise chain of eight sin calls, per fragment,
    // and all of it was being multiplied by zero.
    //
    // The branch is coherent: fade is a function of distance, so the fragments that
    // take it are a ring near the horizon rather than a speckle across the screen.
    if (fade > 0.0) {
        vec4 skyColor = vec4(1.0);
        vec4 skyGlow = vec4(1.0);
        vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
        float skyHorizonFog = disableLodFog > 0.5 ? 0.0 : horizonFog;
        getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), skyHorizonFog, skyColor, skyGlow);
        float murkiness = disableLodFog > 0.5 ? 0.0 : max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
        skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
        skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

        outColor = mix(terraColor, skyColor, fade);
        outGlow = mix(vec4(0.0), skyGlow, fade);
    } else {
        outColor = terraColor;
        outGlow = vec4(0.0);
    }

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}

