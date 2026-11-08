using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Animator.Desktop;

// The menus, the tool bar and the keyboard: the keys are the key map's (the Windows editor's
// defaults, changed in Settings), for the commands this editor has so far
public sealed partial class MainWindow
{
	private readonly Dictionary<ViewTool, ToggleButton> _toolButtons = new();
	private ToggleButton _animateButton = null!;
	private ToggleButton _playButton = null!;
	private Button _sceneButton = null!;

	// One command (by its key map id): its menu text, and what it does
	private sealed record Command(string Name, string Header, Action Run);

	private readonly Dictionary<string, Command> _commands = new();
	private readonly Dictionary<string, MenuItem> _menuItems = new();
	private KeyMap _keys = new();

	private void Add(string name, string header, Action run) => _commands[name] = new Command(name, header, run);

	// The commands a key can be given here (Settings lists these)
	internal bool HasCommand(string id) => _commands.ContainsKey(id) || PaintCommands.Contains(id);

	internal KeyMap Keys => _keys;

	private void DefineCommands()
	{
		Add("file.new", "_New project", () => _ = NewProject());
		Add("file.open", "_Open…", () => _ = OpenProject());
		Add("file.save", "_Save", () => _ = SaveProject());
		Add("file.saveAs", "Save _as…", () => _ = SaveProjectAs());
		Add("file.import", "_Import…", () => _ = ImportFile());
		Add("file.export", "_Export…", () => _ = ExportProject());
		Add("file.settings", "Se_ttings…", () => _ = OpenSettings());
		Add("file.quit", "_Quit", Close);

		Add("edit.undo", "_Undo", () => State.Undo.Undo());
		Add("edit.redo", "_Redo", () => State.Undo.Redo());
		Add("edit.copy", "_Copy the selected keys", () => { if (!SceneLevel) _timeline.Copy(); });
		Add("edit.paste", "_Paste the keys at the playhead", () => { if (!SceneLevel) _timeline.Paste(); });
		Add("edit.delete", "_Delete the selected keys", DeleteKeys);
		Add("edit.back", "_Back to the scene", LeaveObject);

		Add("play.toggle", "_Play / pause", TogglePlaying);
		Add("play.restart", "Back to the _first frame", Restart);
		Add("play.back", "A frame _back", () => StepFrames(-1));
		Add("play.on", "A frame _on", () => StepFrames(+1));
		Add("play.prevKey", "To the key _before", () => JumpToKey(-1));
		Add("play.nextKey", "To the key _after", () => JumpToKey(+1));

		Add("anim.mode", "_Build / Animate", ToggleAnimate);
		Add("anim.key", "_Key the pose here", KeyThePose);
		DefinePickCommands();
		DefineTimelineCommands();
		Add("order.forward", "Picked parts a step to the _front", () => ReorderPicked(+1, toEnd: false));
		Add("order.backward", "Picked parts a step to the _back", () => ReorderPicked(-1, toEnd: false));
		Add("order.front", "Picked parts in front of everything", () => ReorderPicked(+1, toEnd: true));
		Add("order.back", "Picked parts behind everything", () => ReorderPicked(-1, toEnd: true));
		Add("pose.mirror", "_Mirror the pose", MirrorThePose);
		Add("pose.copy", "_Copy the pose", CopyThePose);
		Add("pose.paste", "_Paste the pose", () => PasteThePose(false));
		Add("pose.pasteMirrored", "Paste the pose mi_rrored", () => PasteThePose(true));

		Add("tool.select", "_Select", () => SetTool(ViewTool.Select));
		Add("tool.move", "_Move", () => SetTool(ViewTool.Move));
		Add("tool.rotate", "_Rotate", () => SetTool(ViewTool.Rotate));

		Add("view.reset", "_Reset the view", ResetView);
		Add("view.images", "_Pictures on / off", () => _renderer.ShowImages = !_renderer.ShowImages);
		Add("view.onion", "_Onion skin on / off", () => _painter.Onion.Enabled = !_painter.Onion.Enabled);
		Add("view.grid", "_Grid on / off", () => ToggleSetting(s => s.ShowGrid = !s.ShowGrid));
		Add("view.rulers", "R_ulers on / off", () => ToggleSetting(s => s.ShowRulers = !s.ShowRulers));
		Add("view.guides", "G_uides on / off", () => ToggleSetting(s => s.ShowGuides = !s.ShowGuides));
		Add("view.bones", "_Bones on / off", () => ToggleSetting(s => s.ShowBones = !s.ShowBones));
	}

	private Control BuildMenu()
	{
		DefineCommands();
		MenuItem Item(string name)
		{
			var c = _commands[name];
			var item = new MenuItem { Header = c.Header };
			item.Click += (_, _) => c.Run();
			_menuItems[name] = item;
			return item;
		}
		MenuItem Top(string header, params string?[] names)
		{
			var top = new MenuItem { Header = header };
			foreach (string? name in names)
				top.Items.Add(name == null ? new Separator() : Item(name));
			return top;
		}
		var menu = new Menu
		{
			Items =
			{
				Top("_File", "file.new", "file.open", null, "file.save", "file.saveAs", null, "file.import", "file.export", null, "file.settings", null, "file.quit"),
				Top("_Edit", "edit.undo", "edit.redo", null, "edit.copy", "edit.paste", "edit.delete", null, "edit.back"),
				Top("P_ick", "pick.all", "pick.keys", null, "pick.hide", "pick.hideOthers", "pick.showAll", null, "pick.lock", "pick.lockOthers", "pick.unlockAll"),
				Top("_Playback", "play.toggle", "play.restart", "play.back", "play.on", "play.prevKey", "play.nextKey"),
				Top("_Animation", "anim.mode", "anim.key", "anim.autoKey", "anim.event", null, "keys.earlier", "keys.later", "keys.reverse", "time.insert", "time.remove", null, "order.forward", "order.backward", "order.front", "order.back", null, "pose.mirror", "pose.copy", "pose.paste", "pose.pasteMirrored"),
				Top("_View", "tool.select", "tool.move", "tool.rotate", null, "view.reset", "view.images", "view.bones", "view.onion", null,
					"view.grid", "view.rulers", "view.guides")
			}
		};
		ShowKeysInMenus();
		return menu;
	}

	// Each menu item shows its command's first key
	private void ShowKeysInMenus()
	{
		foreach (var (id, item) in _menuItems)
			item.InputGesture = _keys.KeysOf(id) is { Count: > 0 } keys ? new KeyGesture(keys[0].Key, keys[0].Modifiers) : null;
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
		if (KeyChord.From(key, modifiers) is not { } chord) return false;
		if (HandlePaintKey(chord)) return true;   // (the mouse over the paint canvas: its keys first)
		if (_keys.CommandFor(chord, KeyScope.Main) is not { } id || !_commands.TryGetValue(id, out var command)) return false;
		command.Run();
		return true;
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
