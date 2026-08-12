using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sandbox;

namespace RedSnail.GrassTool;

/// <summary>
/// Sparse painted grass coverage, stored as a chunked grid of density samples rather than
/// individual blade transforms. Blades are generated procedurally on the GPU from this data,
/// so a square kilometre of dense grass costs a few megabytes instead of hundreds.
/// </summary>
public sealed class GrassStorage : BlobData
{
	public override int Version => 1;

	/// <summary>Cells along one edge of a chunk.</summary>
	public const int ChunkResolution = 64;

	/// <summary>World-space size of a single density cell, in source units.</summary>
	public const float CellSize = 32.0f;

	/// <summary>World-space size of a chunk edge, in source units.</summary>
	public const float ChunkSize = ChunkResolution * CellSize;

	public const int CellsPerChunk = ChunkResolution * ChunkResolution;

	/// <summary>
	/// One density sample. Height and normal are baked when painting so grass sits on any
	/// geometry, not just terrain. Matches the HLSL <c>GrassCell</c> struct exactly.
	/// </summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Cell
	{
		public float Height;

		/// <summary>density (0-7) | normal.x (8-15) | normal.y (16-23) | unused (24-31)</summary>
		public uint Packed;

		public readonly float Density => (Packed & 0xFF) / 255.0f;

		public readonly Vector3 Normal
		{
			get
			{
				var x = ((Packed >> 8) & 0xFF) / 127.5f - 1.0f;
				var y = ((Packed >> 16) & 0xFF) / 127.5f - 1.0f;
				var z = MathF.Sqrt( Math.Clamp( 1.0f - x * x - y * y, 0.0f, 1.0f ) );
				return new Vector3( x, y, z );
			}
		}

		public static uint Pack( float density, Vector3 normal )
		{
			var d = (uint)Math.Clamp( density * 255.0f + 0.5f, 0.0f, 255.0f );
			var nx = (uint)Math.Clamp( (normal.x + 1.0f) * 127.5f + 0.5f, 0.0f, 255.0f );
			var ny = (uint)Math.Clamp( (normal.y + 1.0f) * 127.5f + 0.5f, 0.0f, 255.0f );
			return d | (nx << 8) | (ny << 16);
		}
	}

	public readonly record struct ChunkCoord( int X, int Y );

	private readonly Dictionary<ChunkCoord, Cell[]> _chunks = [];

	/// <summary>Bumped on every mutation so the renderer knows to re-upload its GPU buffers.</summary>
	public int Revision { get; private set; }

	public int ChunkCount => _chunks.Count;

	public IReadOnlyDictionary<ChunkCoord, Cell[]> Chunks => _chunks;

	public static ChunkCoord WorldToChunk( Vector3 world ) => new(
		(int)MathF.Floor( world.x / ChunkSize ),
		(int)MathF.Floor( world.y / ChunkSize ) );

	public static Vector2 ChunkOrigin( ChunkCoord coord ) => new( coord.X * ChunkSize, coord.Y * ChunkSize );

	/// <summary>Global cell index on an axis. Negative world positions floor correctly.</summary>
	private static int WorldToCell( float world ) => (int)MathF.Floor( world / CellSize );

	private static int FloorDiv( int a, int b ) => a >= 0 ? a / b : ~(~a / b);

	private static int Mod( int a, int b )
	{
		var r = a % b;
		return r < 0 ? r + b : r;
	}

	/// <summary>
	/// Writes a density sample at a world position, baking the surface height and normal alongside it.
	/// Density of zero frees the sample.
	/// </summary>
	public void SetCell( float worldX, float worldY, float density, float height, Vector3 normal )
	{
		var cellX = WorldToCell( worldX );
		var cellY = WorldToCell( worldY );
		var coord = new ChunkCoord( FloorDiv( cellX, ChunkResolution ), FloorDiv( cellY, ChunkResolution ) );

		if ( !_chunks.TryGetValue( coord, out var cells ) )
		{
			if ( density <= 0.0f ) return;

			cells = new Cell[CellsPerChunk];
			_chunks[coord] = cells;
		}

		var index = Mod( cellY, ChunkResolution ) * ChunkResolution + Mod( cellX, ChunkResolution );
		cells[index] = new Cell { Height = height, Packed = Cell.Pack( density, normal ) };
		Revision++;
	}

	public Cell GetCell( float worldX, float worldY )
	{
		var cellX = WorldToCell( worldX );
		var cellY = WorldToCell( worldY );
		var coord = new ChunkCoord( FloorDiv( cellX, ChunkResolution ), FloorDiv( cellY, ChunkResolution ) );

		if ( !_chunks.TryGetValue( coord, out var cells ) )
			return default;

		return cells[Mod( cellY, ChunkResolution ) * ChunkResolution + Mod( cellX, ChunkResolution )];
	}

	/// <summary>
	/// Reduces density in a radius, removing samples that reach zero.
	/// </summary>
	public void Erase( Vector3 center, float radius, float strength )
	{
		var radiusSq = radius * radius;
		var minCellX = WorldToCell( center.x - radius );
		var maxCellX = WorldToCell( center.x + radius );
		var minCellY = WorldToCell( center.y - radius );
		var maxCellY = WorldToCell( center.y + radius );

		for ( var cy = minCellY; cy <= maxCellY; cy++ )
		{
			for ( var cx = minCellX; cx <= maxCellX; cx++ )
			{
				var coord = new ChunkCoord( FloorDiv( cx, ChunkResolution ), FloorDiv( cy, ChunkResolution ) );
				if ( !_chunks.TryGetValue( coord, out var cells ) )
					continue;

				var wx = (cx + 0.5f) * CellSize;
				var wy = (cy + 0.5f) * CellSize;
				var dx = wx - center.x;
				var dy = wy - center.y;
				if ( dx * dx + dy * dy > radiusSq )
					continue;

				var index = Mod( cy, ChunkResolution ) * ChunkResolution + Mod( cx, ChunkResolution );
				ref var cell = ref cells[index];
				if ( (cell.Packed & 0xFF) == 0 )
					continue;

				var density = Math.Max( cell.Density - strength, 0.0f );
				cell.Packed = density <= 0.0f ? 0u : Cell.Pack( density, cell.Normal );
				Revision++;
			}
		}

		PruneEmptyChunks();
	}

	public void ClearAll()
	{
		if ( _chunks.Count == 0 ) return;

		_chunks.Clear();
		Revision++;
	}

	private void PruneEmptyChunks()
	{
		List<ChunkCoord> empty = null;

		foreach ( var (coord, cells) in _chunks )
		{
			var used = false;
			for ( var i = 0; i < cells.Length; i++ )
			{
				if ( (cells[i].Packed & 0xFF) != 0 ) { used = true; break; }
			}

			if ( !used )
			{
				empty ??= [];
				empty.Add( coord );
			}
		}

		if ( empty is null ) return;

		foreach ( var coord in empty )
			_chunks.Remove( coord );
	}

	public override void Serialize( ref Writer writer )
	{
		writer.Stream.Write( _chunks.Count );

		foreach ( var (coord, cells) in _chunks )
		{
			writer.Stream.Write( coord.X );
			writer.Stream.Write( coord.Y );

			for ( var i = 0; i < CellsPerChunk; i++ )
			{
				writer.Stream.Write( cells[i].Height );
				writer.Stream.Write( cells[i].Packed );
			}
		}
	}

	public override void Deserialize( ref Reader reader )
	{
		_chunks.Clear();

		var chunkCount = reader.Stream.Read<int>();

		for ( var c = 0; c < chunkCount; c++ )
		{
			var coord = new ChunkCoord( reader.Stream.Read<int>(), reader.Stream.Read<int>() );
			var cells = new Cell[CellsPerChunk];

			for ( var i = 0; i < CellsPerChunk; i++ )
			{
				cells[i].Height = reader.Stream.Read<float>();
				cells[i].Packed = reader.Stream.Read<uint>();
			}

			_chunks[coord] = cells;
		}

		Revision++;
	}
}
