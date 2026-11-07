using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Animator.Desktop;

// The menus, the tool bar and the keyboard: the Windows editor's default shortcuts (the ones
// for what this editor does so far)
public sealed partial class MainWindow
{
	private readonly Dictionary<ViewTool, ToggleButton> _toolButtons = new();
	private ToggleButton _animateButton = null!;
	private ToggleButton _playButton = null!;
	private Button _sceneButton = null!;

	// One command: what it does, and its key (shown in the menus)
	private sealed record Command(string Name, string Header, Action Run, KeyGesture? Key = null);

	private readonly Dictionary<string, Command> _commands = new();

	private void Add(string name, string header, Action run, string? key = null) =>
		_commands[name] = new Command(name, header, run, key != null ? KeyGesture.Parse(key) : null);

	private void DefineCommands()
	{
		Add("file.new", "_New project", () => _ = NewProject(), "Ctrl+N");
		Add("file.open", "_Open…", () => _ = OpenProject(), "Ctrl+O");
		Add("file.save", "_Save", () => _ = SaveProject(), "Ctrl+S");
		Add("file.saveAs", "Save _as…", () => _ = SaveProjectAs(), "Ctrl+Shift+S");
		Add("file.import", "_Import…", () => _ = ImportFile(), "Ctrl+I");
		Add("file.quit", "_Quit", Close, "Ctrl+Q");

		Add("edit.undo", "_Undo", () => State.Undo.Undo(), "Ctrl+Z");
		Add("edit.redo", "_Redo", () => State.Undo.Redo(), "Ctrl+Y");
		Add("edit.redo2", "Redo", () => State.Undo.Redo(), "Ctrl+Shift+Z");
		Add("edit.copy", "_Copy the selected keys", () => { if (!SceneLevel) _timeline.Copy(); }, "Ctrl+C");
		Add("edit.paste", "_Paste the keys at the playhead", () => { if (!SceneLevel) _timeline.Paste(); }, "Ctrl+V");
		Add("edit.delete", "_Delete the selected keys", DeleteKeys, "Delete");
		Add("edit.back", "_Back to the scene", LeaveObject, "Escape");

		Add("play.toggle", "_Play / pause", () => State.Playing = !State.Playing, "Space");
		Add("play.restart", "Back to the _first frame", Restart, "R");
		Add("play.back", "A frame _back", () => StepFrames(-1), "Left");
		Add("play.on", "A frame _on", () => StepFrames(+1), "Right");
		Add("play.prevKey", "To the key _before", () => JumpToKey(-1), "Shift+Left");
		Add("play.nextKey", "To the key _after", () => JumpToKey(+1), "Shift+Right");

		Add("anim.mode", "_Build / Animate", ToggleAnimate, "K");
		Add("anim.key", "_Key the pose here", KeyThePose, "S");
		Add("pose.mirror", "_Mirror the pose", MirrorThePose, "Ctrl+M");
		Add("pose.copy", "_Copy the pose", CopyThePose, "Ctrl+Shift+C");
		Add("pose.paste", "_Paste the pose", () => PasteThePose(false), "Ctrl+Shift+V");
		Add("pose.pasteMirrored", "Paste the pose mi_rrored", () => PasteThePose(true), "Ctrl+Alt+V");

		Add("tool.select", "_Select", () => SetTool(ViewTool.Select), "D1");
		Add("tool.move", "_Move", () => SetTool(ViewTool.Move), "D2");
		Add("tool.rotate", "_Rotate", () => SetTool(ViewTool.Rotate), "D3");

		Add("view.reset", "_Reset the view", ResetView, "F");
		Add("view.images", "_Pictures on / off", () => _renderer.ShowImages = !_renderer.ShowImages, "G");
		Add("view.onion", "_Onion skin on / off", () => _painter.Onion.Enabled = !_painter.Onion.Enabled, "O");
		Add("view.grid", "_Grid on / off", () => ToggleSetting(s => s.ShowGrid = !s.ShowGrid), "Ctrl+OemQuotes");
		Add("view.rulers", "R_ulers on / off", () => ToggleSetting(s => s.ShowRulers = !s.ShowRulers), "Ctrl+R");
		Add("view.guides", "G_uides on / off", () => ToggleSetting(s => s.ShowGuides = !s.ShowGuides), "Ctrl+OemSemicolon");
		Add("view.bones", "_Bones on / off", () => ToggleSetting(s => s.ShowBones = !s.ShowBones));
	}

	private Control BuildMenu()
	{
		DefineCommands();
		MenuItem Item(string name)
		{
			var c = _commands[name];
			var item = new MenuItem { Header = c.Header, InputGesture = c.Key };
			item.Click += (_, _) => c.Run();
			return item;
		}
		MenuItem Top(string header, params string?[] names)
		{
			var top = new MenuItem { Header = header };
			foreach (string? name in names)
				top.Items.Add(name == null ? new Separator() : Item(name));
			return top;
		}
		return new Menu
		{
			Items =
			{
				Top("_File", "file.new", "file.open", null, "file.save", "file.saveAs", null, "file.import", null, "file.quit"),
				Top("_Edit", "edit.undo", "edit.redo", null, "edit.copy", "edit.paste", "edit.delete", null, "edit.back"),
				Top("_Playback", "play.toggle", "play.restart", "play.back", "play.on", "play.prevKey", "play.nextKey"),
				Top("_Animation", "anim.mode", "anim.key", null, "pose.mirror", "pose.copy", "pose.paste", "pose.pasteMirrored"),
				Top("_View", "tool.select", "tool.move", "tool.rotate", null, "view.reset", "view.images", "view.bones", "view.onion", null,
					"view.grid", "view.rulers", "view.guides")
			}
		};
	}

	private Control BuildToolBar()
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
		foreach (var (tool, label) in new[] { (ViewTool.Select, "Select"), (ViewTool.Move, "Move"), (ViewTool.Rotate, "Rotate") })
		{
			var button = new ToggleButton { Content = label };
			button.Click += (_, _) => SetTool(tool);
			_toolButtons[tool] = button;
			bar.Children.Add(button);
		}
		bar.Children.Add(new Border { Width = 12 });
		_animateButton = new ToggleButton { Content = "Animate" };
		_animateButton.Click += (_, _) => ToggleAnimate();
		bar.Children.Add(_animateButton);
		var key = new Button { Content = "Key" };
		key.Click += (_, _) => KeyThePose();
		bar.Children.Add(key);
		_playButton = new ToggleButton { Content = "Play" };
		_playButton.Click += (_, _) => State.Playing = !State.Playing;
		bar.Children.Add(_playButton);
		bar.Children.Add(new Border { Width = 12 });
		_sceneButton = new Button { Content = "◀ Scene" };
		_sceneButton.Click += (_, _) => LeaveObject();
		bar.Children.Add(_sceneButton);
		return bar;
	}

	// Every frame: the bar shows what's on
	private void SyncToolBar()
	{
		foreach (var (tool, button) in _toolButtons)
		{
			button.IsChecked = _tool.Tool == tool;
			button.IsEnabled = !SceneLevel;
		}
		_animateButton.IsChecked = State.AnimateMode;
		_animateButton.IsEnabled = !SceneLevel;
		_playButton.IsChecked = State.Playing;
		_sceneButton.IsEnabled = !SceneLevel;
	}

	// ======================= the keyboard =======================

	private void WireKeys()
	{
		KeyDown += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return;   // (typing in a field)
			if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
		};
		KeyUp += (_, e) => _mods = Input.ModsOf(e.KeyModifiers);
	}

	// The command a key (with these modifiers) runs. True: there was one.
	internal bool HandleKey(Key key, KeyModifiers modifiers)
	{
		if (HandlePaintKey(key, modifiers)) return true;   // (the mouse over the paint canvas: its keys first)
		var pressed = new KeyGesture(key, modifiers);
		foreach (var c in _commands.Values)
			if (c.Key is { } gesture && gesture.Key == pressed.Key && gesture.KeyModifiers == pressed.KeyModifiers)
			{
				c.Run();
				return true;
			}
		return false;
	}

	// ======================= what the commands do =======================

	private void SetTool(ViewTool tool)
	{
		if (!SceneLevel) _tool.Tool = tool;
	}

	private void ToggleAnimate()
	{
		if (SceneLevel) return;
		State.AnimateMode = !State.AnimateMode;
		RefreshPoses();   // (symmetrizing is for Build)
		Hint(State.AnimateMode ? "Animate: posing keys the pose at the playhead." : "Build: changes are to the object itself.");
	}

	internal void KeyThePose()
	{
		if (SceneLevel) return;
		if (!State.AnimateMode)
		{
			Hint("Keys are set in Animate (K).");
			return;
		}
		Hint(_timeline.Keys.KeyPose() == 0 ? "Nothing to key here: pose something, or pick bones and parts." : "");
	}

	private void StepFrames(int by)
	{
		State.Playing = false;
		if (SceneLevel) _session.StepScene(by);
		else State.SetFrame(State.Frame + by);
	}

	private void Restart()
	{
		if (SceneLevel) _session.SceneState.Time = 0;
		else State.SetFrame(0);
	}

	private void JumpToKey(int direction)
	{
		if (SceneLevel) return;
		State.Playing = false;
		_timeline.JumpToKey(direction);
	}

	private void DeleteKeys()
	{
		if (SceneLevel) return;
		if (!_timeline.DeleteSelected()) _timeline.Keys.DeletePickedKeysHere();
	}

	private void ToggleSetting(Action<AppSettings> change)
	{
		change(_settings);
		ApplySettings();
		try { _settings.Save(); }
		catch (Exception) { }   // (not being able to remember it isn't worth interrupting anyone for)
	}
}
