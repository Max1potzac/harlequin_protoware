using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Animator.Viewer;

// Buttons for what was only on right-click menus: the timeline's (Keys ▾) and a part's draw
// order and masking (Order and mask ▾, over the properties)
public partial class MainWindow
{
	private void KeysMenu_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel)
		{
			if (_objectTool.Selected is { } obj && _sceneState.SelectedClip is { } clip) ShowClipMenu(obj, clip, ToFrames(_sceneState.Time));
			else if (_objectTool.Selected != null) AddClipAtPlayhead_Click(sender, e);
			else Tell("Pick an object first: the menu is for its clips.");
			return;
		}
		if (_graphMode)
		{
			if (_state.SelectedKeys.Count > 0) ShowKeyMenu();
			else Tell("Select a key in the curves first.");
			return;
		}
		var picked = _timeline.Range;
		if (_state.SelectedKeys.Count > 0) ShowKeyMenu(picked);
		else ShowTimelineMenu(_state.Frame, picked);
	}

	private void PartMenu_Click(object sender, RoutedEventArgs e)
	{
		if (_shownSlot is not { } slot) return;
		var menu = OrderMenu(slot);
		menu.PlacementTarget = (UIElement)sender;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	// (every frame) the part button shows when a part's properties do
	private void SyncMenuButtons()
	{
		var shown = !_sceneLevel && _shownSlot != null ? Visibility.Visible : Visibility.Collapsed;
		if (PartMenuButton.Visibility != shown) PartMenuButton.Visibility = shown;
	}
}
