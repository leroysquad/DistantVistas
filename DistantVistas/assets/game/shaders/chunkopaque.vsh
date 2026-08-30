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
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in int colormapData;
 #endif

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float cameraUnderwater;

uniform float shadowIntensity = 1;
uniform vec3 lightPosition;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

out vec4 rgba;
out vec2 uv;
out vec4 rgbaFog;
out float fogAmount;
out vec3 normal;
out vec3 vertexPosition;
out vec4 worldPos;
out vec4 camPos;
out float lod0Fade;
out float nb;

 #if SSAOLEVEL > 0
out vec4 gnormal;
 #endif


flat out int renderFlags;

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
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif

	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	bool isLeaves = ((renderFlags & WindModeBitMask) > 0); 
	
	worldPos = applyVertexWarping(renderFlags, truePos);
	worldPos = applyGlobalWarping(worldPos);
	
	camPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * camPos;
	
	calcShadowMapCoords(modelViewMatrix, worldPos);
 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = uvIn;
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);
	
	// Distance fade out
	rgba.a = 1.0; // Distant Vistas: disable vanilla view-distance edge fade (see TopoHorizon/Defog)
	
	rgbaFog = rgbaFogIn;
	
	normal = unpackNormal(renderFlags);
	
#if SSAOLEVEL > 0
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	gnormal.w = isLeaves ? 1 : 0; // Cheap hax to make SSAO on leaves less bad looking;
#endif


	// To fix Z-Fighting on blocks over certain other blocks
	if (gl_Position.z > -1) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);   // The offset helps decors not to become invisible nearby
	}
	


	//  11.3.24: For performance, we now pre-calculate the LOD0 fade alpha value in the vertex shader, and pass it to the fragment shader; this reduces conditionality in the fragment shader

	// Lod 0 fade
	// This makes the lod fade more noticable, actually O_O
	if ((renderFlags & Lod0BitMask) != 0) {
		
		// We made this transition smoother, because it looks better,
		// if you notice chunk popping, revert to the old, harsher transition
		// Radfast and Tyron, May 28 2021 ^_^
		float b = clamp(10 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 2.5, 0.0, 1.0);
		//float b = clamp(20 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 5, 0.0, 1.0);
				
		lod0Fade = 1 - b;
	}
	else    lod0Fade = 0.0;
	

	//  14.3.24: We can also pre-calculate nb

#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
#else
	float intensity = 0.45;
#endif
	nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);
}