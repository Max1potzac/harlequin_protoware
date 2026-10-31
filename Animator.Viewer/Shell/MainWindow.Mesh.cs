using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer;

// Meshes (the part panel's Mesh box): a grid made over the selected part's picture so it can
// bend; weights giving its points to bones (worked out from where the bones are, all on one, or
// painted in the viewport); its points dragged (Build: where they sit on the picture; Animate:
// deform keys at the playhead). Editing a skin: the skin's own copy of the drawing changes.
public partial class MainWindow
{
	private enum MeshTool { None, Points, Weights }
	private MeshTool _meshTool;
	private bool _syncingMesh;

	private sealed class MeshDrag
	{
		public required Slot Slot;
		public required Attachment Drawing;
		public required int Point;               // -1: painting weights
		public StructureSnapshot? Structure;     // Build
		public AnimationSnapshot? Keys;          // Animate
		public Vector2[]? StartOffsets;
		public Vector2 Start;
		public bool Subtract;
		public bool Changed;
	}
	private MeshDrag? _meshDrag;
	private int _meshHover = -1;

	// The selected part's drawing that shows now, and its mesh (null: no part, or no mesh)
	private (Slot Slot, Attachment Drawing, Mesh? Mesh)? MeshPart() =>
		!_sceneLevel && _state.SelectedSlot is { } slot && _skeleton.ResolveAttachment(slot) is { } drawing
			? (slot, drawing, drawing.Mesh) : null;

	private Bone? WeightBone() =>
		MeshBoneBox.SelectedItem is string name ? _skeleton.FindBone(name) : _state.SelectedSlot?.Bone;

	// ======================= the panel =======================

	private void SyncMeshPanel()
	{
		var part = MeshPart();
		MeshBox.Visibility = part != null && PartProps.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
		if (part is not { } p) return;
		_syncingMesh = true;
		bool has = p.Mesh != null, building = !_state.AnimateMode;
		MeshMakeButton.IsEnabled = building;
		MeshRemoveButton.IsEnabled = has && building;
		MeshPointsToggle.IsEnabled = has;
		MeshWeightsToggle.IsEnabled = has && building && p.Mesh!.Weighted;
		MeshWeightsButton.IsEnabled = has && building;
		if (!MeshWeightsToggle.IsEnabled && _meshTool == MeshTool.Weights) _meshTool = MeshTool.None;
		if (!has) _meshTool = MeshTool.None;
		MeshPointsToggle.IsChecked = _meshTool == MeshTool.Points;
		MeshWeightsToggle.IsChecked = _meshTool == MeshTool.Weights;
		var names = _skeleton.AllBones().Select(b => b.Name).ToList();
		if (!MeshBoneBox.Items.Cast<string>().SequenceEqual(names))
		{
			var chosen = MeshBoneBox.SelectedItem as string;
			MeshBoneBox.Items.Clear();
			foreach (var name in names) MeshBoneBox.Items.Add(name);
			MeshBoneBox.SelectedItem = chosen != null && names.Contains(chosen) ? chosen : p.Slot.Bone.Name;
		}
		if (MeshBoneBox.SelectedItem == null) MeshBoneBox.SelectedItem = p.Slot.Bone.Name;
		_syncingMesh = false;
		MeshHint.Text = p.Mesh switch
		{
			null => "No mesh: the picture stays flat. Make a mesh to let it bend.",
			{ Weighted: false } m => $"{m.Points.Length} points, following {p.Slot.Bone.Name} flat. Weights ▾ lets bones bend it; in Animate, Edit points keys deforms.",
			{ } m => $"{m.Points.Length} points, {m.Weights!.SelectMany(w => w).Select(w => w.Bone).Distinct().Count()} bone(s) bending it." +
				(_meshTool == MeshTool.Weights ? " Drag over the points to give the chosen bone more (Alt: less)." : "")
		};
	}

	private void EditMesh(string name, Func<Slot, Attachment, bool> change)
	{
		if (MeshPart() is not { } p) return;
		_state.EditStructure(() =>
		{
			var drawing = _state.EditableDrawing(p.Slot) ?? p.Drawing;   // editing a skin: its own copy
			return change(p.Slot, drawing) ? name : null;
		});
		SyncMeshPanel();
	}

	private void MeshMake_Click(object sender, RoutedEventArgs e)
	{
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		foreach (var (label, across) in new[] { ("Fine", 16), ("Medium", 10), ("Coarse", 6), ("Just the corners", 1) })
		{
			var item = new MenuItem { Header = across == 1 ? label : $"{label} ({across} across)" };
			item.Click += (_, _) => MakeMesh(across);
			menu.Items.Add(item);
		}
		menu.IsOpen = true;
	}

	private void MakeMesh(int across)
	{
		if (MeshPart() is not { } part || !_skeleton.Assets.TryGetValue(part.Drawing.AssetId, out var asset)
			|| _assets.Get(asset, false) is not { } bitmap) return;
		var visible = VisibleRect(bitmap) ?? (0, 0, bitmap.Width, bitmap.Height);
		var area = ((int)visible.X, (int)visible.Y, (int)visible.W, (int)visible.H);
		int step = across <= 1 ? Math.Max(area.Item3, area.Item4) + 1 : Math.Max(2, (int)MathF.Ceiling(Math.Max(area.Item3, area.Item4) / (float)across));
		bool Shows(int x0, int y0, int x1, int y1)
		{
			for (int y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y1); y++)
				for (int x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x1); x++)
					if (bitmap.GetPixel(x, y).Alpha > 8) return true;
			return false;
		}
		EditMesh($"Make a mesh on {part.Slot.Name}", (_, drawing) =>
		{
			drawing.Mesh = MeshOps.Grid(bitmap.Width, bitmap.Height, area, step, Shows);
			return true;
		});
		_meshTool = MeshTool.Points;
		SyncMeshPanel();
	}

	private void MeshRemove_Click(object sender, RoutedEventArgs e)
	{
		EditMesh("Remove the mesh", (_, drawing) => { drawing.Mesh = null; return true; });
		_meshTool = MeshTool.None;
		SyncMeshPanel();
	}

	private void MeshWeightsMenu_Click(object sender, RoutedEventArgs e)
	{
		if (MeshPart() is not { Mesh: not null } part) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Add(string header, string tip, Func<Slot, Attachment, Mesh, Mesh> make)
		{
			var item = new MenuItem { Header = header, ToolTip = tip };
			item.Click += (_, _) =>
			{
				EditMesh(header, (slot, drawing) =>
				{
					if (drawing.Mesh is not { } mesh) return false;
					_skeleton.SetToSetupPose();   // (weights are given at rest)
					_skeleton.UpdateWorld();
					drawing.Mesh = make(slot, drawing, mesh);
					return true;
				});
			};
			menu.Items.Add(item);
		}
		Add("From the nearest bones", "Each point shared by the 3 bones nearest to it, the nearer the more (then paint to adjust)",
			(slot, drawing, mesh) => MeshOps.AutoWeights(_skeleton, slot, drawing, mesh));
		var picked = _state.PickedBones.ToList();
		if (picked.Count > 0)
			Add($"From the picked bones ({picked.Count})", "As above, among the picked bones only",
				(slot, drawing, mesh) => MeshOps.AutoWeights(_skeleton, slot, drawing, mesh, picked));
		var bone = WeightBone() ?? part.Slot.Bone;
		Add($"All on {bone.Name}", "Every point follows that bone alone", (slot, drawing, mesh) => MeshOps.AllOn(_skeleton, slot, drawing, mesh, bone));
		Add("None (follows its part, flat)", "No weights: the mesh moves with its part's bone, and deform keys push it about",
			(_, _, mesh) => mesh.Rigid());
		menu.IsOpen = true;
	}

	private void MeshPoints_Click(object sender, RoutedEventArgs e)
	{
		_meshTool = MeshPointsToggle.IsChecked == true ? MeshTool.Points : MeshTool.None;
		SyncMeshPanel();
	}

	private void MeshWeights_Click(object sender, RoutedEventArgs e)
	{
		_meshTool = MeshWeightsToggle.IsChecked == true ? MeshTool.Weights : MeshTool.None;
		SyncMeshPanel();
	}

	private void MeshBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_syncingMesh) SyncMeshPanel();
	}

	// ======================= the viewport =======================

	private void DrawMeshOverlay(SKCanvas canvas)
	{
		if (_meshTool == MeshTool.None || MeshPart() is not { Mesh: { } mesh } part) return;
		DrawInSpace(canvas, _camera.Space, () =>
		{
			float px = 1f / MathF.Max(_renderer.Zoom, 1e-4f);
			var posed = MeshOps.Posed(part.Slot, part.Drawing, mesh);
			using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * px, IsAntialias = true };
			var t = mesh.Triangles;
			for (int i = 0; i < t.Length; i += 3)
			{
				using var path = ShapePaths.Polyline(new[] { posed[t[i]], posed[t[i + 1]], posed[t[i + 2]] }, closed: true);
				canvas.DrawPath(path, edge);
			}
			var bone = _meshTool == MeshTool.Weights ? WeightBone() : null;
			using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
			using var ring = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * px, IsAntialias = true };
			for (int i = 0; i < posed.Length; i++)
			{
				dot.Color = bone != null ? Heat(MeshOps.WeightOf(mesh, i, bone)) : i == _meshHover ? SKColors.Yellow : SKColors.White;
				float r = (bone != null ? 4.5f : 3.5f) * px;
				canvas.DrawCircle(posed[i].X, posed[i].Y, r, dot);
				canvas.DrawCircle(posed[i].X, posed[i].Y, r, ring);
			}
		});
	}

	// Blue (none) → green → red (all of it)
	private static SKColor Heat(float weight)
	{
		weight = Math.Clamp(weight, 0f, 1f);
		return weight < 0.5f
			? new SKColor(0, (byte)(weight * 2 * 255), (byte)((1 - weight * 2) * 255))
			: new SKColor((byte)((weight - 0.5f) * 2 * 255), (byte)((1 - (weight - 0.5f) * 2) * 255), 0);
	}

	private int MeshPointAt(Vector2 screen, Slot slot, Attachment drawing, Mesh mesh)
	{
		var posed = MeshOps.Posed(slot, drawing, mesh);
		int best = -1;
		float bestDistance = 9f;
		for (int i = 0; i < posed.Length; i++)
		{
			float d = Vector2.Distance(_camera.WorldToScreen(posed[i]), screen);
			if (d < bestDistance) (best, bestDistance) = (i, d);
		}
		return best;
	}

	// True: the mouse went down for the mesh tool (a point taken hold of, or weights painted)
	private bool MeshMouseDown(Vector2 screen, MouseButton button)
	{
		if (_meshTool == MeshTool.None || button != MouseButton.Left || MeshPart() is not { Mesh: { } mesh } part) return false;
		if (_meshTool == MeshTool.Weights)
		{
			if (_state.AnimateMode || WeightBone() is null) return false;
			var drawing = part.Drawing;
			_meshDrag = new MeshDrag { Slot = part.Slot, Drawing = drawing, Point = -1, Structure = _state.CaptureStructure(), Subtract = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) };
			if (_state.EditSkin != null && _state.EditableDrawing(part.Slot) is { } own) _meshDrag.Drawing = own;
			PaintWeightsAt(screen);
			return true;
		}

		int point = MeshPointAt(screen, part.Slot, part.Drawing, mesh);
		if (point < 0) return false;
		var drag = new MeshDrag { Slot = part.Slot, Drawing = part.Drawing, Point = point, Start = _camera.ScreenToWorld(screen) };
		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			drag.Keys = AnimationSnapshot.Capture(_state.Animation);
			drag.StartOffsets = part.Slot.DeformOf(part.Drawing)?.ToArray() ?? new Vector2[mesh.Points.Length];
		}
		else
		{
			drag.Structure = _state.CaptureStructure();
			if (_state.EditSkin != null && _state.EditableDrawing(part.Slot) is { } own) drag.Drawing = own;
		}
		_meshDrag = drag;
		return true;
	}

	private void MeshMouseMove(Vector2 screen)
	{
		if (_meshDrag is not { } drag)
		{
			_meshHover = _meshTool == MeshTool.Points && MeshPart() is { Mesh: { } m } p ? MeshPointAt(screen, p.Slot, p.Drawing, m) : -1;
			return;
		}
		if (drag.Point < 0)
		{
			PaintWeightsAt(screen);
			return;
		}
		if (drag.Drawing.Mesh is not { } mesh || drag.Point >= mesh.Points.Length) return;
		var world = _camera.ScreenToWorld(screen);
		if (!Matrix3x2.Invert(MeshOps.PointMatrix(drag.Slot, drag.Drawing, mesh, drag.Point), out var toPixels)) return;
		if (drag.Keys != null && drag.StartOffsets != null)
		{
			// Animate: a deform key here, the point pushed by how far the mouse went (in its pixels)
			var offsets = (Vector2[])drag.StartOffsets.Clone();
			offsets[drag.Point] += Vector2.TransformNormal(world - drag.Start, toPixels);
			MeshOps.KeyDeform(_state.Animation, drag.Slot, drag.Drawing, offsets, _state.Frame, _state.Fps);
		}
		else
			drag.Drawing.Mesh = MeshOps.MovePoint(mesh, drag.Point, Vector2.Transform(world, toPixels));
		drag.Changed = true;
	}

	private void PaintWeightsAt(Vector2 screen)
	{
		if (_meshDrag is not { } drag || drag.Drawing.Mesh is not { Weighted: true } mesh || WeightBone() is not { } bone) return;
		var posed = MeshOps.Posed(drag.Slot, drag.Drawing, mesh);
		float reach = (float)MeshBrush.Value;
		var touched = new Dictionary<int, float>();
		for (int i = 0; i < posed.Length; i++)
		{
			float d = Vector2.Distance(_camera.WorldToScreen(posed[i]), screen);
			if (d <= reach) touched[i] = 1f - d / reach * 0.7f;   // the middle of the brush, most
		}
		if (touched.Count == 0) return;
		float amount = (float)MeshStrength.Value * 0.35f * (drag.Subtract ? -1f : 1f);
		drag.Drawing.Mesh = MeshOps.Paint(mesh, bone, touched, amount);
		drag.Changed = true;
	}

	private void MeshMouseUp()
	{
		if (_meshDrag is not { } drag) return;
		_meshDrag = null;
		if (!drag.Changed) return;
		string name = drag.Point < 0 ? $"Paint weights on {drag.Slot.Name}"
			: drag.Keys != null ? $"Deform {drag.Slot.Name} @ frame {_state.Frame}" : $"Move a mesh point of {drag.Slot.Name}";
		if (drag.Keys != null)
			_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(name, drag.Keys, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
		else if (drag.Structure != null)
			_state.PushStructure(drag.Structure, name);
		SyncMeshPanel();
	}
}
