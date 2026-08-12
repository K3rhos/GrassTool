HEADER
{
	DevShader = true;
	Description = "Procedural grass blades, generated on the GPU from an instance buffer";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
	#include "grass_shared.fxc"

	StructuredBuffer<GrassBlade> GrassBlades < Attribute( "GrassBlades" ); >;
}

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// x = height along the blade (0 at root, 1 at tip), y = per-blade tint jitter
	float2 vBladeCoords : TEXCOORD8;
};

// The draw supplies no vertex buffer; every position comes from SV_VertexID. The stub keeps
// the shader system's vertex plumbing happy.
struct VS_INPUT
{
	float3 pos : POSITION < Semantic( None ); >;
};

VS
{
	float GrassBladeCurve < Attribute( "GrassBladeCurve" ); Default( 0.25 ); >;
	float GrassAlignToGround < Attribute( "GrassAlignToGround" ); Default( 0.35 ); >;
	float GrassNormalUpBias < Attribute( "GrassNormalUpBias" ); Default( 0.6 ); >;

	float2 GrassWindDirection < Attribute( "GrassWindDirection" ); Default2( 1, 0.35 ); >;
	float GrassWindStrength < Attribute( "GrassWindStrength" ); Default( 0.35 ); >;
	float GrassWindSpeed < Attribute( "GrassWindSpeed" ); Default( 1.6 ); >;
	float GrassWindWaveScale < Attribute( "GrassWindWaveScale" ); Default( 0.0016 ); >;
	float GrassTime < Attribute( "GrassTime" ); Default( 0 ); >;

	PixelInput MainVs( uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID )
	{
		GrassBlade blade = GrassBlades[instanceId];

		// Unpack the triangle list back into a triangle strip. Odd triangles swap their first
		// two corners so the winding stays consistent along the blade.
		uint tri = vertexId / 3;
		uint corner = vertexId % 3;
		uint local = corner;
		if ( tri & 1 )
			local = ( corner == 0 ) ? 1 : ( ( corner == 1 ) ? 0 : 2 );

		uint stripIndex = tri + local;

		uint segment = stripIndex >> 1;
		bool isTip = ( stripIndex == GRASS_BLADE_STRIP_VERTS - 1 );
		float side = isTip ? 0.0 : ( ( stripIndex & 1 ) ? 1.0 : -1.0 );
		float t = (float)segment / (float)GRASS_BLADE_SEGMENTS;

		float3 up = normalize( lerp( float3( 0, 0, 1 ), blade.Normal, GrassAlignToGround ) );
		float3 forward = normalize( float3( cos( blade.Yaw ), sin( blade.Yaw ), 0 ) - up * dot( float3( cos( blade.Yaw ), sin( blade.Yaw ), 0 ), up ) );
		float3 right = normalize( cross( up, forward ) );

		// Travelling wave, so a field ripples rather than every blade swaying in lockstep.
		float2 windDir = normalize( GrassWindDirection + 1e-6 );
		float phase = GrassTime * GrassWindSpeed + blade.Phase * 6.28318531 + dot( blade.Position.xy, windDir ) * GrassWindWaveScale;
		float wind = sin( phase ) * 0.7 + sin( phase * 2.3 + 1.7 ) * 0.3;

		// Quadratic in t keeps the root planted while the tip carries the whole deflection. Clamped
		// because rise below is only meaningful for |bend| <= 1 - past that the blade stops being a
		// bent blade and becomes a long sliver flung sideways, which is both wrong and a source of
		// near-plane interpolation blowups when one lands in front of the camera.
		float bendAmount = clamp( ( GrassBladeCurve + wind * GrassWindStrength ) * t * t, -1.0, 1.0 );

		// Bending costs the blade height, otherwise it visibly stretches as it leans over.
		float rise = sqrt( saturate( 1.0 - bendAmount * bendAmount ) );

		float width = blade.Width * saturate( 1.0 - t * t );

		float3 positionWs = blade.Position;
		positionWs += up * ( blade.Height * t * rise );
		positionWs += forward * ( blade.Height * bendAmount );
		positionWs += right * ( side * width * 0.5 );

		// Tangent runs up the blade, following the same bend the position took.
		float3 tangent = normalize( up * rise + forward * ( 2.0 * bendAmount ) );
		float3 faceNormal = normalize( cross( right, tangent ) );
		float3 normalWs = normalize( lerp( faceNormal, float3( 0, 0, 1 ), GrassNormalUpBias ) );

		PixelInput o = (PixelInput)0;

		o.vPositionWs = positionWs - g_vHighPrecisionLightingOffsetWs.xyz;
		o.vPositionPs = Position3WsToPs( positionWs );
		o.vNormalWs = normalWs;
		o.vTextureCoords = float4( side * 0.5 + 0.5, t, 0, 0 );
		o.vVertexColor = float4( 1, 1, 1, 1 );
		o.vBladeCoords = float2( t, blade.Tint );

		#if ( PS_INPUT_HAS_TANGENT_BASIS )
			o.vTangentUWs = right;
			o.vTangentVWs = tangent;
		#endif

		return o;
	}
}

PS
{
	#define CUSTOM_MATERIAL_INPUTS 1
	#include "common/pixel.hlsl"

	// Blades are single-sided geometry viewed from every angle, so backfaces must render.
    RenderState(CullMode, NONE);

	#include "common/classes/Light.hlsl"
	#include "common/classes/AmbientLight.hlsl"

	float3 GrassRootColor < Attribute( "GrassRootColor" ); Default3( 0.11, 0.20, 0.06 ); >;
	float3 GrassTipColor < Attribute( "GrassTipColor" ); Default3( 0.42, 0.58, 0.18 ); >;
	float GrassColorVariation < Attribute( "GrassColorVariation" ); Default( 0.25 ); >;
	float GrassRootOcclusion < Attribute( "GrassRootOcclusion" ); Default( 0.4 ); >;
	float GrassTransmission < Attribute( "GrassTransmission" ); Default( 0.5 ); >;
	float GrassWrap < Attribute( "GrassWrap" ); Default( 0.5 ); >;

	// Cascade shadows without the screen-space contact shadow mask that
	// DirectionalLightShadow::GetVisibility folds in. That mask is built from the depth prepass,
	// which this grass never takes part in, so at every blade it holds the shadowing of whatever
	// geometry is behind it - the blades end up wearing the silhouette of the background.
	float SunVisibility( float3 worldPosition, float2 screenPosition )
	{
		if ( g_DirectionalLightCascadeCount == 0 )
			return 1.0f;

		// FindCascade is a free function in DirectionalLightShadow.hlsl, not a member of the struct.
		float3 positionLs;
		int cascade = FindCascade( worldPosition, positionLs );

		if ( cascade < 0 )
			return 1.0f;

		return DirectionalLightShadow::SampleCascade( cascade, worldPosition, screenPosition );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// Saturated rather than used raw: a blade clipping the near plane makes perspective-correct
		// interpolation push these well outside 0..1, and tint feeds a multiplier on albedo, so an
		// out-of-range value there shows up as a blown-out white pixel.
		float t = saturate( i.vBladeCoords.x );
		float tint = saturate( i.vBladeCoords.y );

		float3 albedo = lerp( GrassRootColor, GrassTipColor, t );
		albedo *= lerp( 1.0 - GrassColorVariation, 1.0 + GrassColorVariation, tint );

		float3 normalWs = normalize( i.vNormalWs );
		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;

		// Blades are thin and translucent, so light bleeding through from behind matters as much
		// as light landing on the front. Both fall off toward the root, where the canopy is dense.
		float transmission = GrassTransmission * t;
		float occlusion = lerp( 1.0 - GrassRootOcclusion, 1.0, t );

		float3 diffuse = 0.0f;

		uint lightCount = Light::Count( i.vPositionSs );
		for ( uint index = 0; index < lightCount; index++ )
		{
			Light light = Light::From( worldPosition, i.vPositionSs, index );
			
			// Light::From resolves visibility through the screen-space mask; swap in a cascade-only
			// value so the sun still casts real shadows on the grass without that contamination.
			if ( light.LightData.Type == LightType::LightTypeDirectional )
                light.Visibility = SunVisibility(worldPosition, i.vPositionSs.xy);

			float ndotl = dot( normalWs, light.Direction );

			// Wrapped diffuse rather than saturate( ndotl ): a blade is not an opaque solid, and a
			// hard terminator across a field of them reads as a mass of black edges.
			float front = saturate( ( ndotl + GrassWrap ) / ( 1.0f + GrassWrap ) );
			float back = saturate( -ndotl ) * transmission;

			diffuse += light.Color * light.Attenuation * light.Visibility * ( front + back );
		}

        float3 ambient = AmbientLight::From(worldPosition, i.vPositionSs, normalWs);

		float3 color = albedo * ( diffuse + ambient ) * occlusion;

		return float4( color, 1.0f );
	}
}
