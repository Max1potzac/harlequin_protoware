using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Meshes (the part panel's Mesh box), as the Windows editor has them: a grid made over the
// selected part's picture so it can bend; weights giving its points to bones (worked out from
// where the bones are, all on one, or painted in the viewport); its points dragged (Build: where
// they sit on the picture; Animate: deform keys at the playhead). Editing a skin: the skin's own
// copy of the drawing changes. The viewport side is MeshEditTool.
public sealed partial class MainWindow
{
	private readonly Border _meshBox = new() { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
	private readonly Button _meshMake = new() { Content = "Make a mesh ▾" };
	private readonly Button _meshRemove = new() { Content = "Remove" };
	internal readonly ToggleButton MeshPoints = new() { Content = "Edit points" };
	internal readonly ToggleButton MeshWeights = new() { Content = "Paint weights" };
	private readonly Button _meshWeightsMenu = new() { Content = "Weights ▾" };
	internal readonly ComboBox MeshBone = new() { MinWidth = 120 };
	internal readonly Slider MeshBrush = new() { Minimum = 5, Maximum = 120, Value = 30, Width = 110 };
	internal readonly Slider MeshStrength = new() { Minimum = 0.05, Maximum = 1, Value = 0.5, Width = 110 };
	internal readonly TextBlock MeshHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	private bool _syncingMesh;
	private string _meshPanelKey = "";   // what the panel was last filled in for

	internal int MeshHover => _mesh.Hover;
	internal bool MeshBoxShown => _meshBox.IsVisible;

	internal MeshMode MeshTool
	{
		get => _mesh.Mode;
		set => _mesh.Mode = value;
	}

	// The selected part's drawing that shows now, and its mesh (null: no part, or no mesh)
	private (Slot Slot, Attachment Drawing, Mesh? Mesh)? MeshPart() => _session.MeshOf(State.SelectedSlot);

	private Bone? WeightBone() =>
		MeshBone.SelectedItem is string name ? State.Skeleton.FindBone(name) : State.SelectedSlot?.Bone;

	private Control BuildMeshBox()
	{
		_mesh.WeightBone = WeightBone;
		ToolTip.SetTip(_meshMake, "A grid over the picture, so bones (and deform keys) can bend it");
		_meshMake.Click += (_, _) => MeshMakeMenu().ShowAt(_meshMake);
		_meshRemove.Click += (_, _) =>
		{
			if (MeshPart() is { } part) _session.RemoveMesh(part.Slot);
			MeshTool = MeshMode.None;
			SyncMeshPanel(force: true);
		};
		ToolTip.SetTip(MeshPoints, "Drag the mesh's points in the viewport: in Build, where they sit on the picture; in Animate, deform keys at the playhead");
		MeshPoints.Click += (_, _) => { MeshTool = MeshPoints.IsChecked == true ? MeshMode.Points : MeshMode.None; SyncMeshPanel(force: true); };
		ToolTip.SetTip(MeshWeights, "Drag over the points in the viewport: the chosen bone gets more of them (Alt: less)");
		MeshWeights.Click += (_, _) => { MeshTool = MeshWeights.IsChecked == true ? MeshMode.Weights : MeshMode.None; SyncMeshPanel(force: true); };
		ToolTip.SetTip(_meshWeightsMenu, "Which bones bend the mesh, worked out for every point");
		_meshWeightsMenu.Click += (_, _) => { if (MeshWeightsMenu() is { } menu) menu.ShowAt(_meshWeightsMenu); };
		ToolTip.SetTip(MeshBone, "The bone the weight brush gives the points to");
		ToolTip.SetTip(MeshBrush, "How far the weight brush reaches, on screen");
		ToolTip.SetTip(MeshStrength, "How much a stroke gives");
		MeshBone.SelectionChanged += (_, _) => { if (!_syncingMesh) SyncMeshPanel(force: true); };
		MeshBrush.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _mesh.BrushPx = (float)MeshBrush.Value; };
		MeshStrength.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _mesh.Strength = (float)MeshStrength.Value; };

		static WrapPanel Row(params Control[] controls)
		{
			var row = new WrapPanel();
			foreach (var c in controls) { c.Margin = new Thickness(0, 0, 6, 6); row.Children.Add(c); }
			return row;
		}
		static TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		_meshBox.Child = new StackPanel
		{
			Spacing = 2,
			Children =
			{
				new TextBlock { Text = "Mesh", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
				Row(_meshMake, _meshRemove),
				Row(MeshPoints, MeshWeights, _meshWeightsMenu),
				Row(Words("Bone"), MeshBone),
				Row(Words("Brush"), MeshBrush, Words("Strength"), MeshStrength),
				MeshHint
			}
		};
		return _meshBox;
	}

	private MenuFlyout MeshMakeMenu()
	{
		var menu = new MenuFlyout();
		foreach (var (label, across) in new[] { ("Fine", 16), ("Medium", 10), ("Coarse", 6), ("Just the corners", 1) })
		{
			var item = new MenuItem { Header = across == 1 ? label : $"{label} ({across} across)" };
			item.Click += (_, _) => MakeMesh(across);
			menu.Items.Add(item);
		}
		return menu;
	}

	internal void MakeMesh(int across)
	{
		if (MeshPart() is not { } part || !_session.MakeMesh(part.Slot, across)) return;
		MeshTool = MeshMode.Points;
		SyncMeshPanel(force: true);
	}

	internal MenuFlyout? MeshWeightsMenu()
	{
		if (MeshPart() is not { Mesh: not null } part) return null;
		var menu = new MenuFlyout();
		void Add(string header, string tip, Action weigh)
		{
			var item = new MenuItem { Header = header };
			ToolTip.SetTip(item, tip);
			item.Click += (_, _) =>
			{
				weigh();
				SyncMeshPanel(force: true);
			};
			menu.Items.Add(item);
		}
		var slot = part.Slot;
		Add("From the nearest bones", "Each point shared by the 3 bones nearest to it, the nearer the more (then paint to adjust)",
			() => _session.WeighMesh(slot, EditorSession.Weights.Nearest, "From the nearest bones"));
		var picked = State.PickedBones.ToList();
		if (picked.Count > 0)
			Add($"From the picked bones ({picked.Count})", "As above, among the picked bones only",
				() => _session.WeighMesh(slot, EditorSession.Weights.Nearest, $"From the picked bones ({picked.Count})", among: picked));
		var bone = WeightBone() ?? slot.Bone;
		Add($"All on {bone.Name}", "Every point follows that bone alone", () => _session.WeighMesh(slot, EditorSession.Weights.AllOn, $"All on {bone.Name}", bone));
		Add("None (follows its part, flat)", "No weights: the mesh moves with its part's bone, and deform keys push it about",
			() => _session.WeighMesh(slot, EditorSession.Weights.None, "None (follows its part, flat)"));
		return menu;
	}

	// Every frame: the box shows for a part with a picture, its buttons as the mesh and the mode
	// allow, the hint saying what there is (filled in again only when that changes)
	private void SyncMeshPanel(bool force = false)
	{
		var part = State.SelectedSlot != null && !SceneLevel ? MeshPart() : null;
		_meshBox.IsVisible = part != null;
		if (part is not { } p)
		{
			if (MeshTool != MeshMode.None) MeshTool = MeshMode.None;
			return;
		}
		bool has = p.Mesh != null, building = !State.AnimateMode;
		bool weighted = p.Mesh?.Weighted == true;
		if (!has || (MeshTool == MeshMode.Weights && !(weighted && building))) MeshTool = MeshMode.None;
		string key = $"{p.Slot.GetHashCode()}|{p.Drawing.GetHashCode()}|{p.Mesh?.GetHashCode()}|{p.Mesh?.Points.Length}|{weighted}|{building}|{MeshTool}|{State.Skeleton.AllBones().Count()}|{State.Undo.NextUndoName}";
		if (!force && key == _meshPanelKey) return;
		_meshPanelKey = key;

		_syncingMesh = true;
		_meshMake.IsEnabled = building;
		_meshRemove.IsEnabled = has && building;
		MeshPoints.IsEnabled = has;
		MeshWeights.IsEnabled = has && building && weighted;
		_meshWeightsMenu.IsEnabled = has && building;
		MeshPoints.IsChecked = MeshTool == MeshMode.Points;
		MeshWeights.IsChecked = MeshTool == MeshMode.Weights;
		var names = State.Skeleton.AllBones().Select(b => b.Name).ToList();
		if (MeshBone.ItemsSource is not List<string> listed || !listed.SequenceEqual(names))
		{
			var chosen = MeshBone.SelectedItem as string;
			MeshBone.ItemsSource = names;
			MeshBone.SelectedItem = chosen != null && names.Contains(chosen) ? chosen : p.Slot.Bone.Name;
		}
		MeshBone.SelectedItem ??= p.Slot.Bone.Name;
		_syncingMesh = false;
		MeshHint.Text = p.Mesh switch
		{
			null => "No mesh: the picture stays flat. Make a mesh to let it bend.",
			{ Weighted: false } m => $"{m.Points.Length} points, following {p.Slot.Bone.Name} flat. Weights ▾ lets bones bend it; in Animate, Edit points keys deforms.",
			{ } m => $"{m.Points.Length} points, {m.Weights!.SelectMany(w => w).Select(w => w.Bone).Distinct().Count()} bone(s) bending it." +
				(MeshTool == MeshMode.Weights ? " Drag over the points to give the chosen bone more (Alt: less)." : "")
		};
	}
}
