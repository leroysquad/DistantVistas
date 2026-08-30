#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in int colormapData;
 #endif



uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float forcedTransparency;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

out vec4 rgba;
out vec4 rgbaFog;
out float fogAmount;
out vec2 uv;
out vec4 worldPos;
out vec3 vertexPos;

flat out int renderFlags;
flat out vec3 normal;
out float normalShadeIntensity;

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexID / 4];
	int vIndex = gl_VertexID & 0x03;
	renderFlags = vdata.flags[vIndex];
	vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	
 #endif

	vertexPos = xyz;
	
	vec4 truePos = vec4(xyz + origin, 1.0);
	worldPos = truePos;
	
	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;
	
	gl_Position = projectionMatrix * cameraPos;

	calcShadowMapCoords(modelViewMatrix, worldPos);

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgba.a = max(0.0, 1.0 - forcedTransparency); // Distant Vistas: disable vanilla view-distance edge fade
	
	rgbaFog = rgbaFogIn;
	
	// To fix Z-Fighting on blocks over certain other blocks. 
	if (gl_Position.z > 0) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / max(3, gl_Position.z * 0.05);
	}
	
	normal = unpackNormal(renderFlags);
	normalShadeIntensity = min(1, rgbaLightIn.a * 1.5);
}