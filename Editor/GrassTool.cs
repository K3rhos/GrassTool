using System;
using System.Linq;
using Editor;
using Editor.TerrainEditor;
using Sandbox;

namespace RedSnail.GrassTool.Editor;

/// <summary>
/// Paints grass coverage onto any surface. The brush writes density into the target
/// <see cref="GrassRenderer"/>'s density map, baking the surface height and normal under each
/// cell so blades sit on whatever geometry is there. Hold Ctrl to erase.
/// </summary>
[EditorTool("grass")]
[Title("Grass")]
[Icon("grass")]
public sealed class GrassPaintTool : EditorTool
{
	public BrushSettings BrushSettings { get; private set; } = new();

	private GrassRenderer _target;
	private int _layerIndex;
	private ComboBox _layerCombo;
	private int _layerComboHash;
	private bool _erasing;
	private bool _dragging;
	private bool _painted;
	private Vector3 _lastPaintPosition;

	// Repainting the same spot every frame just burns traces, so the brush has to travel a
	// fraction of its own radius before it deposits again.
	private float PaintStepDistance => BrushSettings.Size * 0.25f;

	public GrassPaintTool()
	{
		RebuildSidebarOnSelectionChange = false;
	}

	public override Widget CreateToolSidebar()
	{
		var sidebar = new ToolSidebarWidget();
		sidebar.AddTitle("Grass Brush", "brush");
		sidebar.MinimumWidth = 300;

		{
			var group = sidebar.AddGroup("Layer");
			_layerCombo = new ComboBox();
			_layerComboHash = 0;
			group.Add(_layerCombo);
			RebuildLayerCombo();
		}

		{
			var group = sidebar.AddGroup("Brush");
			var so = BrushSettings.GetSerialized();
			group.Add(ControlSheet.CreateRow(so.GetProperty(nameof(BrushSettings.Size))));
			group.Add(ControlSheet.CreateRow(so.GetProperty(nameof(BrushSettings.Opacity))));
		}

		{
			var group = sidebar.AddGroup("Actions");

			var clear = new Button("Clear Layer", "delete_sweep");
			clear.ToolTip = "Remove every painted cell from the selected layer";
			clear.Clicked += () =>
			{
				var layer = ResolveLayer();
				if (layer?.Storage is null)
					return;

				// Wiping the density map throws away every stroke and there is no undo for it,
				// so this one gets a confirmation.
				Dialog.AskConfirm(
					() =>
					{
						layer.Storage.ClearAll();
						_target?.MarkDirty();
					},
					$"Are you sure you want to delete all grass painted into '{layer.DisplayName}'? " +
						"This action cannot be undone.",
					"Delete Layer's Grass",
					"Delete",
					"Cancel");
			};
			group.Add(clear);
		}

		// Soaks up the leftover height. Without it the column spreads the groups out to fill the
		// panel instead of stacking them at the top.
		sidebar.Layout.AddStretchCell();

		return sidebar;
	}

	public override void OnUpdate()
	{
		RebuildLayerCombo();

		_erasing = Gizmo.IsCtrlPressed;

		DrawBrushPreview();

		Gizmo.Hitbox.BBox(BBox.FromPositionAndSize(Vector3.Zero, 999999));

		if (Gizmo.IsLeftMouseDown)
		{
			if (!_dragging)
			{
				_dragging = true;
				_lastPaintPosition = Vector3.Zero;
			}

			OnPaintUpdate();
		}
		else if (_dragging)
		{
			_dragging = false;
			_lastPaintPosition = Vector3.Zero;

			if (_painted)
			{
				ResolveTarget()?.MarkDirty();
				_painted = false;
			}
		}
	}

	/// <summary>
	/// Uses the selected renderer if there is one, otherwise the only one in the scene. Creating
	/// it implicitly would leave stray components around every time someone opens the tool.
	/// </summary>
	private GrassRenderer ResolveTarget()
	{
		var selected = Selection
			.OfType<GameObject>()
			.Select(go => go.Components.Get<GrassRenderer>(FindMode.EnabledInSelfAndDescendants))
			.FirstOrDefault(r => r.IsValid());

		if (selected.IsValid())
		{
			_target = selected;
			return _target;
		}

		if (_target.IsValid())
			return _target;

		_target = Scene.GetAllComponents<GrassRenderer>().FirstOrDefault();
		return _target;
	}

	/// <summary>
	/// The layer the brush writes into. Layers each keep their own coverage, so this is what decides
	/// whether a stroke lands on the tall grass or the ground cover.
	/// </summary>
	private GrassLayer ResolveLayer()
	{
		var target = ResolveTarget();
		if (!target.IsValid() || target.Layers is null || target.Layers.Count == 0)
			return null;

		_layerIndex = Math.Clamp(_layerIndex, 0, target.Layers.Count - 1);
		return target.Layers[_layerIndex];
	}

	/// <summary>
	/// Refills the layer dropdown when the renderer or its layers change. Rebuilt from a hash rather
	/// than every frame, since replacing the items would fight the user opening the list.
	/// </summary>
	private void RebuildLayerCombo()
	{
		if (_layerCombo is null)
			return;

		var target = ResolveTarget();
		var layers = target.IsValid() ? target.Layers : null;

		var hash = new HashCode();
		hash.Add(target);

		if (layers is not null)
		{
			for (var i = 0; i < layers.Count; i++)
				hash.Add(layers[i]?.DisplayName);
		}

		var combined = hash.ToHashCode();
		if (combined == _layerComboHash)
			return;

		_layerComboHash = combined;
		_layerCombo.Clear();

		if (layers is null || layers.Count == 0)
		{
			_layerCombo.AddItem("No layers", onSelected: () => _layerIndex = 0, selected: true);
			_layerCombo.ToolTip = "Add a layer to the Grass Renderer to paint into";
			return;
		}

		_layerIndex = Math.Clamp(_layerIndex, 0, layers.Count - 1);
		_layerCombo.ToolTip = "Which layer the brush paints into";

		for (var i = 0; i < layers.Count; i++)
		{
			var index = i;
			_layerCombo.AddItem(layers[i]?.DisplayName ?? "Unnamed", onSelected: () => _layerIndex = index, selected: i == _layerIndex);
		}
	}

	private void OnPaintUpdate()
	{
		var layer = ResolveLayer();
		if (layer?.Storage is null)
			return;

		var cursor = TraceCursor();
		if (!cursor.Hit)
			return;

		if (_lastPaintPosition != Vector3.Zero &&
			 Vector3.DistanceBetween(cursor.HitPosition, _lastPaintPosition) < PaintStepDistance)
			return;

		_lastPaintPosition = cursor.HitPosition;

		var radius = (float)BrushSettings.Size;
		var strength = BrushSettings.Opacity;

		if (_erasing)
		{
			layer.Storage.Erase(cursor.HitPosition, radius, strength);
			_painted = true;
			return;
		}

		PaintCells(layer.Storage, cursor.HitPosition, radius, strength);
		_painted = true;
	}

	/// <summary>
	/// Walks every density cell the brush touches and traces straight down onto the world to bake
	/// the surface height and normal. Blades then follow whatever they were painted onto.
	/// </summary>
	private void PaintCells(GrassStorage storage, Vector3 center, float radius, float strength)
	{
		var radiusSq = radius * radius;

		var minX = (int)MathF.Floor((center.x - radius) / GrassStorage.CellSize);
		var maxX = (int)MathF.Floor((center.x + radius) / GrassStorage.CellSize);
		var minY = (int)MathF.Floor((center.y - radius) / GrassStorage.CellSize);
		var maxY = (int)MathF.Floor((center.y + radius) / GrassStorage.CellSize);

		// Enough headroom to find the surface from above without punching through overhangs the
		// brush was never aimed at.
		var traceHeight = radius + 512.0f;

		for (var cy = minY; cy <= maxY; cy++)
		{
			for (var cx = minX; cx <= maxX; cx++)
			{
				var wx = (cx + 0.5f) * GrassStorage.CellSize;
				var wy = (cy + 0.5f) * GrassStorage.CellSize;

				var dx = wx - center.x;
				var dy = wy - center.y;
				var distSq = dx * dx + dy * dy;
				if (distSq > radiusSq)
					continue;

				var from = new Vector3(wx, wy, center.z + traceHeight);
				var to = new Vector3(wx, wy, center.z - traceHeight);

				var tr = Scene.Trace.Ray(from, to)
					.UseRenderMeshes(true)
					.WithTag("solid")
					.Run();

				if (!tr.Hit)
					continue;

				// Soft edge, so overlapping strokes build up smoothly instead of leaving a disc.
				var falloff = 1.0f - MathF.Sqrt(distSq) / radius;
				var added = strength * MathF.Pow(falloff, 0.5f);

				var existing = storage.GetCell(wx, wy).Density;
				var density = Math.Clamp(existing + added, 0.0f, 1.0f);

				storage.SetCell(wx, wy, density, tr.HitPosition.z, tr.Normal);
			}
		}
	}

	private SceneTraceResult TraceCursor() =>
		Scene.Trace.Ray(Gizmo.CurrentRay, 100000)
			.UseRenderMeshes(true)
			.WithTag("solid")
			.Run();

	private void DrawBrushPreview()
	{
		var tr = TraceCursor();
		if (!tr.Hit)
			return;

		using (Gizmo.Scope("GrassBrush"))
		{
			Gizmo.Draw.Color = _erasing
				? Color.FromBytes(250, 150, 150)
				: Color.FromBytes(150, 250, 160);

			Gizmo.Draw.LineCircle(tr.HitPosition + tr.Normal * 1.0f, tr.Normal, BrushSettings.Size);
			Gizmo.Draw.LineCircle(tr.HitPosition + tr.Normal * 1.0f, tr.Normal, BrushSettings.Size * 0.5f);
		}
	}
}
