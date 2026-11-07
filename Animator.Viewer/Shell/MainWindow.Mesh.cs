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
	private MeshEditTool _mesh = null!;   // the viewport side (see MeshEditTool)
	private MeshMode _meshTool   // (None until the tool is made: the panels fill in first)
	{
		get => _mesh?.Mode ?? MeshMode.None;
		set { if (_mesh != null) _mesh.Mode = value; }
	}
	private bool _syncingMesh;

	private void InitMeshTool() => _mesh = new MeshEditTool(_session, _camera)
	{
		WeightBone = WeightBone,
		BrushPx = (float)MeshBrush.Value,
		Strength = (float)MeshStrength.Value
	};

	// The selected part's drawing that shows now, and its mesh (null: no part, or no mesh)
	private (Slot Slot, Attachment Drawing, Mesh? Mesh)? MeshPart() => _session.MeshOf(_state.SelectedSlot);

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
		if (!MeshWeightsToggle.IsEnabled && _meshTool == MeshMode.Weights) _meshTool = MeshMode.None;
		if (!has) _meshTool = MeshMode.None;
		MeshPointsToggle.IsChecked = _meshTool == MeshMode.Points;
		MeshWeightsToggle.IsChecked = _meshTool == MeshMode.Weights;
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
				(_meshTool == MeshMode.Weights ? " Drag over the points to give the chosen bone more (Alt: less)." : "")
		};
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
		if (MeshPart() is not { } part || !_session.MakeMesh(part.Slot, across)) return;
		_meshTool = MeshMode.Points;
		SyncMeshPanel();
	}

	private void MeshRemove_Click(object sender, RoutedEventArgs e)
	{
		if (MeshPart() is { } part) _session.RemoveMesh(part.Slot);
		_meshTool = MeshMode.None;
		SyncMeshPanel();
	}

	private void MeshWeightsMenu_Click(object sender, RoutedEventArgs e)
	{
		if (MeshPart() is not { Mesh: not null } part) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Add(string header, string tip, Action weigh)
		{
			var item = new MenuItem { Header = header, ToolTip = tip };
			item.Click += (_, _) =>
			{
				weigh();
				SyncMeshPanel();
			};
			menu.Items.Add(item);
		}
		var slot = part.Slot;
		Add("From the nearest bones", "Each point shared by the 3 bones nearest to it, the nearer the more (then paint to adjust)",
			() => _session.WeighMesh(slot, EditorSession.Weights.Nearest, "From the nearest bones"));
		var picked = _state.PickedBones.ToList();
		if (picked.Count > 0)
			Add($"From the picked bones ({picked.Count})", "As above, among the picked bones only",
				() => _session.WeighMesh(slot, EditorSession.Weights.Nearest, $"From the picked bones ({picked.Count})", among: picked));
		var bone = WeightBone() ?? slot.Bone;
		Add($"All on {bone.Name}", "Every point follows that bone alone", () => _session.WeighMesh(slot, EditorSession.Weights.AllOn, $"All on {bone.Name}", bone));
		Add("None (follows its part, flat)", "No weights: the mesh moves with its part's bone, and deform keys push it about",
			() => _session.WeighMesh(slot, EditorSession.Weights.None, "None (follows its part, flat)"));
		menu.IsOpen = true;
	}

	private void MeshPoints_Click(object sender, RoutedEventArgs e)
	{
		_meshTool = MeshPointsToggle.IsChecked == true ? MeshMode.Points : MeshMode.None;
		SyncMeshPanel();
	}

	private void MeshWeights_Click(object sender, RoutedEventArgs e)
	{
		_meshTool = MeshWeightsToggle.IsChecked == true ? MeshMode.Weights : MeshMode.None;
		SyncMeshPanel();
	}

	private void MeshBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_syncingMesh) SyncMeshPanel();
	}

	// ======================= the viewport =======================

	private void DrawMeshOverlay(SKCanvas canvas)
	{
		if (_meshTool == MeshMode.None || MeshPart() is not { Mesh: { } mesh } part) return;
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
			var bone = _meshTool == MeshMode.Weights ? WeightBone() : null;
			using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
			using var ring = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * px, IsAntialias = true };
			for (int i = 0; i < posed.Length; i++)
			{
				dot.Color = bone != null ? Heat(MeshOps.WeightOf(mesh, i, bone)) : i == _mesh.Hover ? SKColors.Yellow : SKColors.White;
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

	// True: the mouse went down for the mesh tool (a point taken hold of, or weights painted)
	private bool MeshMouseDown(Vector2 screen, PointerButton button)
	{
		_mesh.BrushPx = (float)MeshBrush.Value;
		_mesh.Strength = (float)MeshStrength.Value;
		return _mesh.MouseDown(screen, button, subtract: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
	}

	private void MeshMouseMove(Vector2 screen) => _mesh.MouseMove(screen);

	private void MeshMouseUp()
	{
		if (_mesh.MouseUp()) SyncMeshPanel();
	}
}
