using Avalonia.Controls;

namespace Animator.Desktop;

// The timeline's two views: the dopesheet (keys in rows) and the graph editor (Curves: the picked
// bones' and parts' numbers as curves, with Bézier handles to ease them; see GraphPanel). Ctrl+G
// or the Curves box switches. Both share the selected keys and the key menu (curves and easing
// included). As the Windows editor has it.
public sealed partial class MainWindow
{
	private GraphPanel? _graphPanel;
	internal GraphPanel Graph => _graphPanel ??= new GraphPanel(State);
	private bool _graphMode;
	private bool _graphPressed;   // the current press went to the curves

	internal readonly CheckBox CurvesBox = new() { Content = "Curves" };

	internal bool GraphMode => _graphMode;

	// The curves only show inside an object (at scene level, the clips)
	private bool ShowingGraph => _graphMode && !SceneLevel;

	internal void SetGraphMode(bool on)
	{
		_graphMode = on;
		_syncingTimelineTools = true;
		CurvesBox.IsChecked = on;
		_syncingTimelineTools = false;
		TimelineView.InvalidateVisual();
		AnimHint(on ? "Curves: the picked's keys as curves. Drag a key, or the handles of the selected one; wheel zooms the values, middle-drag moves them, a double-click fits them." : "");
	}

	private Control BuildCurvesBox()
	{
		ToolTip.SetTip(CurvesBox, $"The graph editor: the picked bones' and parts' keys as curves, with Bézier handles to ease them ({_keys.Describe("anim.graph")})");
		CurvesBox.IsCheckedChanged += (_, _) => { if (!_syncingTimelineTools) SetGraphMode(CurvesBox.IsChecked == true); };
		return CurvesBox;
	}
}
