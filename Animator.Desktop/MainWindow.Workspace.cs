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
// And the banner over the viewport while a skin is edited, the play buttons, the objects to switch
// to from inside one, and a bone deleted with what's on it or without.
public sealed partial class MainWindow
{
	private readonly Border _workspaceFrame = new() { BorderThickness = new Thickness(3), IsHitTestVisible = false };
	private readonly Border _skinBanner = new()
	{
		CornerRadius = new CornerRadius(4),
		Padding = new Thickness(8, 2),
		Margin = new Thickness(0, 30, 0, 0),
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Top,
		IsHitTestVisible = false,
		IsVisible = false
	};
	internal readonly TextBlock SkinBannerText = new() { Foreground = Brushes.White };
	internal readonly Button AddBoneButton = new() { Content = "+ Bone" };
	internal readonly CheckBox SnapJointsBox = new() { Content = "Snap to ends", IsChecked = true };
	internal readonly ComboBox ObjectSwitch = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private Control _objectSwitchRow = null!;
	private GridSplitter _timelineSplitter = null!;
	private bool? _shownAnimating, _shownSceneLevel;
	private double _timelineHeight = 190;
	private bool _syncingObjects;
	private string? _objectsSwitchable;   // what the object switch was filled in from

	internal bool SkinBannerShown => _skinBanner.IsVisible;
	internal bool TimelineShown => TimelineView.IsVisible;
	internal IBrush? WorkspaceFrameBrush => _workspaceFrame.BorderBrush;

	private sealed record ObjectChoice(SceneObject Object)
	{
		public override string ToString() => Object.Name;
	}

	// (the viewport's cell gets the frame and the banner over it)
	private void AddWorkspaceOverlays(Panel views)
	{
		_skinBanner.Child = SkinBannerText;
		views.Children.Add(_workspaceFrame);
		views.Children.Add(_skinBanner);
	}

	// The rig tab's own: + Bone, Snap to ends, and the objects to switch to
	private void WireRigBuilding()
	{
		ToolTip.SetTip(AddBoneButton, "A child of the picked bone (or a root bone). Tip: Ctrl+right-drag in the viewport draws one.");
		AddBoneButton.Click += (_, _) => AddBone();
		ToolTip.SetTip(SnapJointsBox, "Build: a new bone started near the picked bone's end, or a joint dragged near its parent's end, snaps exactly onto it and stays there. Pull it further away to free it.");
		SnapJointsBox.IsCheckedChanged += (_, _) => _tool.SnapJoints = SnapJointsBox.IsChecked == true;
		ToolTip.SetTip(ObjectSwitch, "The scene's objects: pick another to edit it");
		ObjectSwitch.SelectionChanged += (_, _) =>
		{
			if (_syncingObjects || SceneLevel || ObjectSwitch.SelectedItem is not ObjectChoice { Object: var obj } || obj == _session.Active) return;
			Avalonia.Threading.Dispatcher.UIThread.Post(() => EnterObject(obj));   // (not while the list is in the middle of its own change)
		};
		_objectSwitchRow = Labelled("Object", ObjectSwitch);
		_objectSwitchRow.Margin = new Thickness(4, 4, 4, 0);
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

	// The objects to switch to, inside one (when there's more than one)
	private void SyncObjectSwitch()
	{
		bool shown = !SceneLevel && Scene.Objects.Count > 1;
		_objectSwitchRow.IsVisible = shown;
		if (!shown) return;
		_syncingObjects = true;
		try
		{
			var choices = Scene.Objects.Select(o => new ObjectChoice(o)).ToList();
			ObjectSwitch.ItemsSource = choices;
			ObjectSwitch.SelectedItem = choices.FirstOrDefault(c => c.Object == _session.Active);
		}
		finally
		{
			_syncingObjects = false;
		}
	}

	// Every frame: the layout follows the mode (K, going in and out of objects, an undo)
	private void SyncWorkspace()
	{
		bool scene = SceneLevel, animate = State.AnimateMode;
		if (!scene && !animate && State.Playing) State.Playing = false;   // building: nothing plays
		SyncSkinBanner();
		string objects = $"{scene}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_session.Active!)}|{string.Join("\n", Scene.Objects.Select(o => o.Name))}";
		if (objects != _objectsSwitchable)
		{
			_objectsSwitchable = objects;
			SyncObjectSwitch();
		}
		if (_shownAnimating == animate && _shownSceneLevel == scene) return;
		_shownAnimating = animate;
		_shownSceneLevel = scene;

		_workspaceFrame.BorderBrush = scene ? null : new SolidColorBrush(Themes.ColorOf(animate ? "AnimateFrame" : "BuildFrame"));
		bool timeline = scene || animate;   // (the scene has its own)
		if (_rows[3].Height.Value > 0) _timelineHeight = _rows[3].Height.Value;
		_rows[3].Height = new GridLength(timeline ? _timelineHeight : 0);
		TimelineView.IsVisible = _timelineSplitter.IsVisible = timeline;
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

	// The play buttons at the start of the bar: back to the start, a frame back, play / pause, a frame on
	private IEnumerable<Control> BuildTransport()
	{
		Button B(string glyph, string command, string tip)
		{
			var b = new Button { Content = glyph, Padding = new Thickness(8, 4), MinWidth = 32, HorizontalContentAlignment = HorizontalAlignment.Center };
			ToolTip.SetTip(b, $"{tip} ({_keys.Describe(command)})");
			b.Click += (_, _) => { _commands[command].Run(); Viewport.Focus(); };
			return b;
		}
		return new Control[] { B("|◀", "play.restart", "Back to the first frame"), B("◀", "play.back", "A frame back"), B("▶", "play.toggle", "Play / pause"), B("▶|", "play.on", "A frame on") };
	}
}
