using System.Windows;
using System.Windows.Media;

namespace Animator.Viewer;

// Two workspaces inside an object, one switch apart (the bar over the viewport, or K):
// - Build: the object itself, at rest (Setup mode): bones, parts, drawing, skins, hitboxes.
//   No timeline; nothing plays.
// - Animate: the timeline, keys and poses (Animate mode). What builds the rig (+ Bone, + Part,
//   Bind, Delete, the bone-fitting options) is put away.
// The frame round the viewport is blue while building, red while animating.
public partial class MainWindow
{
	private bool? _shownAnimating;
	private bool? _shownSceneLevel;
	private GridLength _timelineHeight = new(230);
	private bool _syncingWorkspace;

	private static Brush BuildFrameBrush => Themes.Brush("BuildFrame");
	private static Brush AnimateFrameBrush => Themes.Brush("AnimateFrame");

	private void Workspace_Checked(object sender, RoutedEventArgs e)
	{
		if (_syncingWorkspace || !IsLoaded || _sceneLevel) return;
		SetAnimating(AnimateChoice.IsChecked == true);
	}

	private void SetAnimating(bool animate)
	{
		if (_state.AnimateMode == animate) return;
		_state.AnimateMode = animate;
		SyncWorkspace();
		Viewport.Focus();
	}

	// Every frame: the layout follows the mode (K, the switch, going in and out of objects, undo)
	private void SyncWorkspace()
	{
		bool scene = _sceneLevel, animate = _state.AnimateMode;
		if (!scene && !animate && _state.Playing) _state.Playing = false;   // building: nothing plays
		SyncSkinEditBanner();
		SyncPlayButton();
		SyncMenuButtons();
		if (_shownAnimating == animate && _shownSceneLevel == scene) return;
		_shownAnimating = animate;
		_shownSceneLevel = scene;

		_syncingWorkspace = true;
		BuildChoice.IsChecked = !animate;
		AnimateChoice.IsChecked = animate;
		_syncingWorkspace = false;
		WorkspaceBar.Visibility = scene ? Visibility.Collapsed : Visibility.Visible;
		WorkspaceFrame.BorderBrush = scene ? null : animate ? AnimateFrameBrush : BuildFrameBrush;

		// The timeline: animating, and at scene level (the scene's own); put away while building
		bool timeline = scene || animate;
		if (TimelineRow.Height.Value > 0) _timelineHeight = TimelineRow.Height;
		TimelineRow.Height = timeline ? _timelineHeight : new GridLength(0);
		TimelineArea.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
		SyncPanels(scene, timeline);

		SyncRigMode();   // (what builds the rig shows only while building)
		if (!scene) RefreshRigPanel();
		UpdateStatus();
	}

	// Another theme: panels whose rows were made with the old colours are made again
	private void ThemeChanged()
	{
		_shownAnimating = null;   // (the viewport's frame: again)
		if (!_sceneLevel) RefreshRigPanel();
		else RefreshScenePanel();
		Viewport.InvalidateVisual();
		TimelineView.InvalidateVisual();
	}

	private void SyncSkinEditBanner()
	{
		var skin = _sceneLevel ? null : _state.EditSkin;
		var shown = skin != null ? Visibility.Visible : Visibility.Collapsed;
		if (SkinEditBanner.Visibility != shown) SkinEditBanner.Visibility = shown;
		string text = skin != null ? $"Editing the skin {skin.Name}: changed parts go into it" : "";
		if (SkinEditText.Text != text) SkinEditText.Text = text;
	}
}
