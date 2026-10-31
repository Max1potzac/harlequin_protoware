using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer;

// The viewport's tools, down its left side inside an object (1-4): Select, Move, Rotate, Paint
// (see BoneTool.Tool). At scene level they hide: objects are picked and moved as before.
public partial class MainWindow
{
	private bool _syncingViewTool;

	private void SetViewTool(ViewTool tool)
	{
		_tool.Tool = tool;
		_syncingViewTool = true;
		SelectToolButton.IsChecked = tool == ViewTool.Select;
		MoveToolButton.IsChecked = tool == ViewTool.Move;
		RotateToolButton.IsChecked = tool == ViewTool.Rotate;
		PaintToolButton.IsChecked = tool == ViewTool.Paint;
		_syncingViewTool = false;
		UpdateStatus();
	}

	private void ViewTool_Checked(object sender, RoutedEventArgs e)
	{
		if (_syncingViewTool || _tool == null || sender is not RadioButton { Tag: string name }) return;
		if (Enum.TryParse<ViewTool>(name, out var tool)) SetViewTool(tool);
	}

	// Every frame: the tools show inside an object only
	private void SyncViewToolBar()
	{
		var shown = _sceneLevel ? Visibility.Collapsed : Visibility.Visible;
		if (ViewToolBar.Visibility != shown) ViewToolBar.Visibility = shown;
	}
}
