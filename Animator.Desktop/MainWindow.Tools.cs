using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The viewport's own controls, as the Windows editor has them (ViewTools, Workspace):
// - down its left side, the tools (Select, Move, Rotate, Paint: 1-4), and under them what it
//   shows (bones, pictures, onion ghosts, hitboxes, the grid, snapping to it, the view reset)
// - over its top, Build | Animate (K), and the banner while a skin is edited
// - the play buttons at the start of the timeline's bar
// The icons are drawn here (on a 24 × 24 square), so they look the same everywhere.
public sealed partial class MainWindow
{
	private readonly Dictionary<ViewTool, ToggleButton> _toolButtons = new();
	private readonly Dictionary<string, ToggleButton> _viewToggles = new();   // command → its button
	private readonly ToggleButton _buildChoice = new(), _animateChoice = new();
	private readonly Button _sceneButton = new() { Padding = new Thickness(8, 2) };
	internal readonly Button KeyButton = new() { Content = "Key" };
	private readonly Border _toolPanel = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(3) };
	private readonly Border _workspacePanel = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(3) };
	private readonly List<Control> _objectOnlyTools = new();   // (put away at scene level)
	private Control _toolColumn = null!, _overTop = null!;
	private Button _playButton = null!;
	private PathIcon _playIcon = null!;
	private bool? _shownPlaying;

	internal ToggleButton ToolButton(ViewTool tool) => _toolButtons[tool];
	internal ToggleButton ViewToggle(string command) => _viewToggles[command];
	internal ToggleButton BuildChoice => _buildChoice;
	internal ToggleButton AnimateChoice => _animateChoice;
	internal Button SceneButton => _sceneButton;
	internal bool ToolsShown => _toolColumn.IsVisible && _toolButtons[ViewTool.Select].IsVisible;

	// ======================= the icons =======================

	private static class Icons
	{
		public const string Select = "M5,2 L5,19 L9.5,14.8 L12.6,21.5 L15.3,20.3 L12.2,13.7 L18.5,13.7 Z";
		public const string Move = "M12,1.5 L15.8,5.3 L13,5.3 L13,11 L18.7,11 L18.7,8.2 L22.5,12 L18.7,15.8 L18.7,13 L13,13 L13,18.7 L15.8,18.7 L12,22.5 L8.2,18.7 L11,18.7 L11,13 L5.3,13 L5.3,15.8 L1.5,12 L5.3,8.2 L5.3,11 L11,11 L11,5.3 L8.2,5.3 Z";
		public const string Rotate = "M12,3 A9,9 0 1 0 21,12 L18.5,12 A6.5,6.5 0 1 1 12,5.5 L12,8 L16,4.25 L12,0.5 Z";
		public const string Paint = "M20.5,3.5 C19.9,2.9 19,2.9 18.4,3.5 L10.2,11.7 L12.3,13.8 L20.5,5.6 C21.1,5 21.1,4.1 20.5,3.5 Z M9.2,12.8 C7.2,12.8 5.6,14.4 5.6,16.4 C5.6,17.9 4.6,19.2 3,19.6 C4.3,20.8 6,21.5 7.8,21.5 C10.8,21.5 13.2,19.1 13.2,16.1 C13.2,15.6 13.1,15.2 13,14.8 Z";
		public const string Build = "M14.5,2.5 L21.5,9.5 L19.5,11.5 L17.5,9.5 L15.8,11.2 L12.8,8.2 L14.5,6.5 L12.5,4.5 Z M11.4,9.6 L14.4,12.6 L5.2,21.8 C4.4,22.6 3.1,22.6 2.2,21.8 C1.4,20.9 1.4,19.6 2.2,18.8 Z";
		public const string Animate = "M7,4 L20,12 L7,20 Z";
		public const string Bones = "F1 M6,10.5 H18 V13.5 H6 Z M2.5,9.5 A2.5,2.5 0 1 1 7.5,9.5 A2.5,2.5 0 1 1 2.5,9.5 Z M2.5,14.5 A2.5,2.5 0 1 1 7.5,14.5 A2.5,2.5 0 1 1 2.5,14.5 Z M16.5,9.5 A2.5,2.5 0 1 1 21.5,9.5 A2.5,2.5 0 1 1 16.5,9.5 Z M16.5,14.5 A2.5,2.5 0 1 1 21.5,14.5 A2.5,2.5 0 1 1 16.5,14.5 Z";   // (F1: overlaps filled, not cut out)
		public const string Pictures = "M2.5,4.5 H21.5 V19.5 H2.5 Z M4.5,6.5 V17.5 H19.5 V6.5 Z M6,16 L10,10.5 L13,14 L15,12 L18,16 Z M14.5,9 A1.5,1.5 0 1 0 17.5,9 A1.5,1.5 0 1 0 14.5,9 Z";
		public const string Onion = "M10,8 H21 V20 H10 Z M6,5 H17 V6.5 H7.5 V16 H6 Z M2,2 H13 V3.5 H3.5 V12 H2 Z";
		public const string Hitboxes = "M3,3 H21 V21 H3 Z M5,5 V19 H19 V5 Z M10,12 A2,2 0 1 0 14,12 A2,2 0 1 0 10,12 Z";
		public const string Snap = "M5,3 H9.5 V12 A2.5,2.5 0 0 0 14.5,12 V3 H19 V12 A7,7 0 0 1 5,12 Z";
		public const string Reset = "M3,3 H9 V5 H5 V9 H3 Z M15,3 H21 V9 H19 V5 H15 Z M3,15 H5 V19 H9 V21 H3 Z M19,15 H21 V21 H15 V19 H19 Z M10,10 H14 V14 H10 Z";
		public const string Restart = "M5,5 H7.5 V19 H5 Z M19,5 L9,12 L19,19 Z";
		public const string Back = "M16,4.5 L8.5,12 L16,19.5 L17.8,17.7 L12.1,12 L17.8,6.3 Z";
		public const string Play = "M7,4.5 L19.5,12 L7,19.5 Z";
		public const string Pause = "M6.5,5 H10.5 V19 H6.5 Z M13.5,5 H17.5 V19 H13.5 Z";
		public const string On = "M8,4.5 L15.5,12 L8,19.5 L6.2,17.7 L11.9,12 L6.2,6.3 Z";

		public static string Grid
		{
			get
			{
				var cells = new System.Text.StringBuilder("M3,3 H21 V21 H3 Z");
				foreach (var (from, to) in new[] { (4.5, 8.5), (10.0, 14.0), (15.5, 19.5) })
					foreach (var (top, bottom) in new[] { (4.5, 8.5), (10.0, 14.0), (15.5, 19.5) })
						cells.Append(System.FormattableString.Invariant($" M{from},{top} V{bottom} H{to} V{top} Z"));
				return cells.ToString();
			}
		}
	}

	// An icon on the 24 × 24 square (its corners marked, so every icon is scaled alike)
	private static PathIcon MakeIcon(string data, double size = 16) => new()
	{
		Data = IconGeometry(data),
		Width = size,
		Height = size
	};

	private static Geometry IconGeometry(string data)
	{
		string fill = data.StartsWith("F1 ", StringComparison.Ordinal) ? "F1 " : "";   // (how overlaps fill goes first)
		return StreamGeometry.Parse(fill + "M0,0 L0,0 Z M24,24 L24,24 Z " + data[fill.Length..]);
	}

	private static ToggleButton IconToggle(string icon, string tip)
	{
		var button = new ToggleButton
		{
			Content = MakeIcon(icon),
			Width = 32,
			Height = 30,
			Padding = new Thickness(0),
			Margin = new Thickness(0, 0, 0, 2),
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(button, tip);
		return button;
	}

	// ======================= down the viewport's left =======================

	private Control BuildToolColumn()
	{
		var column = new StackPanel { Orientation = Orientation.Vertical };
		foreach (var (tool, icon, tip, id) in new[]
		{
			(ViewTool.Select, Icons.Select, "Select: click a bone or part to pick it, drag a box round several", "tool.select"),
			(ViewTool.Move, Icons.Move, "Move: drag anything to move it (the picked move together)", "tool.move"),
			(ViewTool.Rotate, Icons.Rotate, "Rotate: drag to turn bones and parts round their joint or pivot", "tool.rotate"),
			(ViewTool.Paint, Icons.Paint, "Paint: one click on a part opens it in the paint editor (a part of another object opens that object)", "tool.paint")
		})
		{
			var t = tool;
			var button = IconToggle(icon, $"{tip} ({_keys.Describe(id)})");
			button.Click += (_, _) => { SetTool(t); Viewport.Focus(); };
			_toolButtons[tool] = button;
			_objectOnlyTools.Add(button);
			column.Children.Add(button);
		}
		var line = new Border { Height = 1, Margin = new Thickness(3, 3, 3, 5), Background = Brushes.Gray, Opacity = 0.6 };
		_objectOnlyTools.Add(line);
		column.Children.Add(line);

		// What the viewport shows (the View menu's toggles, a click away)
		foreach (var (id, icon, tip, objectOnly) in new[]
		{
			("view.bones", Icons.Bones, "Bones: shown, or hidden (hidden, they can't be picked or dragged in the viewport; the Rig tab still picks them)", true),
			("view.images", Icons.Pictures, "Pictures: shown, or just the bones", false),
			("view.onion", Icons.Onion, "Onion skin: faint ghosts of the frames before (red) and after (blue)", false),
			("view.hitboxes", Icons.Hitboxes, "Hitboxes and points: shown (the Boxes view always shows them)", true),
			("view.grid", Icons.Grid, "The grid (its size: View > Grid size)", false),
			("view.snapGrid", Icons.Snap, "Snap to the grid: what's dragged lands on its lines", false)
		})
		{
			string command = id;
			var button = IconToggle(icon, $"{tip} ({_keys.Describe(command)})");
			button.Click += (_, _) => { _commands[command].Run(); Viewport.Focus(); };
			_viewToggles[command] = button;
			if (objectOnly) _objectOnlyTools.Add(button);
			column.Children.Add(button);
		}
		var reset = new Button
		{
			Content = MakeIcon(Icons.Reset),
			Width = 32,
			Height = 30,
			Padding = new Thickness(0),
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(reset, $"Reset the view: the object (or the stage) in the middle, at its own size ({_keys.Describe("view.reset")})");
		reset.Click += (_, _) => { ResetView(); Viewport.Focus(); };
		column.Children.Add(reset);

		_toolPanel.Child = column;
		_toolColumn = UiSize.Scaled(_toolPanel);
		_toolColumn.HorizontalAlignment = HorizontalAlignment.Left;
		_toolColumn.VerticalAlignment = VerticalAlignment.Top;
		return _toolColumn;
	}

	// ======================= over the viewport's top =======================

	private Control BuildWorkspaceBar()
	{
		Control Choice(ToggleButton button, string icon, string text, string tip, bool animate)
		{
			button.Content = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 7,
				Children = { MakeIcon(icon, 14), new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } }
			};
			button.Padding = new Thickness(14, 4);
			ToolTip.SetTip(button, $"{tip} ({_keys.Describe("anim.mode")} switches)");
			button.Click += (_, _) => { SetAnimating(animate); Viewport.Focus(); };
			return button;
		}
		_buildChoice.Margin = new Thickness(0, 0, 2, 0);
		_workspacePanel.Child = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Children =
			{
				Choice(_buildChoice, Icons.Build, "Build", "Build the object: bones, parts, drawing, skins, hitboxes, all at rest", false),
				Choice(_animateChoice, Icons.Animate, "Animate", "Animate it: the timeline, keys, poses. The rig itself can't change here.", true)
			}
		};
		_skinBanner.Margin = new Thickness(0, 6, 0, 0);
		_skinBanner.Child = SkinBannerText;
		var stack = new StackPanel { Children = { _workspacePanel, _skinBanner } };
		_overTop = UiSize.Scaled(stack);
		_overTop.HorizontalAlignment = HorizontalAlignment.Center;
		_overTop.VerticalAlignment = VerticalAlignment.Top;
		return _overTop;
	}

	internal void SetAnimating(bool animate)
	{
		if (SceneLevel || State.AnimateMode == animate) return;
		ToggleAnimate();
	}

	// ======================= the play buttons =======================

	private IEnumerable<Control> BuildTransport()
	{
		Button B(string icon, string command, string tip, double width = 30)
		{
			var b = new Button
			{
				Content = MakeIcon(icon, 14),
				Width = width,
				Height = 28,
				Padding = new Thickness(0),
				HorizontalContentAlignment = HorizontalAlignment.Center,
				VerticalContentAlignment = VerticalAlignment.Center
			};
			ToolTip.SetTip(b, $"{tip} ({_keys.Describe(command)})");
			b.Click += (_, _) => { _commands[command].Run(); Viewport.Focus(); };
			return b;
		}
		_playButton = B(Icons.Play, "play.toggle", "Play / pause", 36);
		_playIcon = (PathIcon)_playButton.Content!;
		return new Control[] { B(Icons.Restart, "play.restart", "Back to the first frame"), B(Icons.Back, "play.back", "A frame back"), _playButton, B(Icons.On, "play.on", "A frame on") };
	}

	// ======================= every frame =======================

	// The tools, the switches and the play button show what's on; the tools keep clear of the rulers
	private void SyncToolBar()
	{
		bool scene = SceneLevel;
		foreach (var (tool, button) in _toolButtons) button.IsChecked = _tool.Tool == tool;
		foreach (var (id, button) in _viewToggles)
			if (ViewChecks.TryGetValue(id, out var on)) button.IsChecked = on();
		foreach (var c in _objectOnlyTools) c.IsVisible = !scene;
		_workspacePanel.IsVisible = !scene;
		_buildChoice.IsChecked = !State.AnimateMode;
		_animateChoice.IsChecked = State.AnimateMode;
		KeyButton.IsEnabled = !scene;
		double edge = _settings.ShowRulers ? ViewAids.RulerSize + 6 : 6;
		if (_toolColumn.Margin.Left != edge) _toolColumn.Margin = new Thickness(edge, edge, 0, 0);
		var top = new Thickness(0, _settings.ShowRulers ? ViewAids.RulerSize + 6 : 8, 0, 0);
		if (_overTop.Margin != top) _overTop.Margin = top;
		if (_shownPlaying != State.Playing)
		{
			_shownPlaying = State.Playing;
			_playIcon.Data = IconGeometry(State.Playing ? Icons.Pause : Icons.Play);
		}
		_sceneButton.Content = scene ? "Edit ▸" : $"◂ Scene ({_keys.Gesture("edit.back")})";
		_sceneButton.IsEnabled = !scene || PickedObject != null;
	}

	// The panels over the viewport: a backing of the window's own colour, a little see-through
	private void ShowOverlayColours(Color window)
	{
		var backing = new SolidColorBrush(Color.FromArgb(0xE0, window.R, window.G, window.B));
		_toolPanel.Background = backing;
		_workspacePanel.Background = backing;
	}
}
