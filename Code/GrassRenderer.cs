using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sandbox;
using Sandbox.Rendering;
using RenderStage = Sandbox.Rendering.Stage;

namespace RedSnail.GrassTool;

/// <summary>
/// One kind of grass on a renderer: what it looks like, and where it has been painted. Layers are
/// painted independently and overlap freely, so ground cover can sit under tall grass rather than
/// competing with it for the same cells.
/// </summary>
public sealed class GrassLayer
{
	/// <summary>What the brush calls this layer. Only ever shown in the editor.</summary>
	[Property]
	public string Name { get; set; } = "Grass";

	[Property]
	public GrassDefinition Definition { get; set; }

	/// <summary>Stops the layer drawing without throwing away what has been painted into it.</summary>
	[Property]
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Decorrelates this layer's placement from the others. Every random choice a cell makes -
	/// where its grass sits, which way it faces, how big it is, which sprite it takes - comes from
	/// hashing the cell's position, so two layers painted over the same ground with the same seed
	/// put their grass in exactly the same spots.
	///
	/// A new layer picks a random one, which is almost always what you want. Set two layers to the
	/// same seed deliberately and they will line up, which is occasionally useful - flowers that
	/// always sit in the same tufts as the grass, say.
	/// </summary>
	[Property]
	public int Seed { get; set; } = 0;

	/// <summary>Painted coverage. Serialized as a binary blob, not JSON.</summary>
	[Property, Hide]
	public GrassStorage Storage { get; set; } = new();

	[Hide] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name;
}

/// <summary>
/// Renders painted grass. Each frame a compute pass expands the density map into instances for
/// whatever is near the camera, then one indirect draw renders them. No blade ever exists on the
/// CPU, and nothing is stored per-blade in the scene.
///
/// A renderer holds any number of <see cref="GrassLayer"/>s - tall grass, ground cover, whatever -
/// each with its own definition, material and painted coverage. They share one instance buffer and
/// run one after another, so adding a layer costs a dispatch and a draw rather than a second
/// allocation of the whole budget.
/// </summary>
[Icon("grass"), Group("Grass"), Title("Grass Renderer")]
public sealed class GrassRenderer : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	[StructLayout(LayoutKind.Sequential)]
	private struct GpuChunk
	{
		public Vector2 Origin;
		public int CellOffset;
		public int Pad;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GpuBlade
	{
		public Vector3 Position;
		public float Yaw;
		public Vector3 Normal;
		public float Height;
		public float Width;
		public float Tint;
		public float Phase;

		// Which sprite of the sheet a card draws. Unused by blades, and it occupies what was
		// padding, so the struct stays at 48 bytes either way.
		public float Variant;
	}

	/// <summary>
	/// The GPU side of one layer. Chunks and cells are its own painted data; the args buffer is its
	/// own because the vertex count per instance follows the layer's style. Everything else - the
	/// instance buffer, the frustum, the camera - is shared across layers.
	/// </summary>
	private sealed class LayerState
	{
		public GrassLayer Source;

		public GpuBuffer<GpuChunk> ChunkBuffer;
		public GpuBuffer<GrassStorage.Cell> CellBuffer;
		public GpuBuffer<int> VisibleChunkBuffer;
		public GpuBuffer<GpuBuffer.IndirectDrawArguments> ArgsBuffer;

		public readonly List<Vector2> ChunkOrigins = [];
		public int[] VisibleScratch = [];

		public int ChunkCount;
		public int UploadedVersion = -1;
		public int UploadedVertexCount = -1;
		public bool WarnedAboutMaterial;

		public void Release()
		{
			ChunkBuffer?.Dispose();
			ChunkBuffer = null;

			CellBuffer?.Dispose();
			CellBuffer = null;

			VisibleChunkBuffer?.Dispose();
			VisibleChunkBuffer = null;

			ArgsBuffer?.Dispose();
			ArgsBuffer = null;

			ChunkOrigins.Clear();
			ChunkCount = 0;
			UploadedVersion = -1;
			UploadedVertexCount = -1;
		}
	}

	// Must match GRASS_BLADE_* and GRASS_CARD_* in grass_shared.fxc.
	private const int BladeStripVerts = 7;
	private const int BladeVertexCount = (BladeStripVerts - 2) * 3;
	private const int CardPlaneVertexCount = 6;

	// Byte offset of InstanceCount within IndirectDrawArguments. The struct is sequential
	// { uint VertexCount; uint InstanceCount; uint FirstVertex; uint FirstInstance; }, so this is
	// fixed at 4. Marshal.OffsetOf would express it directly but is outside the sandbox whitelist.
	private const int ArgsInstanceCountOffset = 4;

	/// <summary>
	/// The kinds of grass this renderer draws. The brush paints whichever one is picked in the Grass
	/// tool's sidebar, so each layer keeps its own coverage and they can overlap.
	/// </summary>
	[Property, Group("Layers")]
	public List<GrassLayer> Layers { get; set; } = [new GrassLayer()];

	/// <summary>
	/// How many instances the buffer has room for, at 48 bytes each - so a million is ~48 MB. Every
	/// layer draws out of this one buffer, so it has to fit the largest single layer, not their sum.
	///
	/// This is capacity, not a budget: how much grass there is, is decided by the render distance
	/// and the density, and this only needs to be big enough to hold the result. Set it too low and
	/// the generation overruns it, at which point which grass gets a slot is settled by whichever
	/// GPU thread reaches the counter first - an order that changes every frame, so patches flicker
	/// in and out - which is the symptom to recognise if grass ever starts churning at distance.
	/// </summary>
	[Property, Group("General"), Range(50000, 8000000), Alias("MaxBlades")]
	public int MaxGrassObjects { get; set; } = 1000000;

	/// <summary>
	/// How far outside the camera frustum grass is still generated, in world units. Culling is
	/// done against the frustum as it was when the frame started, so turning the camera brings in
	/// grass that was never generated. Raise this if it visibly streams in at the screen edges,
	/// which is most obvious at low or capped framerates.
	/// </summary>
	[Property, Group("General"), Range(0, 4000)]
	public float CullPadding { get; set; } = 1000.0f;

	/// <summary>
	/// Where a renderer kept its definition before it had layers. Still deserialized so that scenes
	/// built before the change migrate into the first layer instead of coming back empty.
	/// </summary>
	[Property, Hide]
	public GrassDefinition Definition { get; set; }

	/// <summary>Painted coverage from before layers. Migrated into the first layer on load.</summary>
	[Property, Hide]
	public GrassStorage Storage { get; set; }

	private ComputeShader _generateShader;
	private Material _bladeMaterial;
	private CommandList _commandList;

	private GpuBuffer<Vector4> _frustumPlaneBuffer;
	private GpuBuffer<GpuBlade> _bladeBuffer;

	private readonly List<LayerState> _layerStates = [];
	private readonly Vector4[] _planeScratch = new Vector4[6];

	private CameraComponent _lastCamera;
	private int _uploadedCapacity = -1;

	protected override void OnAwake()
	{
		var existing = Scene.Get<GrassRenderer>();

		if (!existing.IsValid() || existing == this)
			return;

		Log.Warning($"Only one {nameof(GrassRenderer)} can exist in a scene: the one added to \"{GameObject.Name}\" has been removed. Use the one on \"{existing.GameObject.Name}\". Add a layer to it instead of a second renderer.");

		Destroy();
	}

	protected override void OnEnabled()
	{
		Layers ??= [];
		MigrateSingleLayer();

		_generateShader = new ComputeShader("grass_generate_cs");
		_commandList = new CommandList("Grass Rendering");

		// Force a full re-upload: the buffers were released on disable, so a matching version
		// number here would otherwise leave the compute pass reading freed resources.
		_uploadedCapacity = -1;

		foreach (var state in _layerStates)
			state.UploadedVersion = -1;
	}

	protected override void OnDisabled()
	{
		_lastCamera?.RemoveCommandList(_commandList);
		_lastCamera = null;

		_commandList?.Reset();
		_commandList = null;

		ReleaseBuffers();

		_generateShader = null;
		_bladeMaterial = null;

		_uploadedCapacity = -1;
	}

	/// <summary>
	/// Moves a pre-layers renderer's definition and coverage into the first layer. Runs once: the
	/// old storage is dropped afterwards, so a second pass has nothing left to find.
	/// </summary>
	private void MigrateSingleLayer()
	{
		if (Storage is null || Storage.ChunkCount == 0)
		{
			Definition = null;
			Storage = null;
			return;
		}

		if (Layers.Count == 0)
			Layers.Add(new GrassLayer());

		var first = Layers[0];

		// Only claim a layer that has nothing of its own to lose.
		if (first.Storage is null || first.Storage.ChunkCount == 0)
		{
			first.Storage = Storage;
			first.Definition ??= Definition;
		}

		Definition = null;
		Storage = null;
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
	/// The camera the grass is generated for. While editing this is the viewport camera, or
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
	/// Brings the per-layer GPU state in line with the layer list, then re-packs any layer whose
	/// painted data changed. Layers that did not change cost nothing here.
	/// </summary>
	private void RefreshBuffers()
	{
		Layers ??= [];

		// Layers can be added, removed or reordered in the inspector at any time.
		while (_layerStates.Count > Layers.Count)
		{
			_layerStates[^1].Release();
			_layerStates.RemoveAt(_layerStates.Count - 1);
		}

		while (_layerStates.Count < Layers.Count)
			_layerStates.Add(new LayerState());

		var capacityChanged = _uploadedCapacity != MaxGrassObjects;

		if (capacityChanged || _bladeBuffer is null)
		{
			_bladeBuffer?.Dispose();
			_bladeBuffer = new GpuBuffer<GpuBlade>(MaxGrassObjects, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Append);
			_uploadedCapacity = MaxGrassObjects;
		}

		_frustumPlaneBuffer ??= new GpuBuffer<Vector4>(6, GpuBuffer.UsageFlags.Structured);

		for (var i = 0; i < Layers.Count; i++)
			RefreshLayer(Layers[i], _layerStates[i]);
	}

	private void RefreshLayer(GrassLayer layer, LayerState state)
	{
		var storage = layer?.Storage;

		// A layer the inspector swapped out from under us keeps none of its old upload.
		if (!ReferenceEquals(state.Source, layer))
		{
			state.Release();
			state.Source = layer;
		}

		if (storage is null || storage.ChunkCount == 0)
		{
			if (state.ChunkCount != 0)
				state.Release();

			state.UploadedVersion = storage?.Revision ?? -1;
			return;
		}

		if (state.UploadedVersion == storage.Revision && state.ChunkBuffer is not null)
			return;

		state.Release();
		state.Source = layer;

		state.ChunkCount = storage.ChunkCount;
		state.UploadedVersion = storage.Revision;

		var chunks = new GpuChunk[state.ChunkCount];
		var cells = new GrassStorage.Cell[state.ChunkCount * GrassStorage.CellsPerChunk];

		var index = 0;
		foreach (var (coord, cellData) in storage.Chunks)
		{
			var origin = GrassStorage.ChunkOrigin(coord);
			var offset = index * GrassStorage.CellsPerChunk;

			chunks[index] = new GpuChunk { Origin = origin, CellOffset = offset };
			Array.Copy(cellData, 0, cells, offset, GrassStorage.CellsPerChunk);
			state.ChunkOrigins.Add(origin);

			index++;
		}

		state.ChunkBuffer = new GpuBuffer<GpuChunk>(state.ChunkCount, GpuBuffer.UsageFlags.Structured);
		state.ChunkBuffer.SetData(chunks);

		state.CellBuffer = new GpuBuffer<GrassStorage.Cell>(cells.Length, GpuBuffer.UsageFlags.Structured);
		state.CellBuffer.SetData(cells);

		state.VisibleChunkBuffer = new GpuBuffer<int>(state.ChunkCount, GpuBuffer.UsageFlags.Structured);
		state.VisibleScratch = new int[state.ChunkCount];

		state.ArgsBuffer = new GpuBuffer<GpuBuffer.IndirectDrawArguments>(1, GpuBuffer.UsageFlags.IndirectDrawArguments);

		// The vertex count is written when the draw is recorded, which happens later this same
		// frame - it depends on the style, and that can change without the painted data changing.
		state.UploadedVertexCount = -1;
	}

	private void RecordCommandList(CameraComponent camera)
	{
		_commandList.Reset();

		if (_bladeBuffer is null || Layers.Count == 0)
			return;

		var cameraPosition = camera.WorldPosition;

		UploadFrustumPlanes(camera);

		// Shared by every layer: one camera, one frustum, one instance buffer.
		_commandList.Attributes.Set("GrassFrustumPlanes", _frustumPlaneBuffer);
		_commandList.Attributes.Set("GrassBlades", _bladeBuffer);
		_commandList.Attributes.Set("GrassCameraPos", cameraPosition);
		_commandList.Attributes.Set("GrassTime", RealTime.Now);

		for (var i = 0; i < Layers.Count; i++)
			RecordLayer(Layers[i], _layerStates[i], cameraPosition);
	}

	/// <summary>
	/// Generates and draws one layer. Layers share the instance buffer, so the barriers around each
	/// one also order it against the layer before: the transition back to UnorderedAccess cannot
	/// start until the previous layer's draw has finished reading. That serializes the layers on the
	/// GPU, which is the price of not allocating the whole budget once per layer.
	/// </summary>
	private void RecordLayer(GrassLayer layer, LayerState state, Vector3 cameraPosition)
	{
		if (layer is null || !layer.Enabled || state.ChunkCount == 0)
			return;

		var definition = layer.Definition;

		var visibleCount = CollectVisibleChunks(state, cameraPosition, definition?.MaxDistance ?? 12000.0f);
		if (visibleCount == 0)
			return;

		var (material, vertexCount) = GetDrawSetup(definition);

		if (material is null)
		{
			if (!state.WarnedAboutMaterial)
			{
				state.WarnedAboutMaterial = true;
				Log.Warning($"{nameof(GrassRenderer)}: layer '{layer.DisplayName}' is set to Sprites but names " +
					"no Sprite Material, so there is nothing to draw it with. Assign a material using the " +
					"grass_sprite shader, with the sprite sheet in its Color slot and a greyscale image of " +
					"the cutout in its Alpha slot.");
			}

			return;
		}

		state.WarnedAboutMaterial = false;

		// The vertex count only changes when the style or the plane count does, so it is written
		// then rather than every frame: an unconditional SetData here would be a CPU write racing
		// the CopyStructureCount below, which fills in the instance count of the same struct.
		if (vertexCount != state.UploadedVertexCount)
		{
			state.ArgsBuffer.SetData(new[] { new GpuBuffer.IndirectDrawArguments { VertexCount = (uint)vertexCount } });
			state.UploadedVertexCount = vertexCount;
		}

		_commandList.Attributes.Set("GrassChunks", state.ChunkBuffer);
		_commandList.Attributes.Set("GrassCells", state.CellBuffer);
		_commandList.Attributes.Set("GrassVisibleChunks", state.VisibleChunkBuffer);
		_commandList.Attributes.Set("GrassVisibleChunkCount", visibleCount);
		_commandList.Attributes.Set("GrassSeed", layer.Seed);

		definition?.ApplyTo(_commandList.Attributes);

		_commandList.ResourceBarrierTransition(_bladeBuffer, ResourceState.UnorderedAccess);
		_commandList.SetCounterValue(_bladeBuffer, 0);

		_commandList.DispatchCompute(_generateShader, visibleCount * GrassStorage.CellsPerChunk, 1, 1);

		// The appends must all land before the counter is read into the draw arguments.
		_commandList.UavBarrier(_bladeBuffer);

		_commandList.ResourceBarrierTransition(state.ArgsBuffer, ResourceState.CopyDestination);
		_commandList.CopyStructureCount(_bladeBuffer, state.ArgsBuffer, ArgsInstanceCountOffset);

		_commandList.ResourceBarrierTransition(_bladeBuffer, ResourceState.GenericRead);
		_commandList.ResourceBarrierTransition(state.ArgsBuffer, ResourceState.IndirectArgument);

		_commandList.DrawInstancedIndirect(material, state.ArgsBuffer);
	}

	/// <summary>
	/// The material and per-instance vertex count a definition draws with. Both shaders read the
	/// same instance buffer filled by the same generation pass; they differ only in what the vertex
	/// stage builds out of each instance.
	/// </summary>
	private (Material Material, int VertexCount) GetDrawSetup(GrassDefinition definition)
	{
		// Sprite cards draw with the material the definition names, because that is what carries
		// the sheet. Null until one is assigned, and the caller skips the draw.
		if (definition is not null && definition.Style == GrassStyle.Sprites)
			return (definition.SpriteMaterial, definition.PlaneCount * CardPlaneVertexCount);

		_bladeMaterial ??= Material.FromShader("grass");
		return (_bladeMaterial, BladeVertexCount);
	}

	/// <summary>
	/// Narrows a layer's dispatch to chunks near the camera. Per-instance frustum culling happens
	/// on the GPU; this only has to be conservative.
	/// </summary>
	private static int CollectVisibleChunks(LayerState state, Vector3 cameraPosition, float maxDistance)
	{
		// A chunk's near corner can be in range while its centre isn't, hence the circumradius.
		var cullRadius = maxDistance + GrassStorage.ChunkSize * 0.7072f;
		var cullRadiusSq = cullRadius * cullRadius;

		var halfChunk = GrassStorage.ChunkSize * 0.5f;
		var count = 0;

		for (var i = 0; i < state.ChunkOrigins.Count; i++)
		{
			var origin = state.ChunkOrigins[i];
			var dx = origin.x + halfChunk - cameraPosition.x;
			var dy = origin.y + halfChunk - cameraPosition.y;

			if (dx * dx + dy * dy > cullRadiusSq)
				continue;

			state.VisibleScratch[count++] = i;
		}

		if (count > 0)
			state.VisibleChunkBuffer.SetData(state.VisibleScratch.AsSpan(0, count));

		return count;
	}

	private void UploadFrustumPlanes(CameraComponent camera)
	{
		var frustum = camera.GetFrustum();

		// These planes are sampled once on the CPU but the grass they cull is not drawn until the
		// frame presents, by which point the camera has kept turning. Pushing every plane outward
		// gives the generation something to work with at the screen edges - without it, grass
		// rotating into view was culled before it was ever needed, which reads as it streaming in
		// from the sides. The slack that buys scales with the frame time, so a capped or struggling
		// framerate is exactly when it matters most.
		var padding = MathF.Max(CullPadding, 0.0f);

		// Plane.GetDistance is dot( point, Normal ) - Distance, so w is negated to let the shader
		// use a plain dot( xyz, p ) + w. Adding the padding there slides the plane outward.
		_planeScratch[0] = ToVector4(frustum.LeftPlane, padding);
		_planeScratch[1] = ToVector4(frustum.RightPlane, padding);
		_planeScratch[2] = ToVector4(frustum.TopPlane, padding);
		_planeScratch[3] = ToVector4(frustum.BottomPlane, padding);
		_planeScratch[4] = ToVector4(frustum.NearPlane, padding);

		// The far plane is left tight - MaxDistance already governs the far edge, and padding it
		// would only generate grass that fades out before it is ever visible.
		_planeScratch[5] = ToVector4(frustum.FarPlane, 0.0f);

		_frustumPlaneBuffer.SetData(_planeScratch);

		static Vector4 ToVector4(Plane plane, float padding) =>
			new(plane.Normal.x, plane.Normal.y, plane.Normal.z, -plane.Distance + padding);
	}

	private void ReleaseBuffers()
	{
		foreach (var state in _layerStates)
			state.Release();

		_layerStates.Clear();

		_frustumPlaneBuffer?.Dispose();
		_frustumPlaneBuffer = null;

		_bladeBuffer?.Dispose();
		_bladeBuffer = null;
	}

	/// <summary>
	/// Called by the editor tool after painting, so the next frame re-uploads the density map.
	/// </summary>
	public void MarkDirty()
	{
		foreach (var state in _layerStates)
			state.UploadedVersion = -1;
	}

	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected || Layers is null)
			return;

		Gizmo.Draw.Color = Color.Green.WithAlpha(0.35f);

		foreach (var layer in Layers)
		{
			var storage = layer?.Storage;
			if (storage is null || storage.ChunkCount == 0)
				continue;

			foreach (var (coord, _) in storage.Chunks)
			{
				var origin = GrassStorage.ChunkOrigin(coord);
				var mins = new Vector3(origin.x, origin.y, 0);
				var maxs = new Vector3(origin.x + GrassStorage.ChunkSize, origin.y + GrassStorage.ChunkSize, 0);

				Gizmo.Draw.LineBBox(new BBox(WorldTransform.PointToLocal(mins), WorldTransform.PointToLocal(maxs)));
			}
		}
	}
}
