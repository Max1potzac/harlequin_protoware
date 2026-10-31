using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer;

// The timeline's two views: the dopesheet (keys in rows) and the graph editor (Curves: the keys'
// numbers as curves, with Bézier handles to ease them; see GraphPanel). Ctrl+G or the Curves box
// switches. Both share the selected keys, and the key menu (curves and easing included).
public partial class MainWindow
{
	private GraphPanel? _graphPanel;
	private GraphPanel Graph => _graphPanel ??= new GraphPanel(_state);
	private bool _graphMode;

	private void SetGraphMode(bool on)
	{
		_graphMode = on;
		_syncingTimelineTools = true;
		GraphBox.IsChecked = on;
		_syncingTimelineTools = false;
		TimelineView.InvalidateVisual();
		UpdateStatus();
	}

	private void Graph_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingTimelineTools) return;
		SetGraphMode(GraphBox.IsChecked == true);
	}

	// The easing presets: Bézier handles for the selected keys' stretches
	private static readonly (string Name, Bezier Curve, string Tip)[] Easings =
	{
		("Ease in", Bezier.EaseIn, "Starts slowly, ends at full speed"),
		("Ease out", Bezier.EaseOut, "Starts at full speed, slows into the next key"),
		("Ease in and out", Bezier.EaseInOut, "Slow at both ends"),
		("Overshoot", Bezier.Overshoot, "Goes past the next key's value, then settles back on it"),
		("Anticipate", Bezier.Anticipate, "Backs off first, then goes")
	};

	private void AddEasingItems(ContextMenu menu, Action<Bezier, string> set)
	{
		var ease = new MenuItem { Header = "Bézier easing", ToolTip = "A curve with handles (shown with a ring): shape it in the Curves view" };
		foreach (var (name, curve, tip) in Easings)
		{
			var item = new MenuItem { Header = name, ToolTip = tip };
			item.Click += (_, _) => set(curve, name.ToLowerInvariant());
			ease.Items.Add(item);
		}
		menu.Items.Add(ease);
	}
}
