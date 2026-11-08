using Avalonia.Controls;

namespace Animator.Desktop;

// The rest of the shell, as the Windows editor has it (Recent, Places, Help):
// - File > Open recent (the last 10 projects; one that isn't there any more is taken off the
//   list) and File > Reopen (back to the last save, the changes since thrown away)
// - Places: where each edit was made (the scene, or an object with its animation, Build or
//   Animate, painting or not). Undo and redo first bring you back there, as a step of their own,
//   when you've moved on since: undoing while you work on "walk" can't quietly take back
//   something you did to "idle".
// - Edit > History (every step, to go back to any of them)
// - the status line (the mode, what's picked, the last edit) and Help: the keyboard and mouse
//   window (F1), the user manual, the engine format, About
public sealed partial class MainWindow
{
	private const string ManualUrl = "https://github.com/Max1potzac/harlequin_protoware/blob/master/docs/manual.md";
	private const string EngineFormatUrl = "https://github.com/Max1potzac/harlequin_protoware/blob/master/docs/engine-format.md";

	private string KeyOf(string id) => _keys.Gesture(id) is { Length: > 0 } key ? key : "no key";

	// ======================= recent projects =======================

	private readonly MenuItem _recentMenu = new() { Header = "Open _recent" };

	// (the tests answer Reopen's question)
	internal Func<Task<bool>>? AnswerReopen { get; set; }

	internal List<string> Recent => _settings.RecentFiles ??= new List<string>();

	// (after opening or saving a project)
	private void RememberRecent(string path)
	{
		RecentFiles.Add(Recent, path);
		SaveSettingsQuietly();
	}

	private void Forget(string path)
	{
		if (RecentFiles.Remove(Recent, path)) SaveSettingsQuietly();
	}

	// File > Open recent, under Open; filled in as the File menu opens
	private void AddRecentMenu(MenuItem file)
	{
		int at = file.Items.IndexOf(_menuItems["file.open"]) + 1;
		file.Items.Insert(at, _recentMenu);
		file.Items.Insert(at + 1, _menuItems["file.reopen"]);
		_recentMenu.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });   // (so it opens as a submenu)
		file.SubmenuOpened += (_, _) => FillRecentMenu();
	}

	internal void FillRecentMenu()
	{
		_menuItems["file.reopen"].IsEnabled = _session.ProjectPath is { } open && File.Exists(open);
		_recentMenu.Items.Clear();
		int n = 0;
		foreach (string path in Recent.ToList())
		{
			n++;
			var item = new MenuItem { Header = $"{(n < 10 ? $"_{n}" : "1_0")}  {Path.GetFileNameWithoutExtension(path)}" };
			ToolTip.SetTip(item, path);
			string chosen = path;
			item.Click += (_, _) => _ = OpenRecent(chosen);
			_recentMenu.Items.Add(item);
		}
		if (n == 0)
		{
			_recentMenu.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
			return;
		}
		_recentMenu.Items.Add(new Separator());
		var clear = new MenuItem { Header = "_Clear the list" };
		clear.Click += (_, _) =>
		{
			Recent.Clear();
			SaveSettingsQuietly();
		};
		_recentMenu.Items.Add(clear);
	}

	internal async Task OpenRecent(string path)
	{
		if (!File.Exists(path))
		{
			Forget(path);
			Hint($"{Path.GetFileName(path)} isn't there any more (moved, renamed or deleted): it's off the recent projects.");
			return;
		}
		if (!await ConfirmDiscard()) return;
		if (!await OpenFile(path)) Forget(path);
	}

	internal async Task Reopen()
	{
		if (_session.ProjectPath is not { } path) return;
		if (!File.Exists(path))
		{
			Hint($"{Path.GetFileName(path)} isn't there any more, so there's nothing to go back to.");
			return;
		}
		_session.FinishPaintTool();
		if (Unsaved)
		{
			string question = $"Go back to {_session.DocumentName} as it was last saved? The changes since then are thrown away (undo can't bring them back).";
			bool ok = AnswerReopen is { } answer ? await answer() : await Dialogs.Ask(this, "Reopen", question, "Reopen", "Cancel") == "Reopen";
			if (!ok) return;
		}
		await OpenFile(path);
	}

	// ======================= places =======================

	// The frame is where to look once back, not part of the place: scrubbing isn't moving away
	private sealed record EditPlace(SceneObject? Object, Animation? Animation, bool Animating, bool Painting, Slot? PaintSlot, int Frame)
	{
		public bool Equals(EditPlace? other) =>
			other is not null && Object == other.Object && Animation == other.Animation &&
			Animating == other.Animating && Painting == other.Painting && PaintSlot == other.PaintSlot;

		public override int GetHashCode() => HashCode.Combine(Object, Animation, Animating, Painting, PaintSlot);
	}

	private void InitPlaces()
	{
		State.Undo.Place = CurrentPlace;
		State.Undo.GoTo = place => { if (place is EditPlace where) GoToPlace(where); };
	}

	private object CurrentPlace() => SceneLevel
		? new EditPlace(null, null, false, false, null, 0)
		: new EditPlace(_session.Active, State.Animation, State.AnimateMode, _session.PaintOpen, _session.PaintOpen ? _session.PaintSlot : null, State.Frame);

	private void GoToPlace(EditPlace place)
	{
		if (place.Object == null)
		{
			LeaveObject();
			return;
		}
		if (!Scene.Objects.Contains(place.Object)) return;   // gone: the edit itself is undone
		if (SceneLevel || _session.Active != place.Object) EnterObject(place.Object);
		if (place.Animation != null && State.Animations.Contains(place.Animation) && State.Animation != place.Animation)
			SwitchAnimation(place.Animation);
		State.AnimateMode = place.Animating;
		State.Playing = false;
		State.SetFrame(place.Frame);
		if (place.Painting && place.PaintSlot is { } slot && State.Skeleton.Slots.Contains(slot))
		{
			if (!_session.PaintOpen || _session.PaintSlot != slot) OpenPaintEditorOn(slot);
		}
		else if (!place.Painting && _session.PaintOpen) ClosePaintEditor();
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		RefreshPoses();
	}

	// For the status line: where the next undo was made, when undo would take you back there first
	internal string UndoHint()
	{
		if (!State.Undo.UndoGoesBackFirst || State.Undo.NextUndoPlace is not EditPlace place) return "";
		string where = place.Object == null ? "the scene"
			: $"{(place.Object != _session.Active ? $"{place.Object.Name}, " : "")}{place.Animation?.Name}, {(place.Animating ? "Animate" : "Build")}{(place.Painting ? ", painting" : "")}";
		return $" (made in {where}: {KeyOf("edit.undo")} goes back there first)";
	}

	// ======================= history =======================

	private HistoryWindow? _historyWindow;
	internal HistoryWindow? History => _historyWindow;

	internal void ShowHistory()
	{
		if (_historyWindow != null)
		{
			if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
			_historyWindow.Activate();
			return;
		}
		_historyWindow = new HistoryWindow(State.Undo, count =>
		{
			State.SelectedKeys.Clear();   // (as undo does: keys picked may not be there after)
			State.Undo.MoveTo(count);
		});
		_historyWindow.Closed += (_, _) => _historyWindow = null;
		_historyWindow.Show(this);
	}

	// ======================= help =======================

	private HelpWindow? _helpWindow;
	internal HelpWindow? HelpShown => _helpWindow;

	internal void ShowHelp()
	{
		if (_helpWindow == null)
		{
			_helpWindow = new HelpWindow();
			_helpWindow.Closed += (_, _) => _helpWindow = null;
			_helpWindow.ShowText(HelpText());
			_helpWindow.Show(this);
		}
		else
		{
			if (_helpWindow.WindowState == WindowState.Minimized) _helpWindow.WindowState = WindowState.Normal;
			_helpWindow.Activate();
		}
	}

	private async Task OpenLink(string url)
	{
		bool opened = false;
		try { opened = await Launcher.LaunchUriAsync(new Uri(url)); }
		catch (Exception) { }
		if (!opened) await Dialogs.Tell(this, "Harlequin", $"Couldn't open {url}: open it in a browser.");
	}

	private Task About()
	{
		var version = typeof(MainWindow).Assembly.GetName().Version;
		return Dialogs.Tell(this, "About Harlequin",
			$"Harlequin {version?.ToString(3) ?? ""}\n\n2D skeletal and frame-by-frame animation for game assets.\n\n" +
			"Keys and mouse buttons: File > Settings; what they do: Help > Keyboard and mouse (F1).\nEverything else: Help > The user manual.");
	}

	private static string ButtonName(PointerButton button) => button switch
	{
		PointerButton.Left => "Left",
		PointerButton.Right => "Right",
		PointerButton.Middle => "Middle",
		PointerButton.XButton1 => "Back-button",
		_ => "Forward-button"
	};

	// The paint keys: "M select, W wand, …"
	private string PaintKeys() => string.Join(", ", KeyMap.Commands
		.Where(c => c.Scope == KeyScope.Paint && _keys.Gesture(c.Id).Length > 0)
		.Select(c => $"{_keys.Gesture(c.Id)} {c.Name.ToLowerInvariant()}"));

	// What the clicks, drags and keys do where you are (the help window's text): a line a topic,
	// three spaces between its points
	internal string HelpText()
	{
		string pick = ButtonName(_tool.SelectButton);
		string move = ButtonName(_tool.TransformButton);
		string pan = ButtonName(_tool.PanButton);
		string K(string id) => KeyOf(id);
		string OnOff(bool on) => on ? "on" : "off";
		string view = $"View: {K("view.rulers")} rulers, {K("view.grid")} grid, {K("view.guides")} guides (drag one out of a ruler, back onto it to take it away; {K("view.lockGuides")}: lock them).   " +
			$"Snap: {K("view.snapGrid")} to the grid ({OnOff(_settings.SnapToGrid)}), {K("view.snapGuides")} to guides ({OnOff(_settings.SnapToGuides)}).   {K("view.hitboxes")}: hitboxes and points";
		string numbers = $"Number fields: drag their name left or right to change them (Shift: faster, Ctrl: finer), click to type.   Keys ▾ (the animation bar) = what right-clicking the timeline does.   {K("edit.history")} = the history: every step, to go back to any of them";
		string file = $"File: {K("file.new")} new   {K("file.open")} open   {K("file.save")} save   {K("file.saveAs")} save as   {K("file.import")} import   {K("file.export")} export   {K("file.settings")} settings (keys, mouse, autosave)";

		if (SceneLevel)
			return
				$"Viewport: {pick}-click an object = pick, double-click (or {K("scene.open")}) = edit it.   {move}-drag an object = move, a corner = scale (Shift: keep proportions), the round handle = turn (Shift: 15° steps)\n" +
				$"{pan}-drag (or {move} on empty space) = move the view, wheel = zoom, {K("view.reset")} = reset the view\n" +
				$"{view}\n{numbers}\n" +
				"Objects (the Rig tab): + Object, Copy, Delete, ▲ / ▼ (in front / behind), Edit.   The properties: where it is, whether it shows, is locked, is exported, what it plays without clips, what carries it\n" +
				"Scene timeline: right-click a row = add a clip, drag a clip = move, drag its edges = length / trim, double-click = edit it in place.   Objects without clips loop their preview animation.   Wheel / Shift+wheel / Ctrl+wheel = scroll / scroll in time / zoom\n" +
				$"Keys: {K("play.toggle")} play / pause   {K("play.restart")} restart   {K("play.back")} / {K("play.on")} step   {K("edit.delete")} delete the clip (or object)   {K("edit.undo")} / {K("edit.redo")} undo / redo   {K("view.images")} pictures   {K("view.onion")} onion skin\n" +
				file;

		return
			$"Mode: {(!State.AnimateMode ? "Build: drags edit the rest pose" : State.AutoKey ? "Animate: drags set keys, or move the selected keys of what's dragged" : $"Animate, auto key off: drags pose without keys ({K("anim.key")} keys the pose, another frame drops it)")} ({K("anim.mode")} to switch, {K("anim.autoKey")}: auto key)\n" +
			$"Tools: {K("tool.select")} select, {K("tool.move")} move, {K("tool.rotate")} rotate (the tool bar).   " +
			$"Viewport: {pick}-click = pick (Ctrl: toggle, Shift: add), {pick}-drag = pick in a box (Shift: add, Ctrl: let go), double-click a part = paint it.   " +
			$"{move}-drag: a joint = move, a bone = turn (Shift: 15° steps), a part = move (its corners / round handle / pivot), anything picked = all that's picked; Ctrl+{move}-drag = new bone, Alt+{move}-click a part = put it on the picked bone, the ring at a bone's end = where it ends (Build).   " +
			$"{pan}-drag (or {move} on empty space) = move the view, wheel = zoom, {K("view.reset")} = reset the view\n" +
			$"{view}\n{numbers}\n" +
			$"Rig tab: Ctrl+click = pick several, Shift+click = every row from the picked one, {K("pick.all")} = everything shown, ● = hide / show ({K("pick.hide")}: hide the picked, {K("pick.hideOthers")}: hide the others, {K("pick.showAll")}: show all), " +
			$"padlock = lock / unlock: no clicking or dragging it in the viewport ({K("pick.lock")}: lock the picked, {K("pick.lockOthers")}: lock the others, {K("pick.unlockAll")}: unlock all)\n" +
			"Sets: + Set = a set of the picked; click a set = pick it again (Ctrl: picked or not, Shift: add), right-click = change it.   " +
			"Colour (beside Bones / Layers) = colour layers on the whole object (parts can be left out); in the paint editor, + ◐ = one for that drawing. Keyed in Animate; exports bake them in when asked.   " +
			"Boxes = hitboxes and points on bones, dragged in the viewport while the view is open\n" +
			$"Timeline: click a key = select, Shift+click = add, Ctrl+drag = a box of keys, Alt+click = every key at that frame, drag = move, right-click = curve, stretch, reverse, {K("keys.earlier")} / {K("keys.later")} = a frame earlier / later, {K("edit.copy")} / {K("edit.paste")} = copy / paste at the playhead, {K("pick.keys")} = every key of the picked, drag across empty frames = pick them (right-click: split into an animation sequence; play loops them)\n" +
			$"Poses (the Poses tab): keep a pose by name, put it on (partly, mirrored).   {K("pose.mirror")} = mirror the pose, {K("pose.copy")} / {K("pose.paste")} = copy / paste the pose, {K("pose.pasteMirrored")} = paste it mirrored (sides from names: arm_L / arm_R).   " +
			$"Curves ({K("anim.graph")}): the keys' numbers as curves; click / Shift+click a key, drag it (Shift: one way), drag its square handles to ease it, wheel = zoom, middle-drag = move, double-click = fit.   " +
			$"Frames: {K("time.insert")} = insert one after the playhead, {K("time.remove")} = remove the one at it, {K("play.prevKey")} / {K("play.nextKey")} = to the key before / after, {K("anim.key")} = key the pose (the unkeyed changes, or the picked)\n" +
			$"IK: {K("ik.snap")} = snap the target to the hand, {K("ik.bake")} = bake IK into FK, {K("ik.switch")} = switch IK / FK at this frame (Animate).   Detach / attach: Follows in the bone panel (Animate), Throw / fall under it.   " +
			$"Events: {K("anim.event")} = add at the playhead, right-click the timeline = add here, double-click a flag = edit.   Mixer… (the animation bar) = animations on tracks over each other, and fades between them\n" +
			$"Frame by frame (pick a part): {K("frames.new")} = new drawing, {K("frames.copy")} = copy the drawing, {K("frames.longer")} / {K("frames.shorter")} = hold longer / shorter, {K("edit.delete")} = its key here.   {K("view.onion")} = onion skin\n" +
			$"Paint: double-click a part (on another object: edit that object). Over the canvas: {PaintKeys()}; Shift+click / Shift+drag = line, Alt+click = pick a colour\n" +
			$"Keys: {K("play.toggle")} play / pause   {K("play.restart")} restart   {K("play.back")} / {K("play.on")} step   {K("edit.delete")} delete   {K("edit.undo")} / {K("edit.redo")} undo / redo   {K("edit.back")} back to the scene   " +
			$"{K("ik.toggle")} IK / FK   {K("ik.flip")} the elbow   {K("view.images")} pictures\n" +
			file;
	}

	// ======================= the status line =======================

	private string _statusText = "";
	internal string StatusText => _statusText;

	// Every frame: where you are, the mode, the frame, what's picked, the last edit (and where it
	// was made, when undo goes back there first); the help window follows along
	private void UpdateStatus()
	{
		string lastEdit = $"  ·  Last edit: {State.Undo.NextUndoName ?? "-"}{UndoHint()}";
		string text;
		if (SceneLevel)
			text = $"Scene  ·  frame {_sceneTimeline.Frame}{(_session.SceneState.Selected is { } o ? $"  ·  {o.Name}" : "")}{lastEdit}";
		else
		{
			string mode = !State.AnimateMode ? "Build" : State.AutoKey ? "● Animate" : "● Animate, auto key off";
			int count = State.Picked.Count;
			string shown = State.SelectedSlot?.Name ?? State.Selected?.Name ?? "";
			string picked = shown.Length == 0 ? "" : count > 1 ? $"  ·  {shown} + {count - 1} more" : $"  ·  {shown}";
			text = $"{_session.Active?.Name}: {State.Animation.Name}  ·  {mode}  ·  frame {State.Frame} / {State.FrameCount}{picked}{lastEdit}";
		}
		if (text != _statusText)
		{
			_statusText = text;
			_status.Text = text;
		}
		_helpWindow?.ShowText(HelpText());
	}
}
