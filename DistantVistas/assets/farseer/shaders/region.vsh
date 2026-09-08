#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// DV_FARSEER_OVERLAY
// Distant Vistas overlay of Farseer's region.vsh (MIT, Badgerson).
// Late-only onset (FarseerVisitOnset uniforms): early and late both use
// HorizonDrawScale (4.5x VD). Midground stays Distant Vistas; LodFrontierScout
// fills capture toward this rim. Cap onset at FarViewDistance when shorter.
// No stock Y-sink trench. ASCII-only comments (NVIDIA GLSL).

layout(location = 0) in vec3 vertexPositionIn;

uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform float farViewDistance;
uniform float globeEffect;

uniform sampler2D visitMask;
uniform float visitMaskReady;
uniform float visitOnsetEarly;
uniform float visitOnsetLate;
uniform vec2 camWorldXZ;
uniform vec2 visitMaskOrigin;
uniform float visitMaskSize;

out vec4 worldPos;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float fogAmount;
out float nightVisionStrengthv;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    yLevel = vertexPositionIn.y;
    worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    // worldPos.xz is camera-relative (Farseer modelMatrix subtracts cam).
    float visited = 1.0;
    if (visitMaskReady > 0.5 && visitMaskSize > 1.0)
    {
        vec2 absXZ = worldPos.xz + camWorldXZ;
        vec2 uv = (absXZ - visitMaskOrigin) / visitMaskSize;
        if (uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0)
            visited = texture(visitMask, uv).r;
        else
            visited = 0.0;
    }

    float onsetScale = mix(visitOnsetEarly, visitOnsetLate, clamp(visited, 0.0, 1.0));
    float distStart = viewDistance * onsetScale;
    float maxStart = farViewDistance;
    if (distStart > maxStart) distStart = maxStart;
    if (distStart < viewDistance * 0.5) distStart = viewDistance * 0.5;
    dist = (length(worldPos.xz) - distStart) / max(64.0, farViewDistance - distStart);

    // No stock Y-sink. That dug a trench through mountains at the onset band.
    worldPos.y -= globeEffect * pow(max(0.0, dist), 2.0) * farViewDistance;

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

    rgbaFog = rgbaFogIn;
    nightVisionStrengthv = nightVisionStrength;

    vec4 camPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * camPos;
}
