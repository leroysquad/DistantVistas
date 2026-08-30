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
in vec3 tint;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform float skyFadeStart;
uniform float disableLodFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

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
uniform float snowLineY;

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

void main()
{
    if (dist < 0.0 || dist > 1.0) discard;

    // Flat-shaded facet normal from position derivatives - no normals in the mesh.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    // sunPosition arrives as Calendar.SunPositionNormalized, so it is already a unit
    // vector. The call below passes it to getSkyColorAt unnormalized for the same reason.
    float sunAngle = max(0.0, dot(normal, sunPosition));
    float shade = 0.55 + 0.45 * sunAngle;
    // Flat tops: match vanilla up-face light so grass/snow in the overdraw ring
    // is not a darker plate against the chunks in front.
    float up = clamp(normal.y, 0.0, 1.0);
    if (up > 0.55) shade = max(shade, up * 0.95);

    // Decode the tint slot, then snow line on up-facing terrain.
    // Only the blend band is needed here; the tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin
    bool translucent = band > 0;

    vec3 albedo = vertexColor.rgb * tint;
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);

    if (!translucent) {
        // Alpine overlay only. Winter valleys leave snowLineY disabled so captured
        // snow and seasonal grass match the foreground instead of a white sheet.
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 48.0, yLevel) * upness * 0.45;
        albedo = mix(albedo, vec3(0.82, 0.85, 0.88), snowMix);
    }

    // Water is a smooth surface; only break up land.
    if (!translucent) {
        float period = max(4.0, columnBlocks * 6.0);
        float n = valuenoise(worldPos.xyz / period);
        albedo *= 1.0 + 0.10 * (n - 0.5);
    }

    vec4 terraColor = vec4(albedo, outAlpha);
    terraColor.rgb *= shade * clamp(sunColor * clamp(dayLight, 0.0, 1.0), 0.02, 1.0);

        // Clamp lit albedo so dusk sunColor cannot blow LOD into a saturated orange band.
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

