HEADER
{
	DevShader = true;
	Description = "Sprite grass: crossed textured cards, generated on the GPU from an instance buffer";
}

FEATURES
{
	#include "common/features.hlsl"

	// Upright cards stand up out of the ground; ground cards lie in it, for moss and flattened
	// grass. It lives on the material rather than the grass definition so a sheet of ground cover
	// cannot be paired with the wrong shape.
	Feature( F_GROUND_CARDS, 0..1, "Sprite" );
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

	// x = height up the card (0 at the root, 1 at the top), y = per-card tint jitter,
	// z = distance fade: 0 at the far edge of the render range, 1 once fully faded in
	float3 vCardCoords : TEXCOORD8;

	// Cards render both faces, so the lighting has to know which one it is on. The engine only
	// puts this in pixelinput.hlsl under S_RENDER_BACKFACES, which this shader does not set.
	#if ( PROGRAM == VFX_PROGRAM_PS )
		bool vIsFrontFace : SV_IsFrontFace;
	#endif
};

// The draw supplies no vertex buffer; every position comes from SV_VertexID. The stub keeps
// the shader system's vertex plumbing happy.
struct VS_INPUT
{
	float3 pos : POSITION < Semantic( None ); >;
};

VS
{
	float GrassAlignToGround < Attribute( "GrassAlignToGround" ); Default( 0.35 ); >;
	float GrassBladeCurve < Attribute( "GrassBladeCurve" ); Default( 0.25 ); >;

	int GrassCardPlanes < Attribute( "GrassCardPlanes" ); Default( 2 ); >;
	int GrassSpriteColumns < Attribute( "GrassSpriteColumns" ); Default( 4 ); >;
	int GrassSpriteRows < Attribute( "GrassSpriteRows" ); Default( 4 ); >;

	float GrassMaxDistance < Attribute( "GrassMaxDistance" ); Default( 4000 ); >;
	float GrassCardFadeRange < Attribute( "GrassCardFadeRange" ); Default( 800 ); >;

	float2 GrassWindDirection < Attribute( "GrassWindDirection" ); Default2( 1, 0.35 ); >;
	float GrassWindStrength < Attribute( "GrassWindStrength" ); Default( 0.35 ); >;
	float GrassWindSpeed < Attribute( "GrassWindSpeed" ); Default( 1.6 ); >;
	float GrassWindWaveScale < Attribute( "GrassWindWaveScale" ); Default( 0.0016 ); >;
	float GrassTime < Attribute( "GrassTime" ); Default( 0 ); >;

	StaticCombo( S_GROUND_CARDS, F_GROUND_CARDS, Sys( ALL ) );

	#if S_GROUND_CARDS
		// How far each plane tips out of the ground plane, in degrees. Alternating directions turn a
		// flat pair into a shallow X, which is what keeps a patch from reading as a decal.
		float g_flGroundTilt < Default( 12.0 ); Range( 0.0, 45.0 ); UiGroup( "Sprite,10/30" ); >;

		// Lifts the patch clear of the surface it sits on, so it does not z-fight the ground.
		float g_flGroundLift < Default( 2.0 ); Range( 0.0, 64.0 ); UiGroup( "Sprite,10/40" ); >;
	#endif

	PixelInput MainVs( uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID )
	{
		GrassBlade card = GrassBlades[instanceId];

		uint plane = vertexId / GRASS_CARD_PLANE_VERTS;
		uint corner = vertexId % GRASS_CARD_PLANE_VERTS;

		// Quad corners are 0 = root left, 1 = root right, 2 = top left, 3 = top right. This walks
		// them as the two triangles ( 0, 1, 2 ) and ( 1, 2, 3 ), which tile the quad. Winding is
		// not worth getting right here - cards render both faces.
		uint quadCorner = corner % 3 + corner / 3;
		float2 cardUv = float2( quadCorner & 1, ( quadCorner >> 1 ) & 1 );

		float3 up = normalize( lerp( float3( 0, 0, 1 ), card.Normal, GrassAlignToGround ) );

		// Planes spread over a half turn, which is all there is: turned any further, a plane just
		// repeats an earlier one seen from the other side. Two of them give the classic cross.
		float planeAngle = card.Yaw + ( (float)plane / (float)max( GrassCardPlanes, 1 ) ) * 3.14159265;
		float3 facing = float3( cos( planeAngle ), sin( planeAngle ), 0 );

		// One of the card's axes, in the ground plane either way.
		float3 right = normalize( facing - up * dot( facing, up ) );

		float3 positionWs;
		float3 cardNormal;
		float3 tangentV;

		// How far up the card a pixel is, for the shading gradient toward the root.
		float shadeHeight;

		#if S_GROUND_CARDS
			// Both axes lie in the ground, so the quad is a flat square patch rather than a standing
			// sheet. Width sizes both of them - Height has no meaning for a card with no height.
			float3 alongB = cross( up, right );

			// Each plane tips the opposite way, so in profile the pair reads as a shallow X. Flat
			// planes stacked at the same angle would just look like one decal.
			float tilt = radians( g_flGroundTilt ) * ( ( plane & 1 ) ? -1.0 : 1.0 );
			float3 tiltedB = alongB * cos( tilt ) + up * sin( tilt );

			positionWs = card.Position + up * g_flGroundLift;
			positionWs += right * ( ( cardUv.x - 0.5 ) * card.Width );
			positionWs += tiltedB * ( ( cardUv.y - 0.5 ) * card.Width );

			cardNormal = normalize( cross( right, tiltedB ) );
			tangentV = tiltedB;

			// No part of a ground card is nearer the root than any other, so the gradient the upright
			// ones use would only darken one edge of the patch. Shade it flat.
			shadeHeight = 1.0;

			// Ground cover does not sway - moss on a rock has nothing to bend.
		#else
			// The card spans its width along 'right' and its height along 'up'.
			cardNormal = normalize( cross( right, up ) );

			// Travelling wave, so a field ripples rather than every card leaning in lockstep.
			float2 windDir = normalize( GrassWindDirection + 1e-6 );
			float phase = GrassTime * GrassWindSpeed + card.Phase * 6.28318531 + dot( card.Position.xy, windDir ) * GrassWindWaveScale;
			float wind = sin( phase ) * 0.7 + sin( phase * 2.3 + 1.7 ) * 0.3;

			float v = cardUv.y;

			// Quadratic in height keeps the root planted while the top carries the whole deflection.
			// The lean is along the wind rather than along each card's own yaw, so every plane of a
			// card displaces by the same world vector and the cross stays rigid instead of shearing.
			// Clamped because 'rise' below is only meaningful for |bend| <= 1.
			float bend = clamp( ( GrassBladeCurve + wind * GrassWindStrength ) * v * v, -1.0, 1.0 );

			// Leaning costs the card height, otherwise it visibly stretches as it goes over.
			float rise = sqrt( saturate( 1.0 - bend * bend ) );

			positionWs = card.Position;
			positionWs += up * ( card.Height * v * rise );
			positionWs += float3( windDir, 0.0 ) * ( card.Height * bend );
			positionWs += right * ( ( cardUv.x - 0.5 ) * card.Width );

			tangentV = normalize( up * rise + float3( windDir, 0.0 ) * ( 2.0 * bend ) );
			shadeHeight = v;
		#endif

		// Which sprite of the sheet, as a cell of a columns x rows grid. The card's v runs up from
		// the root while a sheet's runs down from the top, hence the flip.
		uint columns = (uint)max( GrassSpriteColumns, 1 );
		uint rows = (uint)max( GrassSpriteRows, 1 );
		uint variant = (uint)card.Variant;

		float2 cell = float2( variant % columns, variant / columns );
		float2 sheetUv = ( cell + float2( cardUv.x, 1.0 - cardUv.y ) ) / float2( columns, rows );

		// Fades in over the last stretch of the render range. A card entering range appears at the
		// far edge, where this is 0, so one term covers both the far edge and streaming in.
		float viewDistance = length( card.Position - g_vCameraPositionWs );
		float fade = saturate( ( GrassMaxDistance - viewDistance ) / max( GrassCardFadeRange, 1.0 ) );

		PixelInput o = (PixelInput)0;

		o.vPositionWs = positionWs - g_vHighPrecisionLightingOffsetWs.xyz;
		o.vPositionPs = Position3WsToPs( positionWs );
		o.vNormalWs = cardNormal;
		o.vTextureCoords = float4( sheetUv, 0, 0 );
		o.vVertexColor = float4( 1, 1, 1, 1 );
		o.vCardCoords = float3( shadeHeight, card.Tint, fade );

		#if ( PS_INPUT_HAS_TANGENT_BASIS )
			o.vTangentUWs = right;
			o.vTangentVWs = tangentV;
		#endif

		return o;
	}
}

PS
{
	#define CUSTOM_MATERIAL_INPUTS 1
	#include "common/pixel.hlsl"

	// A card is a flat sheet seen from every angle, so both faces must render.
	RenderState( CullMode, NONE );

	#include "grass_lighting.fxc"

	// The sheet comes from the material, not from a render attribute. A texture handed straight to
	// an attribute sits outside the bookkeeping the engine hangs off materials: texture streaming
	// never sees it being used, so it evicts the top mips out from under the draw and the cards go
	// soft and dark until something touches the asset again.
	//
	// The cutout needs its own greyscale image rather than the alpha channel of the colour one. A
	// scalar input reads an image's luminance, so pointing this at an RGBA sheet does not fail
	// loudly - it quietly gives every card a solid rectangle with the plant cut out of it.
	CreateInputTexture2D( SpriteSheetColor, Srgb, 8, "", "_color", "Sprite,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( SpriteSheetAlpha, Linear, 8, "", "_alpha", "Sprite,10/20", Default( 1.0 ) );

	Texture2D g_tGrassSprites < Channel( RGB, Box( SpriteSheetColor ), Srgb ); Channel( A, Box( SpriteSheetAlpha ), Linear ); OutputFormat( BC7 ); SrgbRead( true ); >;

	// Clamped, so filtering at a tile's edge cannot wrap around to the far side of the sheet.
	SamplerState g_sGrassSprites < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float3 GrassCardTint < Attribute( "GrassCardTint" ); Default3( 1, 1, 1 ); >;
	float GrassColorVariation < Attribute( "GrassColorVariation" ); Default( 0.25 ); >;
	float GrassRootOcclusion < Attribute( "GrassRootOcclusion" ); Default( 0.4 ); >;
	float GrassTransmission < Attribute( "GrassTransmission" ); Default( 0.5 ); >;
	float GrassNormalUpBias < Attribute( "GrassNormalUpBias" ); Default( 0.6 ); >;
	float GrassAlphaCutoff < Attribute( "GrassAlphaCutoff" ); Default( 0.35 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// Saturated rather than used raw: a card clipping the near plane makes perspective-correct
		// interpolation push these well outside 0..1, and each one feeds a multiplier below.
		float t = saturate( i.vCardCoords.x );
		float tint = saturate( i.vCardCoords.y );
		float fade = saturate( i.vCardCoords.z );

		float4 sheet = Tex2DS( g_tGrassSprites, g_sGrassSprites, i.vTextureCoords.xy );

		// Interleaved gradient noise: one line, and far less structured than an ordered matrix, so
		// the fade reads as grain rather than a crawling grid. Either test discards - the sprite's
		// own cutout, or the distance fade - and whatever survives is fully opaque, so thousands
		// of cards need no sorting and no blending.
		float dither = frac( 52.9829189 * frac( dot( i.vPositionSs.xy, float2( 0.06711056, 0.00583715 ) ) ) );
		clip( min( sheet.a - GrassAlphaCutoff, fade - dither ) );

		float3 albedo = sheet.rgb * GrassCardTint;
		albedo *= lerp( 1.0 - GrassColorVariation, 1.0 + GrassColorVariation, tint );

		// The card's own normal, flipped on the far face before the bias toward up goes on -
		// biasing first and flipping after would leave a backface pointing into the ground.
		float3 cardNormal = normalize( i.vNormalWs ) * ( i.vIsFrontFace ? 1.0 : -1.0 );

		// Grass lit by its true geometric normal reads as a mass of hard edges; pushing the normal
		// toward world up keeps a field looking like one soft surface.
		float3 normalWs = normalize( lerp( cardNormal, float3( 0, 0, 1 ), GrassNormalUpBias ) );

		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;

		// A card is a cluster of thin leaves, so light bleeding through from behind matters as much
		// as light landing on the front. Both fall off toward the root, where the canopy is dense.
		float transmission = GrassTransmission * t;
		float occlusion = lerp( 1.0 - GrassRootOcclusion, 1.0, t );

		float3 color = GrassShade( albedo, normalWs, worldPosition, i.vPositionSs, transmission, occlusion );

		// The same call ShadingModelStandard::Shade ends on, so gradient, cubemap and volumetric fog
		// all match the rest of the scene. Without it the grass stays saturated while everything
		// around it fades into the distance. Costs nothing when the scene has no fog - it early-outs
		// on g_bFogEnabled.
		return DoAtmospherics( worldPosition, i.vPositionSs.xy, float4( color, 1.0f ) );
	}
}
