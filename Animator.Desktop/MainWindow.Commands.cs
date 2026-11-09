using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Animator.Desktop;

// The menus and the keyboard: the keys are the key map's (the Windows editor's
// defaults, changed in Settings), for the commands this editor has so far
public sealed partial class MainWindow
{

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
		Add("file.reopen", "Re_open (back to the last save)", () => _ = Reopen());
		Add("file.save", "_Save", () => _ = SaveProject());
		Add("file.saveAs", "Save _as…", () => _ = SaveProjectAs());
		Add("file.import", "_Import…", () => _ = ImportFile());
		Add("file.export", "_Export…", () => _ = ExportProject());
		Add("file.settings", "Se_ttings…", () => _ = OpenSettings());
		Add("file.quit", "_Quit", Close);

		Add("edit.undo", "_Undo", () => State.Undo.Undo());
		Add("edit.redo", "_Redo", () => State.Undo.Redo());
		Add("edit.history", "_History…", ShowHistory);
		Add("help.keys", "_Keyboard and mouse", ShowHelp);
		Add("help.manual", "The user _manual", () => _ = OpenLink(ManualUrl));
		Add("help.format", "The _engine format", () => _ = OpenLink(EngineFormatUrl));
		Add("help.about", "_About Harlequin", () => _ = About());
		Add("edit.copy", "_Copy the selected keys", () => { if (!SceneLevel) _timeline.Copy(); });
		Add("edit.paste", "_Paste the keys at the playhead", () => { if (!SceneLevel) _timeline.Paste(); });
		Add("edit.delete", "_Delete the selected keys", DeleteKeys);
		Add("edit.back", "_Back to the scene", LeaveObject);
		Add("edit.bringIn", "Bring _in from another object…", () => _ = BringIn());
		Add("edit.batchBringIn", "Bring in from Animate _projects…", () => _ = BatchBringIn());
		Add("anim.mixer", "_Mixer…", OpenMixer);
		Add("anim.soundMap", "_Sound map…", OpenSoundMap);
		Add("anim.importEvents", "_Import events…", () => { if (SceneLevel) Hint("Open an object first: its animations take the events."); else _ = ImportEvents(); });
		Add("anim.exportEvents", "E_xport events…", () => { if (SceneLevel) Hint("Open an object first: its animations' events are written."); else _ = ExportEvents(); });
		Add("scene.open", "_Open the picked object", () => { if (SceneLevel && PickedObject is { } o) EnterObject(o); });

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
		DefineFrameCommands();
		DefineIkCommands();
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
		Add("tool.paint", "_Paint (one click opens a part)", () => SetTool(ViewTool.Paint));
		Add("view.pixels", "Pi_xel filtering on / off", () => _renderer.PixelArt = !_renderer.PixelArt);
		Add("view.snapGrid", "Snap to the gr_id", () => ToggleSetting(s => s.SnapToGrid = !s.SnapToGrid));
		Add("view.snapGuides", "Snap to the guid_es", () => ToggleSetting(s => s.SnapToGuides = !s.SnapToGuides));
		Add("view.lockGuides", "_Lock the guides", () => ToggleSetting(s => s.LockGuides = !s.LockGuides));
		Add("view.clearGuides", "_Clear the guides", () => _session.ClearGuides());

		Add("view.reset", "_Reset the view", ResetView);
		Add("view.images", "_Pictures on / off", () => _renderer.ShowImages = !_renderer.ShowImages);
		Add("view.onion", "_Onion skin on / off", () => _painter.Onion.Enabled = !_painter.Onion.Enabled);
		Add("view.grid", "_Grid on / off", () => ToggleSetting(s => s.ShowGrid = !s.ShowGrid));
		Add("view.rulers", "R_ulers on / off", () => ToggleSetting(s => s.ShowRulers = !s.ShowRulers));
		Add("view.guides", "G_uides on / off", () => ToggleSetting(s => s.ShowGuides = !s.ShowGuides));
		Add("view.bones", "_Bones on / off", () => ToggleSetting(s => s.ShowBones = !s.ShowBones));
		Add("view.hitboxes", "_Hitboxes and points on / off", ToggleHitboxes);
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
				Top("_Edit", "edit.undo", "edit.redo", "edit.history", null, "edit.copy", "edit.paste", "edit.delete", null, "edit.bringIn", "edit.batchBringIn", null, "edit.back"),
				Top("P_ick", "pick.all", "pick.keys", null, "pick.hide", "pick.hideOthers", "pick.showAll", null, "pick.lock", "pick.lockOthers", "pick.unlockAll"),
				Top("_IK", "ik.toggle", "ik.switch", "ik.snap", "ik.bake", "ik.flip"),
				Top("_Playback", "play.toggle", "play.restart", "play.back", "play.on", "play.prevKey", "play.nextKey"),
				Top("_Animation", "anim.mode", "anim.key", "anim.autoKey", "anim.event", "anim.graph", null, "keys.earlier", "keys.later", "keys.reverse", "time.insert", "time.remove", null, "frames.new", "frames.copy", "frames.longer", "frames.shorter", null, "order.forward", "order.backward", "order.front", "order.back", null, "pose.mirror", "pose.copy", "pose.paste", "pose.pasteMirrored", null, "anim.mixer", "anim.soundMap", "anim.importEvents", "anim.exportEvents"),
				Top("_View", "tool.select", "tool.move", "tool.rotate", "tool.paint", null, "view.reset", "view.images", "view.bones", "view.pixels", "view.onion", "view.hitboxes", null,
					"view.grid", "view.rulers", "view.guides", null, "view.snapGrid", "view.snapGuides", "view.lockGuides", "view.clearGuides"),
				Top("_Help", "help.keys", null, "help.manual", "help.format", null, "help.about")
			}
		};
		// File > Open recent and Reopen, under Open (Reopen was made for the keys, not listed yet)
		var file = (MenuItem)menu.Items[0]!;
		_menuItems["file.reopen"] = Item("file.reopen");
		AddRecentMenu(file);
		AddViewChecks((MenuItem)menu.Items.OfType<MenuItem>().First(m => m.Header as string == "_View"));
		ShowKeysInMenus();
		return menu;
	}

	// Each menu item shows its command's first key
	private void ShowKeysInMenus()
	{
		foreach (var (id, item) in _menuItems)
			item.InputGesture = _keys.KeysOf(id) is { Count: > 0 } keys ? new KeyGesture(keys[0].Key, keys[0].Modifiers) : null;
	}

	// ======================= the keyboard =======================

	// The keyboard, wherever the focus is:
	// - a shortcut goes to the editor first, unless a text field is being typed in or a menu or a
	//   dropdown's list is open: a dropdown, button or checkbox used a moment ago doesn't keep Space
	//   (play), the arrows (a frame) and the rest to itself. (Up and Down, which are no shortcut,
	//   still move in a list.)
	// - in a text field, Enter keeps what's typed and Esc puts back what was there; either way the
	//   keyboard goes back to the work. A click anywhere outside the field keeps what's typed too.
	// - a click on the viewport, the timeline or the paint canvas gives it the keyboard; so does
	//   picking from a dropdown
	private void WireKeys()
	{
		AddHandler(KeyDownEvent, (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			if (e.Handled) return;
			var target = e.Source as Visual;
			if (FieldOf(target) is { } field)
			{
				if (e.Key == Key.Escape)
				{
					if (_fieldWhenEntered.Field == field) field.Text = _fieldWhenEntered.Text;   // (as it was)
					LeaveField(field);
					e.Handled = true;
				}
				else if (e.Key == Key.Enter && !field.AcceptsReturn)
					Dispatcher.UIThread.Post(() => LeaveField(field));   // (after the field's own Enter, which keeps what's typed)
				return;   // (typing)
			}
			if (InMenuOrList(target) || target is ComboBox { IsDropDownOpen: true }) return;   // (choosing)
			if (e.Key == Key.Enter && target is Button) return;   // (a button's own Enter)
			if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
		}, RoutingStrategies.Tunnel);
		KeyUp += (_, e) => _mods = Input.ModsOf(e.KeyModifiers);

		// What a field held when it was entered (for Esc)
		AddHandler(GotFocusEvent, (_, e) =>
		{
			if (e.Source is TextBox field) _fieldWhenEntered = (field, field.Text);
		}, RoutingStrategies.Bubble, handledEventsToo: true);

		// A click (any button): on the viewport, the timeline or the paint canvas, the keyboard goes
		// there; anywhere else outside the field being typed in, it leaves the field (unless the click
		// gave it to something else, or opened a menu)
		AddHandler(PointerPressedEvent, (_, e) =>
		{
			if (e.Source is not Visual source) return;
			if (source.FindAncestorOfType<SkiaView>(includeSelf: true) is { } view)
			{
				if (!view.IsKeyboardFocusWithin) view.Focus(NavigationMethod.Pointer);
				return;
			}
			if (InMenuOrList(source)) return;
			if (FieldOf(FocusManager?.GetFocusedElement() as Visual) is { } field && FieldOf(source) != field)
				Dispatcher.UIThread.Post(() => LeaveField(field));
		}, RoutingStrategies.Tunnel, handledEventsToo: true);

		// Picked from a dropdown: the keyboard back to the work (not when it's set from code)
		AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
		{
			if (e.Source is not ComboBox combo || !(combo.IsDropDownOpen || combo.IsKeyboardFocusWithin)) return;
			Dispatcher.UIThread.Post(() =>
			{
				if (combo.IsDropDownOpen) return;
				var focused = FocusManager?.GetFocusedElement() as Visual;
				if (focused == null || focused == this || combo.IsKeyboardFocusWithin || InMenuOrList(focused)) BackToWork();   // (not if the click gave it to something else)
			});
		}, RoutingStrategies.Bubble, handledEventsToo: true);
	}

	private (TextBox? Field, string? Text) _fieldWhenEntered;

	// The text field this is (or is in), if any
	private static TextBox? FieldOf(Visual? visual) => visual?.FindAncestorOfType<TextBox>(includeSelf: true);

	// In a menu, or a dropdown's list (in their own little window, or over this one)
	private bool InMenuOrList(Visual? visual) => visual != null
		&& (TopLevel.GetTopLevel(visual) != this
			|| visual.FindAncestorOfType<Menu>(includeSelf: true) != null
			|| visual.FindAncestorOfType<MenuItem>(includeSelf: true) != null
			|| visual.FindAncestorOfType<ComboBoxItem>(includeSelf: true) != null);

	// Out of a field (if it's still in it): it keeps what's typed (its own LostFocus commits it), and
	// the keys go to the work
	private void LeaveField(TextBox field)
	{
		if (field.IsKeyboardFocusWithin) BackToWork();
	}

	// The keyboard to where the work is: the paint canvas while painting, else the viewport
	internal void BackToWork()
	{
		if (_session.PaintOpen && PaintView.IsEffectivelyVisible) PaintView.Focus();
		else Viewport.Focus();
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
		if (SceneLevel)
		{
			DeleteAtSceneLevel();   // the picked clip, or object
			return;
		}
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
