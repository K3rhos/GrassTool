using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sandbox;
using Sandbox.Rendering;
using RenderStage = Sandbox.Rendering.Stage;

namespace RedSnail.GrassTool;

/// <summary>
/// Renders a painted grass field. Each frame a compute pass expands the density map into blade
/// instances for whatever is near the camera, then a single indirect draw renders them all.
/// No blade ever exists on the CPU, and nothing is stored per-blade in the scene.
/// </summary>
[Icon( "grass" ), Group( "Grass" ), Title( "Grass Renderer" )]
public sealed class GrassRenderer : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	[StructLayout( LayoutKind.Sequential )]
	private struct GpuChunk
	{
		public Vector2 Origin;
		public int CellOffset;
		public int Pad;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct GpuBlade
	{
		public Vector3 Position;
		public float Yaw;
		public Vector3 Normal;
		public float Height;
		public float Width;
		public float Tint;
		public float Phase;
		public float Pad; // keeps the struct at 48 bytes
	}

	// Must match GRASS_BLADE_* in grass_shared.fxc.
	private const int BladeStripVerts = 7;
	private const int BladeVertexCount = (BladeStripVerts - 2) * 3;

	// Byte offset of InstanceCount within IndirectDrawArguments. The struct is sequential
	// { uint VertexCount; uint InstanceCount; uint FirstVertex; uint FirstInstance; }, so this is
	// fixed at 4. Marshal.OffsetOf would express it directly but is outside the sandbox whitelist.
	private const int ArgsInstanceCountOffset = 4;

	[Property, Group( "General" )]
	public GrassDefinition Definition { get; set; }

	/// <summary>
	/// Ceiling on blades alive at once. Each costs 48 bytes of GPU memory, so 500k is ~24 MB.
	/// Raise it if dense fields visibly clip out at the far edge of the render distance.
	/// </summary>
	[Property, Group( "General" ), Range( 50000, 4000000 )]
	public int MaxBlades { get; set; } = 500000;

	/// <summary>
	/// How far outside the camera frustum blades are still generated, in world units. Culling is
	/// done against the frustum as it was when the frame started, so turning the camera brings in
	/// blades that were never generated. Raise this if they visibly stream in at the screen edges,
	/// which is most obvious at low or capped framerates.
	/// </summary>
	[Property, Group( "General" ), Range( 0, 4000 )]
	public float CullPadding { get; set; } = 1000.0f;

	/// <summary>Painted coverage. Serialized as a binary blob, not JSON.</summary>
	[Property, Hide]
	public GrassStorage Storage { get; set; } = new();

	private ComputeShader _generateShader;
	private Material _material;
	private CommandList _commandList;

	private GpuBuffer<GpuChunk> _chunkBuffer;
	private GpuBuffer<GrassStorage.Cell> _cellBuffer;
	private GpuBuffer<int> _visibleChunkBuffer;
	private GpuBuffer<Vector4> _frustumPlaneBuffer;
	private GpuBuffer<GpuBlade> _bladeBuffer;
	private GpuBuffer<GpuBuffer.IndirectDrawArguments> _argsBuffer;

	private readonly List<Vector2> _chunkOrigins = [];
	private int[] _visibleScratch = [];
	private readonly Vector4[] _planeScratch = new Vector4[6];

	private CameraComponent _lastCamera;
	private int _chunkCount;
	private int _uploadedVersion = -1;
	private int _uploadedMaxBlades = -1;

	protected override void OnEnabled()
	{
		Storage ??= new GrassStorage();

		_generateShader = new ComputeShader("grass_generate_cs");
		_material = Material.FromShader("grass");
		_commandList = new CommandList("Grass Rendering");

		// Force a full re-upload: the buffers were released on disable, so a matching version
		// number here would otherwise leave the compute pass reading freed resources.
		_uploadedVersion = -1;
		_uploadedMaxBlades = -1;

		RefreshBuffers();
	}

	protected override void OnDisabled()
	{
		_lastCamera?.RemoveCommandList(_commandList);
		_lastCamera = null;

		_commandList?.Reset();
		_commandList = null;

		ReleaseBuffers();

		_generateShader = null;
		_material = null;

		_uploadedVersion = -1;
		_uploadedMaxBlades = -1;
	}

	protected override void OnUpdate()
	{
		var renderCamera = GetRenderCamera();

		// Re-attach when the camera changes, and also when the one we attached to stopped being
		// valid. Leaving play mode destroys the play camera without the reference here changing,
		// so comparing references alone would leave us bound to a dead camera forever.
		if (renderCamera != _lastCamera || !_lastCamera.IsValid())
		{
			if (_lastCamera.IsValid())
				_lastCamera.RemoveCommandList(_commandList);

			_lastCamera = null;

			if (renderCamera.IsValid())
			{
				renderCamera.AddCommandList(_commandList, RenderStage.AfterOpaque);
				_lastCamera = renderCamera;
			}
		}

		// Nothing to draw through until a camera exists. State is cleared above, so we pick one
		// up as soon as one appears.
		if (!_lastCamera.IsValid())
			return;

		// Culling follows whichever camera the viewport actually looks through, which is not
		// necessarily the one replaying the list.
		var cullCamera = GetCullCamera();
		
		if (!cullCamera.IsValid())
			return;

		RefreshBuffers();
		RecordCommandList(cullCamera);
	}

	/// <summary>
	/// The camera whose command list actually replays. A scene camera does so in the editor
	/// viewport as well as in game, so it wins when one exists; with an empty scene the editor
	/// camera is the only thing left that will replay ours.
	/// </summary>
	private CameraComponent GetRenderCamera()
	{
		if (Scene.Camera.IsValid())
			return Scene.Camera;

		if (Scene.IsEditor)
			return Application.Editor?.Camera;

		return null;
	}

	/// <summary>
	/// The camera the blades are generated for. While editing this is the viewport camera, or
	/// nothing outside the game camera's frustum would ever be generated.
	/// </summary>
	private CameraComponent GetCullCamera()
	{
		if (Scene.IsEditor)
		{
			var editorCamera = Application.Editor?.Camera;
			
			if (editorCamera.IsValid())
				return editorCamera;
		}

		return Scene.Camera;
	}

	/// <summary>
	/// Re-packs painted chunks into GPU buffers. Skipped entirely unless the painted data or the
	/// blade budget actually changed.
	/// </summary>
	private void RefreshBuffers()
	{
		if ( Storage is null || Storage.ChunkCount == 0 )
		{
			if ( _chunkCount != 0 )
			{
				ReleaseBuffers();
				_chunkCount = 0;
			}

			_uploadedVersion = Storage?.Revision ?? -1;
			return;
		}

		if ( _uploadedVersion == Storage.Revision && _uploadedMaxBlades == MaxBlades && _chunkBuffer is not null )
			return;

		ReleaseBuffers();

		_chunkCount = Storage.ChunkCount;
		_uploadedVersion = Storage.Revision;
		_uploadedMaxBlades = MaxBlades;

		var chunks = new GpuChunk[_chunkCount];
		var cells = new GrassStorage.Cell[_chunkCount * GrassStorage.CellsPerChunk];

		_chunkOrigins.Clear();

		var index = 0;
		foreach ( var (coord, cellData) in Storage.Chunks )
		{
			var origin = GrassStorage.ChunkOrigin( coord );
			var offset = index * GrassStorage.CellsPerChunk;

			chunks[index] = new GpuChunk { Origin = origin, CellOffset = offset };
			Array.Copy( cellData, 0, cells, offset, GrassStorage.CellsPerChunk );
			_chunkOrigins.Add( origin );

			index++;
		}

		_chunkBuffer = new GpuBuffer<GpuChunk>( _chunkCount, GpuBuffer.UsageFlags.Structured );
		_chunkBuffer.SetData( chunks );

		_cellBuffer = new GpuBuffer<GrassStorage.Cell>( cells.Length, GpuBuffer.UsageFlags.Structured );
		_cellBuffer.SetData( cells );

		_visibleChunkBuffer = new GpuBuffer<int>( _chunkCount, GpuBuffer.UsageFlags.Structured );
		_visibleScratch = new int[_chunkCount];

		_frustumPlaneBuffer = new GpuBuffer<Vector4>( 6, GpuBuffer.UsageFlags.Structured );

		_bladeBuffer = new GpuBuffer<GpuBlade>( MaxBlades, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Append );

		_argsBuffer = new GpuBuffer<GpuBuffer.IndirectDrawArguments>( 1, GpuBuffer.UsageFlags.IndirectDrawArguments );
		_argsBuffer.SetData( new[]
		{
			new GpuBuffer.IndirectDrawArguments { VertexCount = BladeVertexCount }
		} );
	}

	private void RecordCommandList( CameraComponent camera )
	{
		_commandList.Reset();

		if ( _chunkCount == 0 || _bladeBuffer is null )
			return;

		var definition = Definition;
		var cameraPosition = camera.WorldPosition;

		var visibleCount = CollectVisibleChunks( cameraPosition, definition?.MaxDistance ?? 12000.0f );
		if ( visibleCount == 0 )
			return;

		UploadFrustumPlanes( camera );

		_commandList.Attributes.Set( "GrassChunks", _chunkBuffer );
		_commandList.Attributes.Set( "GrassCells", _cellBuffer );
		_commandList.Attributes.Set( "GrassVisibleChunks", _visibleChunkBuffer );
		_commandList.Attributes.Set( "GrassFrustumPlanes", _frustumPlaneBuffer );
		_commandList.Attributes.Set( "GrassBlades", _bladeBuffer );
		_commandList.Attributes.Set( "GrassVisibleChunkCount", visibleCount );
		_commandList.Attributes.Set( "GrassCameraPos", cameraPosition );
		_commandList.Attributes.Set( "GrassTime", RealTime.Now );

		definition?.ApplyTo( _commandList.Attributes );

		_commandList.ResourceBarrierTransition( _bladeBuffer, ResourceState.UnorderedAccess );
		_commandList.SetCounterValue( _bladeBuffer, 0 );

		_commandList.DispatchCompute( _generateShader, visibleCount * GrassStorage.CellsPerChunk, 1, 1 );

		// The appends must all land before the counter is read into the draw arguments.
		_commandList.UavBarrier( _bladeBuffer );

		_commandList.ResourceBarrierTransition( _argsBuffer, ResourceState.CopyDestination );
		_commandList.CopyStructureCount( _bladeBuffer, _argsBuffer, ArgsInstanceCountOffset );

		_commandList.ResourceBarrierTransition( _bladeBuffer, ResourceState.GenericRead );
		_commandList.ResourceBarrierTransition( _argsBuffer, ResourceState.IndirectArgument );

		_commandList.DrawInstancedIndirect( _material, _argsBuffer );
	}

	/// <summary>
	/// Narrows the dispatch to chunks near the camera. Per-blade frustum culling happens on the
	/// GPU; this only has to be conservative.
	/// </summary>
	private int CollectVisibleChunks( Vector3 cameraPosition, float maxDistance )
	{
		// A chunk's near corner can be in range while its centre isn't, hence the circumradius.
		var cullRadius = maxDistance + GrassStorage.ChunkSize * 0.7072f;
		var cullRadiusSq = cullRadius * cullRadius;

		var halfChunk = GrassStorage.ChunkSize * 0.5f;
		var count = 0;

		for ( var i = 0; i < _chunkOrigins.Count; i++ )
		{
			var origin = _chunkOrigins[i];
			var dx = origin.x + halfChunk - cameraPosition.x;
			var dy = origin.y + halfChunk - cameraPosition.y;

			if ( dx * dx + dy * dy > cullRadiusSq )
				continue;

			_visibleScratch[count++] = i;
		}

		if ( count > 0 )
			_visibleChunkBuffer.SetData( _visibleScratch.AsSpan( 0, count ) );

		return count;
	}

	private void UploadFrustumPlanes( CameraComponent camera )
	{
		var frustum = camera.GetFrustum();

		// These planes are sampled once on the CPU but the blades they cull are not drawn until the
		// frame presents, by which point the camera has kept turning. Pushing every plane outward
		// gives the generation something to work with at the screen edges - without it, blades
		// rotating into view were culled before they were ever needed, which reads as them
		// streaming in from the sides. The slack that buys scales with the frame time, so a capped
		// or struggling framerate is exactly when it matters most.
		var padding = MathF.Max( CullPadding, 0.0f );

		// Plane.GetDistance is dot( point, Normal ) - Distance, so w is negated to let the shader
		// use a plain dot( xyz, p ) + w. Adding the padding there slides the plane outward.
		_planeScratch[0] = ToVector4( frustum.LeftPlane, padding );
		_planeScratch[1] = ToVector4( frustum.RightPlane, padding );
		_planeScratch[2] = ToVector4( frustum.TopPlane, padding );
		_planeScratch[3] = ToVector4( frustum.BottomPlane, padding );
		_planeScratch[4] = ToVector4( frustum.NearPlane, padding );

		// The far plane is left tight - MaxDistance already governs the far edge, and padding it
		// would only generate blades that fade out before they are ever visible.
		_planeScratch[5] = ToVector4( frustum.FarPlane, 0.0f );

		_frustumPlaneBuffer.SetData( _planeScratch );

		static Vector4 ToVector4( Plane plane, float padding ) =>
			new( plane.Normal.x, plane.Normal.y, plane.Normal.z, -plane.Distance + padding );
	}

	private void ReleaseBuffers()
	{
		_chunkBuffer?.Dispose();
		_chunkBuffer = null;

		_cellBuffer?.Dispose();
		_cellBuffer = null;

		_visibleChunkBuffer?.Dispose();
		_visibleChunkBuffer = null;

		_frustumPlaneBuffer?.Dispose();
		_frustumPlaneBuffer = null;

		_bladeBuffer?.Dispose();
		_bladeBuffer = null;

		_argsBuffer?.Dispose();
		_argsBuffer = null;

		_chunkOrigins.Clear();
		_chunkCount = 0;
	}

	/// <summary>
	/// Called by the editor tool after painting, so the next frame re-uploads the density map.
	/// </summary>
	public void MarkDirty() => _uploadedVersion = -1;

	protected override void DrawGizmos()
	{
		if ( !Gizmo.IsSelected || Storage is null || Storage.ChunkCount == 0 )
			return;

		Gizmo.Draw.Color = Color.Green.WithAlpha( 0.35f );

		foreach ( var (coord, _) in Storage.Chunks )
		{
			var origin = GrassStorage.ChunkOrigin( coord );
			var mins = new Vector3( origin.x, origin.y, 0 );
			var maxs = new Vector3( origin.x + GrassStorage.ChunkSize, origin.y + GrassStorage.ChunkSize, 0 );

			Gizmo.Draw.LineBBox( new BBox( WorldTransform.PointToLocal( mins ), WorldTransform.PointToLocal( maxs ) ) );
		}
	}
}
