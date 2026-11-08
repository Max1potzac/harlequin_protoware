using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Build and Animate as the Windows editor has them (Workspace), inside an object:
// - Build: the object itself, at rest. The timeline is put away and nothing plays (play switches
//   to Animate). The frame round the viewport is blue.
// - Animate: the timeline, keys and poses. What builds the rig (+ Bone, + Part, Bind, Give it
//   bones, deleting bones and parts, Snap to ends, Parts stay put) is put away. The frame is red.
// At scene level the timeline is the scene's, and the properties panel is put away (the picked
// object's settings are in the left panel). And + Bone, Snap to ends, the banner while a skin is
// edited, and a bone deleted with what's on it or without.
public sealed partial class MainWindow
{
	private readonly Border _workspaceFrame = new() { BorderThickness = new Thickness(3), IsHitTestVisible = false };
	private readonly Border _skinBanner = new()
	{
		CornerRadius = new CornerRadius(4),
		Padding = new Thickness(8, 2),
		HorizontalAlignment = HorizontalAlignment.Center,
		IsHitTestVisible = false,
		IsVisible = false
	};
	internal readonly TextBlock SkinBannerText = new() { Foreground = Brushes.White };
	internal readonly Button AddBoneButton = new() { Content = "+ Bone" };
	internal readonly CheckBox SnapJointsBox = new() { Content = "Snap to ends", IsChecked = true };
	private GridSplitter _timelineSplitter = null!, _propsSplitter = null!;
	private Control _propsPanel = null!, _timelineBar = null!;
	private bool? _shownAnimating, _shownSceneLevel;
	private SceneObject? _shownActive;
	private double _timelineHeight = 190, _propertiesWidth = 300;

	internal bool SkinBannerShown => _skinBanner.IsVisible;
	internal bool TimelineShown => TimelineView.IsVisible;
	internal bool PropertiesShown => _propsPanel.IsVisible;
	internal IBrush? WorkspaceFrameBrush => _workspaceFrame.BorderBrush;

	// (the viewport's cell gets the frame, the switches over its top and the tools down its left)
	private void AddWorkspaceOverlays(Panel views)
	{
		views.Children.Add(_workspaceFrame);
		views.Children.Add(BuildWorkspaceBar());
		views.Children.Add(BuildToolColumn());
	}

	// + Bone and Snap to ends
	private void WireRigBuilding()
	{
		ToolTip.SetTip(AddBoneButton, "A child of the picked bone (or a root bone). Tip: Ctrl+right-drag in the viewport draws one.");
		AddBoneButton.Click += (_, _) => AddBone();
		ToolTip.SetTip(SnapJointsBox, "Build: a new bone started near the picked bone's end, or a joint dragged near its parent's end, snaps exactly onto it and stays there. Pull it further away to free it.");
		SnapJointsBox.IsCheckedChanged += (_, _) => _tool.SnapJoints = SnapJointsBox.IsChecked == true;
	}

	internal void AddBone()
	{
		if (SceneLevel || State.AnimateMode || IsDrawn) return;
		var made = _session.AddBone(State.Selected, _tool.SnapJoints);
		State.Selected = made;
		State.SelectedSlot = null;
		RefreshRigList();
	}

	// Deleting a bone: with everything on it, or handing what's on it to its parent
	internal MenuFlyout DeleteBoneMenu(Bone bone)
	{
		var menu = new MenuFlyout();
		if (bone.Parent is { } parent)
		{
			var keep = new MenuItem { Header = $"Delete \"{bone.Name}\": its parts and bones go to \"{parent.Name}\"" };
			keep.Click += (_, _) => _session.DissolveBone(bone);
			menu.Items.Add(keep);
		}
		var all = new MenuItem { Header = $"Delete \"{bone.Name}\" with everything on it" };
		all.Click += (_, _) => _session.DeleteBone(bone);
		menu.Items.Add(all);
		return menu;
	}

	// Every frame: the layout follows the mode (K, going in and out of objects, an undo)
	private void SyncWorkspace()
	{
		bool scene = SceneLevel, animate = State.AnimateMode;
		if (!scene && !animate && State.Playing) State.Playing = false;   // building: nothing plays
		SyncSkinBanner();
		if (_shownAnimating == animate && _shownSceneLevel == scene && _shownActive == _session.Active) return;
		_shownAnimating = animate;
		_shownSceneLevel = scene;
		_shownActive = _session.Active;

		_workspaceFrame.BorderBrush = scene ? null : new SolidColorBrush(Themes.ColorOf(animate ? "AnimateFrame" : "BuildFrame"));
		bool timeline = scene || animate;   // (the scene has its own)
		if (_rows[3].Height.Value > 0) _timelineHeight = _rows[3].Height.Value;
		_rows[3].Height = new GridLength(timeline ? _timelineHeight : 0);
		TimelineView.IsVisible = _timelineSplitter.IsVisible = _timelineBar.IsVisible = timeline;

		// The properties: put away at scene level (the picked object's settings are on the left)
		if (_columns[4].Width.Value > 0) _propertiesWidth = _columns[4].Width.Value;
		_columns[4].Width = new GridLength(scene ? 0 : _propertiesWidth);
		_propsPanel.IsVisible = _propsSplitter.IsVisible = !scene;

		ModeText.Text = scene
			? $"Scene: drag objects to place them. Double-click one (or {_keys.Gesture("scene.open")}) to edit it."
			: $"Editing \"{_session.Active?.Name}\". Other objects are faded. {_keys.Gesture("edit.back")}: back to the scene.";
		RefreshAnimationsBar();
		SyncRigTools();
	}

	private void SyncSkinBanner()
	{
		var skin = SceneLevel ? null : State.EditSkin;
		_skinBanner.IsVisible = skin != null;
		if (skin == null) return;
		string text = $"Editing the skin {skin.Name}: changed parts go into it";
		if (SkinBannerText.Text != text) SkinBannerText.Text = text;
		_skinBanner.Background ??= new SolidColorBrush(Themes.ColorOf("SkinEditing"));
	}
}
