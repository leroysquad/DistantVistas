#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// DV_FARSEER_OVERLAY
// Distant Vistas overlay of Farseer's region.fsh (MIT, Badgerson).
// Stock SkyTint (players crank it to 5-10) plus mix-to-sky and sphere fog
// paints the heightmap as sky, so the silhouette vanishes and clouds get cut.
// Clamp tint, skip sphere fog, only dissolve at the far rim. ColorTint alpha
// is also clamped so a 0.4 slate wash cannot hide relief.

in vec4 worldPos;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float fogAmount;
in float nightVisionStrengthv;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

uniform float skyTint;
uniform vec4 colorTint;
uniform float lightLevelBias;
uniform float fadeBias;
uniform int seaLevel;

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

float bias(float value, float b) {
  float expv = log(0.5) / log(b);
  return pow(value, expv);
}

vec3 bias(vec3 value, float b) {
  float expv = log(0.5) / log(b);
  return pow(value, vec3(expv));
}

void main()
{
    if (dist < 0.0 || dist > 1.0) discard;

    vec4 terraColor = vec4(1.0);
    vec4 terraGlow = vec4(1.0);
    float a = float(seaLevel) + 2.0;
    float b = float(seaLevel) - 1.0;
    float skyTintSafe = clamp(skyTint, 0.0, 0.4);
    float sealevelOffsetFactor = skyTintSafe + ((yLevel - a) / (b - a)) * (-skyTintSafe);
    getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0.0, 1.0), horizonFog, terraColor, terraGlow);

    vec4 skyColor = vec4(1.0);
    vec4 skyGlow = vec4(1.0);
    vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
    getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
    float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
    skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
    skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

    terraColor.rgb = mix(terraColor.rgb, colorTint.rgb, min(colorTint.a, 0.12));
    terraColor.rgb *= bias(clamp(sunColor * dayLight, 0.0, 1.0), lightLevelBias);
    terraColor.rgb *= 0.78;
    terraColor = applyFog(terraColor, fogAmount);
    terraGlow *= dist;

    float fade = smoothstep(0.88, 1.0, dist);
    fade *= step(0.0, fadeBias + 1.0);
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(vec4(0.0), skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
