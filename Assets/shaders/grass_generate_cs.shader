HEADER
{
	DevShader = true;
	Description = "Generates and culls grass blade instances from a painted density map";
}

MODES
{
	Default();
}

COMMON
{
	#include "system.fxc"
	#include "common.fxc"
	#include "grass_shared.fxc"
}

CS
{
	StructuredBuffer<GrassChunk> GrassChunks < Attribute( "GrassChunks" ); >;
	StructuredBuffer<GrassCell> GrassCells < Attribute( "GrassCells" ); >;
	StructuredBuffer<int> GrassVisibleChunks < Attribute( "GrassVisibleChunks" ); >;

	// The six view frustum planes as float4( normal.xyz, -distance ), uploaded by the CPU from
	// CameraComponent.GetFrustum(). A point is inside when dot( plane.xyz, p ) + plane.w >= 0.
	StructuredBuffer<float4> GrassFrustumPlanes < Attribute( "GrassFrustumPlanes" ); >;

	AppendStructuredBuffer<GrassBlade> GrassBlades < Attribute( "GrassBlades" ); >;

	int GrassVisibleChunkCount < Attribute( "GrassVisibleChunkCount" ); >;

	float3 GrassCameraPos < Attribute( "GrassCameraPos" ); >;

	float GrassMaxDistance < Attribute( "GrassMaxDistance" ); >;
	float GrassFadeStart < Attribute( "GrassFadeStart" ); >;
	int GrassBladesPerCell < Attribute( "GrassBladesPerCell" ); >;
	float GrassSlopeLimit < Attribute( "GrassSlopeLimit" ); >;
	float2 GrassBladeHeight < Attribute( "GrassBladeHeight" ); >;
	float2 GrassBladeWidth < Attribute( "GrassBladeWidth" ); >;

	uint HashU( uint x )
	{
		x ^= x >> 16;
		x *= 0x7feb352du;
		x ^= x >> 15;
		x *= 0x846ca68bu;
		x ^= x >> 16;
		return x;
	}

	float HashF( uint x )
	{
		return HashU( x ) * ( 1.0 / 4294967296.0 );
	}

	bool SphereInFrustum( float3 center, float radius )
	{
		[unroll]
		for ( int i = 0; i < 6; i++ )
		{
			float4 plane = GrassFrustumPlanes[i];
			if ( dot( plane.xyz, center ) + plane.w < -radius )
				return false;
		}

		return true;
	}

	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 vThreadId : SV_DispatchThreadID )
	{
		uint tid = vThreadId.x;

		uint slot = tid / GRASS_CELLS_PER_CHUNK;
		if ( slot >= (uint)GrassVisibleChunkCount )
			return;

		uint cellIndex = tid % GRASS_CELLS_PER_CHUNK;

		GrassChunk chunk = GrassChunks[GrassVisibleChunks[slot]];
		GrassCell cell = GrassCells[chunk.CellOffset + cellIndex];

		uint density8 = cell.Packed & 0xFF;
		if ( density8 == 0 )
			return;

		// Ground normal was baked at paint time; z is reconstructed since the blade never points down.
		float nx = ( ( cell.Packed >> 8 ) & 0xFF ) / 127.5 - 1.0;
		float ny = ( ( cell.Packed >> 16 ) & 0xFF ) / 127.5 - 1.0;
		float nz = sqrt( saturate( 1.0 - nx * nx - ny * ny ) );
		float3 groundNormal = float3( nx, ny, nz );

		if ( groundNormal.z < GrassSlopeLimit )
			return;

		uint cx = cellIndex % GRASS_CHUNK_RESOLUTION;
		uint cy = cellIndex / GRASS_CHUNK_RESOLUTION;
		float2 cellMin = chunk.Origin + float2( cx, cy ) * GRASS_CELL_SIZE;
		float2 cellCenter = cellMin + GRASS_CELL_SIZE * 0.5;

		float distance = length( float3( cellCenter, cell.Height ) - GrassCameraPos );
		if ( distance > GrassMaxDistance )
			return;

		// Thin the field out with distance. Squared because coverage falls off with screen area,
		// so blade count drops at the same rate the cell shrinks on screen.
		float fadeRange = max( GrassMaxDistance - GrassFadeStart, 1.0 );
		float lod = 1.0 - saturate( ( distance - GrassFadeStart ) / fadeRange );
		lod *= lod;

		float density = density8 / 255.0;

		// Round up rather than to nearest, so the blade that is only fractionally "there" still
		// gets generated. It is scaled down to nothing below, which is what stops blades from
		// popping into existence as the camera approaches.
		float exactCount = GrassBladesPerCell * density * lod;
		int count = (int)ceil( exactCount );
		if ( count <= 0 )
			return;

		float partial = exactCount - floor( exactCount );
		float lastBladeScale = ( partial > 0.0 ) ? partial : 1.0;

		uint seed = HashU( asuint( cellMin.x ) ) ^ HashU( asuint( cellMin.y ) * 7919u );

		// Plane through the cell centre, so blades follow the slope instead of stair-stepping
		// at cell boundaries. Avoids fetching neighbouring cells entirely.
		float invNz = 1.0 / max( groundNormal.z, 0.1 );

		for ( int i = 0; i < count; i++ )
		{
			uint s = HashU( seed + (uint)i * 0x9e3779b9u );

			float rx = HashF( s );
			float ry = HashF( s ^ 0x68bc21ebu );
			float rYaw = HashF( s ^ 0x02e5be93u );
			float rSize = HashF( s ^ 0x1b56c4e9u );
			float rTint = HashF( s ^ 0x3c6ef372u );
			float rPhase = HashF( s ^ 0x7f4a7c15u );

			float2 xy = cellMin + float2( rx, ry ) * GRASS_CELL_SIZE;
			float2 offset = xy - cellCenter;
			float z = cell.Height - ( groundNormal.x * offset.x + groundNormal.y * offset.y ) * invNz;

			// Only the final blade of the cell is partial, so a cell's blade count grows one
			// blade at a time and each one scales up from zero as the camera closes in.
			float scale = ( i == count - 1 ) ? lastBladeScale : 1.0;

			float height = lerp( GrassBladeHeight.x, GrassBladeHeight.y, rSize ) * scale;

			// Test the blade's midpoint with a radius covering its full extent, so a blade
			// leaning into view at the screen edge isn't culled.
			if ( !SphereInFrustum( float3( xy, z + height * 0.5 ), height ) )
				continue;

			GrassBlade blade;
			blade.Position = float3( xy, z );
			blade.Yaw = rYaw * 6.28318531;
			blade.Normal = groundNormal;
			blade.Height = height;
			blade.Width = lerp( GrassBladeWidth.x, GrassBladeWidth.y, rSize ) * scale;
			blade.Tint = rTint;
			blade.Phase = rPhase;
			blade.Pad = 0;

			GrassBlades.Append( blade );
		}
	}
}
