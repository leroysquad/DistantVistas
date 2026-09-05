#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Fog handling, transition push-down and structure adapted from Farseer's region.vsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColorIn;

uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform float farViewDistance;
uniform float overdrawStart; // DH-style: LOD sink/band start as fraction of viewDistance
// viewDistance comes from fogandlight.vsh include - do not redeclare
uniform float pastViewHaze;
uniform float disableLodFog;
uniform float lookDown;

// Which of this section's four sides border on area we have NO captured data for
// (-X, +X, -Z, +Z; 1 = open). Client-side-only means coverage is whatever the
// server has streamed us, so the cache genuinely runs out mid-air along the edges
// of wherever the player has been. Those boundaries are dissolved into the
// horizon rather than left standing as cliffs.
uniform vec4 openEdges;
uniform float sectionSize;

// Keep-origin slot colour per vertex. Spatial climate and live season are
// applied per fragment in world XZ so greedy 64-block quads are not plates.
// Must equal LodTintRegistry.MaxSlots; StaticAssetChecks pins the match.
const int TINT_SLOTS = 64;
uniform vec4 tintsLow[TINT_SLOTS];
uniform vec4 tintsHigh[TINT_SLOTS];
uniform float tintYLow;
uniform float tintYHigh;

out vec3 tint;
out vec4 worldPos;
out vec4 vertexColor;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float fogAmount;
out float edgeFade;
out float nearFade;
out float tintBlend;
// Section-local XZ, for the gap-fill clip rectangle in the fragment shader.
out vec2 localXZ;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    yLevel = vertexPositionIn.y;
    vertexColor = vertexColorIn;
    localXZ = vertexPositionIn.xz;

    int slotRaw = int(vertexColorIn.a * 255.0 + 0.5);
    int band = slotRaw / TINT_SLOTS;
    int slot = clamp(slotRaw - band * TINT_SLOTS, 0, TINT_SLOTS - 1);
    bool bakedAlbedo = band == 3;
    tintBlend = clamp((yLevel - tintYLow) / max(1.0, tintYHigh - tintYLow), 0.0, 1.0);

    // Keep-origin slot colour only. Fragment shader applies world-space climate
    // and live season. Never seas/clim on baked RGB (0.7.84/85 purple).
    if (bakedAlbedo) {
        tint = vec3(1.0);
    } else {
        tint = mix(tintsLow[slot].rgb, tintsHigh[slot].rgb, tintBlend);
        // Slot 0 is identity. A snow-row high sample must not bleach captured grass
        // or soil tops after vanilla unloads; copy the valley tint instead. Only
        // clamp toward white (0.78, same as 0.7.18). 0.65 crushed greens to grey.
        if (slot > 0) {
            float tintLum = (tint.r + tint.g + tint.b) * 0.333333;
            float tintMx = max(tint.r, max(tint.g, tint.b));
            float tintMn = min(tint.r, min(tint.g, tint.b));
            if ((tintMx - tintMn) < 0.12 && tintLum > 0.62) {
                tint = tintsLow[slot].rgb;
                tintLum = (tint.r + tint.g + tint.b) * 0.333333;
            }
            if (tintLum > 0.78) tint *= 0.78 / max(tintLum, 0.001);
        }
    }

    worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    // 0 at the start of the LOD band (inside vanilla terrain), 1 at the far edge.
    // Negative dist used to discard a camera-locked disc and cut a sky circle
    // through hills. Clamp to 0 so near fragments still draw; vanilla depth
    // plus the sink hides them on loaded chunks.
    // dist == 1 is farViewDistance itself, the captured rim. An extra 512
    // pulled inside the denominator put the far discard / sky fade one ring
    // of tiles inside the land we hold, a second camera-centred cut.
    float distStart = viewDistance * clamp(overdrawStart, 0.15, 0.95);
    float radial = length(worldPos.xz);
    dist = (radial - distStart) / max(64.0, farViewDistance - distStart);
    // clamp(dist, 0.0, dist) is undefined when dist < 0 (min > max).
    dist = max(0.0, dist);

    // Sink LOD terrain into the ground near the transition ring so the seam with real
    // chunks reads as terrain, not a floating shelf.
    //
    // Measured in BLOCKS from the start of the band, not as a fraction of it: dist is
    // normalised over the whole cache, which grows as the player explores, so a
    // fractional ramp changed width depending on how much of the world had been
    // visited -- 86 blocks at a 5000-block edge, 390 at 20000.
    //
    // smoothstep rather than a linear ramp: a straight rise stops dead when it reaches
    // full height and leaves a visible crease right where it finishes. This eases out
    // to zero slope at both ends, so the sink is still there but the top of the bend
    // is not something the eye can catch.
    //
    // Inside the ring (intoBand < 0) keep the full sink so overlap with vanilla does
    // not z-fight the floor. Looking down used to zero the whole sink, so LOD that
    // still overlapped loaded ice sat on the vanilla floor and flickered. Keep the
    // sink next to the camera; only let go past the vanilla ring, where the mesh
    // has to sit at the real surface.
    const float SINK_DEPTH = 5.0;
    const float SINK_FADE_BLOCKS = 110.0;
    float intoBand = radial - distStart;
    float sink = SINK_DEPTH * (1.0 - smoothstep(0.0, SINK_FADE_BLOCKS, max(intoBand, 0.0)));
    // Near-ring lift is BLOCKS from overdraw start, same reason as the sink:
    // fractional dist stretches with the explored cache and would wash the far haze.
    const float NEAR_LIFT_BLOCKS = 180.0;
    nearFade = 1.0 - smoothstep(0.0, NEAR_LIFT_BLOCKS, max(intoBand, 0.0));
    float lookDownFar = clamp(lookDown, 0.0, 1.0) * smoothstep(distStart * 0.5, distStart + 80.0, radial);
    worldPos.y -= sink * (1.0 - lookDownFar);

    // Distance into the section from each open side, as a 0..1 ramp over the outer
    // third. Vertex positions are section-local, so this is just the local x/z.
    float fadeWidth = max(8.0, sectionSize * 0.34);
    vec4 inset = vec4(
        vertexPositionIn.x,
        sectionSize - vertexPositionIn.x,
        vertexPositionIn.z,
        sectionSize - vertexPositionIn.z);
    vec4 nearness = clamp(1.0 - inset / fadeWidth, 0.0, 1.0) * openEdges;
    edgeFade = max(max(nearness.x, nearness.y), max(nearness.z, nearness.w));

    // Only at the TRUE far edge of the LOD band. Mid-horizon openEdges dissolve
    // was turning incomplete mountain ranges into floating peaks against the sky.
    edgeFade *= smoothstep(0.72, 0.98, dist);

    // Vanilla chunks always getFogLevel + applyFog. DisableLodFog used to zero that, so
    // the overdraw ring stayed crisp against fogged foreground. Match ambient fog;
    // extra pastViewHaze is the only thing DisableLodFog still skips.
    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    if (disableLodFog < 0.5) {
        float pastCut = clamp((length(worldPos.xz) - viewDistance * 0.65) / max(64.0, viewDistance * 0.55), 0.0, 1.0);
        fogAmount = max(fogAmount, pastCut * pastCut * pastViewHaze);
    }
    rgbaFog = rgbaFogIn;

    vec4 camPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * camPos;
}

