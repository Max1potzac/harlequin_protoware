using System.Windows.Input;

namespace Animator.Viewer;

// A key and the modifiers held with it: Ctrl+Shift+S, F5, Space
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
	// The way menus show it: "Ctrl+Shift+]"
	public override string ToString() => Join(KeyName(Key));

	// The way settings.json keeps it: the key's own name ("Ctrl+Shift+OemCloseBrackets")
	public string Code => Join(Key.ToString());

	private string Join(string key)
	{
		var parts = new List<string>();
		if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
		if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
		if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
		if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
		parts.Add(key);
		return string.Join("+", parts);
	}

	// Reads either form ("Ctrl+Shift+OemCloseBrackets", "Ctrl+Shift+]", "F5")
	public static bool TryParse(string? text, out KeyChord chord)
	{
		chord = default;
		if (string.IsNullOrWhiteSpace(text)) return false;
		string t = text.Trim();
		string name, held;
		if (t == "+")
		{
			name = "+";
			held = "";
		}
		else if (t.EndsWith("++", StringComparison.Ordinal))   // a plus key: "Ctrl++"
		{
			name = "+";
			held = t[..^2];
		}
		else
		{
			int plus = t.LastIndexOf('+');
			name = t[(plus + 1)..].Trim();
			held = plus < 0 ? "" : t[..plus];
			if (name.Length == 0) return false;   // "Ctrl+": no key
		}

		var modifiers = ModifierKeys.None;
		if (held.Length > 0)
		{
			foreach (var part in held.Split('+'))
			{
				switch (part.Trim().ToLowerInvariant())
				{
					case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
					case "shift": modifiers |= ModifierKeys.Shift; break;
					case "alt": modifiers |= ModifierKeys.Alt; break;
					case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
					default: return false;
				}
			}
		}
		if (KeyFromName(name) is not { } key || IsModifierKey(key) || key == Key.None) return false;
		chord = new KeyChord(key, modifiers);
		return true;
	}

	// The key pressed, with the modifiers held now (null: a modifier on its own, or nothing to bind)
	public static KeyChord? From(KeyEventArgs e) => From(e, Keyboard.Modifiers);

	public static KeyChord? From(KeyEventArgs e, ModifierKeys modifiers)
	{
		var key = e.Key switch
		{
			Key.System => e.SystemKey,                // with Alt (and F10)
			Key.ImeProcessed => e.ImeProcessedKey,
			Key.DeadCharProcessed => e.DeadCharProcessedKey,
			_ => e.Key
		};
		if (key == Key.None || IsModifierKey(key)) return null;
		return new KeyChord(key, modifiers);
	}

	public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
		or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

	// What a key is called on the keys themselves (US layout); otherwise its own name
	public static string KeyName(Key key) => key switch
	{
		Key.OemOpenBrackets => "[",
		Key.OemCloseBrackets => "]",
		Key.OemComma => ",",
		Key.OemPeriod => ".",
		Key.OemMinus => "-",
		Key.OemPlus => "=",
		Key.OemQuestion => "/",
		Key.OemSemicolon => ";",
		Key.OemQuotes => "'",
		Key.OemTilde => "`",
		Key.OemPipe => "\\",
		Key.OemBackslash => "\\",
		>= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
		>= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
		Key.Add => "Num Plus",
		Key.Subtract => "Num Minus",
		Key.Multiply => "Num *",
		Key.Divide => "Num /",
		Key.Decimal => "Num .",
		Key.Enter => "Enter",
		Key.Back => "Backspace",
		Key.Escape => "Esc",
		Key.Delete => "Del",
		Key.Insert => "Ins",
		Key.Space => "Space",
		Key.Left => "Left",
		Key.Right => "Right",
		Key.Up => "Up",
		Key.Down => "Down",
		Key.PageUp => "PageUp",
		Key.PageDown => "PageDown",
		Key.Tab => "Tab",
		_ => key.ToString()
	};

	private static readonly Dictionary<string, Key> Named = BuildNames();

	private static Dictionary<string, Key> BuildNames()
	{
		var names = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
		foreach (var key in Enum.GetValues<Key>())
			names.TryAdd(KeyName(key), key);
		names["Return"] = Key.Enter;
		names["Escape"] = Key.Escape;
		names["Delete"] = Key.Delete;
		names["+"] = Key.OemPlus;
		return names;
	}

	private static Key? KeyFromName(string name) =>
		Enum.TryParse<Key>(name, ignoreCase: true, out var key) && !int.TryParse(name, out _) ? key :
		Named.TryGetValue(name, out var named) ? named : null;
}

// Where a key works: paint keys while the mouse is over the paint canvas (they go first there),
// the rest everywhere else
public enum KeyScope { Main, Paint }

// Something a key can do
public sealed record KeyCommand(string Id, string Group, string Name, KeyScope Scope, KeyChord[] Defaults);

// Which keys do what. The defaults, changed in Settings; settings.json keeps only the changes.
public sealed class KeyMap
{
	private static KeyCommand C(string id, string group, string name, params KeyChord[] keys) =>
		new(id, group, name, KeyScope.Main, keys);

	private static KeyCommand P(string id, string group, string name, params KeyChord[] keys) =>
		new(id, group, name, KeyScope.Paint, keys);

	private static KeyChord K(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

	private const ModifierKeys Ctrl = ModifierKeys.Control;
	private const ModifierKeys Shift = ModifierKeys.Shift;
	private const ModifierKeys Alt = ModifierKeys.Alt;
	private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

	public static IReadOnlyList<KeyCommand> Commands { get; } = new[]
	{
		C("file.new", "File", "New project…", K(Key.N, Ctrl)),
		C("file.open", "File", "Open…", K(Key.O, Ctrl)),
		C("file.save", "File", "Save", K(Key.S, Ctrl)),
		C("file.saveAs", "File", "Save as…", K(Key.S, CtrlShift)),
		C("file.import", "File", "Import…", K(Key.I, Ctrl)),
		C("file.export", "File", "Export…", K(Key.E, Ctrl)),
		C("file.settings", "File", "Settings…", K(Key.OemComma, Ctrl)),

		C("edit.undo", "Edit", "Undo", K(Key.Z, Ctrl)),
		C("edit.redo", "Edit", "Redo", K(Key.Y, Ctrl), K(Key.Z, CtrlShift)),
		C("edit.copy", "Edit", "Copy the selected keys", K(Key.C, Ctrl)),
		C("edit.paste", "Edit", "Paste the keys at the playhead", K(Key.V, Ctrl)),
		C("edit.delete", "Edit", "Delete: the selected keys (or the picked one's keys here); a clip or an object (scene)", K(Key.Delete)),
		C("edit.back", "Edit", "Back: close the paint editor, let go of picked frames, leave the object", K(Key.Escape)),

		C("pick.all", "Pick", "Pick every bone and part shown (and not locked)", K(Key.A, Ctrl)),
		C("pick.keys", "Pick", "Select every key of the picked bones and parts", K(Key.A, CtrlShift)),
		C("pick.hide", "Pick", "Hide the picked bones and parts", K(Key.H)),
		C("pick.hideOthers", "Pick", "Hide everything but the picked", K(Key.H, Shift)),
		C("pick.showAll", "Pick", "Show everything", K(Key.H, Alt)),
		C("pick.lock", "Pick", "Lock the picked (they can't be picked or dragged in the viewport)", K(Key.L)),
		C("pick.lockOthers", "Pick", "Lock everything but the picked", K(Key.L, Shift)),
		C("pick.unlockAll", "Pick", "Unlock everything", K(Key.L, Alt)),

		C("play.toggle", "Playback", "Play / pause", K(Key.Space)),
		C("play.restart", "Playback", "Back to the first frame", K(Key.R)),
		C("play.back", "Playback", "A frame back", K(Key.Left)),
		C("play.on", "Playback", "A frame on", K(Key.Right)),
		C("play.prevKey", "Playback", "To the key before the playhead", K(Key.Left, Shift)),
		C("play.nextKey", "Playback", "To the key after the playhead", K(Key.Right, Shift)),

		C("anim.mode", "Animation", "Setup / Animate mode", K(Key.K)),
		C("anim.event", "Animation", "Add an event at the playhead", K(Key.E)),
		C("anim.key", "Animation", "Key the pose here: the unkeyed changes (auto key off), or else the picked", K(Key.S)),
		C("anim.autoKey", "Animation", "Auto key on / off", K(Key.K, Shift)),
		C("anim.graph", "Animation", "Curves (the graph editor) / keys (the dopesheet)", K(Key.G, Ctrl)),
		C("keys.earlier", "Keys", "The selected keys a frame earlier", K(Key.Left, Alt)),
		C("keys.later", "Keys", "The selected keys a frame later", K(Key.Right, Alt)),
		C("keys.reverse", "Keys", "Reverse the selected keys"),
		C("time.insert", "Keys", "Insert a frame after the playhead (the keys after it move on)", K(Key.OemPlus, Alt)),
		C("time.remove", "Keys", "Remove the frame at the playhead (the keys after it move up)", K(Key.OemMinus, Alt)),
		C("order.forward", "Animation", "Picked parts a step to the front (Animate mode: a key)", K(Key.OemCloseBrackets, Ctrl)),
		C("order.backward", "Animation", "Picked parts a step to the back", K(Key.OemOpenBrackets, Ctrl)),
		C("order.front", "Animation", "Picked parts in front of everything", K(Key.OemCloseBrackets, CtrlShift)),
		C("order.back", "Animation", "Picked parts behind everything", K(Key.OemOpenBrackets, CtrlShift)),

		C("ik.toggle", "IK", "IK / FK", K(Key.I)),
		C("ik.switch", "IK", "Switch IK / FK at this frame (Animate mode)", K(Key.I, Shift)),
		C("ik.snap", "IK", "Snap the IK target to the hand", K(Key.T)),
		C("ik.bake", "IK", "Bake IK into FK", K(Key.T, Shift)),
		C("ik.flip", "IK", "Flip the elbow", K(Key.B)),

		C("frames.new", "Frame by frame", "New drawing", K(Key.F7)),
		C("frames.copy", "Frame by frame", "Copy of the drawing", K(Key.F6)),
		C("frames.longer", "Frame by frame", "Drawing a frame longer", K(Key.F5)),
		C("frames.shorter", "Frame by frame", "Drawing a frame shorter", K(Key.F5, Shift)),

		C("view.images", "View", "Pictures on / off", K(Key.G)),
		C("view.pixels", "View", "Pixel filtering on / off", K(Key.P)),
		C("view.reset", "View", "Reset the view", K(Key.F)),
		C("view.onion", "View", "Onion skin on / off", K(Key.O)),
		C("view.rulers", "View", "Rulers on / off", K(Key.R, Ctrl)),
		C("view.grid", "View", "Grid on / off", K(Key.OemQuotes, Ctrl)),
		C("view.guides", "View", "Guides on / off", K(Key.OemSemicolon, Ctrl)),
		C("view.snapGrid", "View", "Snap to the grid", K(Key.OemQuotes, CtrlShift)),
		C("view.snapGuides", "View", "Snap to the guides", K(Key.OemSemicolon, CtrlShift)),
		C("view.lockGuides", "View", "Lock the guides", K(Key.OemSemicolon, Ctrl | Alt)),

		C("scene.open", "Scene", "Open the selected object", K(Key.Enter)),

		C("tool.select", "Viewport tools", "Select: pick (a box over several); the transform button moves things", K(Key.D1), K(Key.Q)),
		C("tool.move", "Viewport tools", "Move: drag a bone or part to move it (a box elsewhere)", K(Key.D2), K(Key.W)),
		C("tool.rotate", "Viewport tools", "Rotate: drag a bone or part to turn it", K(Key.D3)),
		C("tool.paint", "Viewport tools", "Paint: one click on a part opens it to paint", K(Key.D4)),

		P("paint.select", "Paint tools", "Select", K(Key.M)),
		P("paint.wand", "Paint tools", "Wand", K(Key.W)),
		P("paint.move", "Paint tools", "Move", K(Key.V)),
		P("paint.pencil", "Paint tools", "Pencil", K(Key.N)),
		P("paint.brush", "Paint tools", "Brush", K(Key.B)),
		P("paint.mixer", "Paint tools", "Mixer", K(Key.X)),
		P("paint.eraser", "Paint tools", "Eraser", K(Key.E)),
		P("paint.picker", "Paint tools", "Picker", K(Key.I)),
		P("paint.fill", "Paint tools", "Fill", K(Key.G)),
		P("paint.gradient", "Paint tools", "Gradient", K(Key.D)),
		P("paint.figure", "Paint tools", "Figure", K(Key.U)),

		P("paint.swap", "Paint", "Swap the colours", K(Key.S)),
		P("paint.smaller", "Paint", "Smaller brush", K(Key.OemOpenBrackets)),
		P("paint.bigger", "Paint", "Bigger brush", K(Key.OemCloseBrackets)),
		P("paint.selectAll", "Paint", "Select every pixel", K(Key.A, Ctrl)),
		P("paint.deselect", "Paint", "Deselect", K(Key.D, Ctrl)),
		P("paint.invert", "Paint", "Invert the selection", K(Key.I, CtrlShift)),
		P("paint.delete", "Paint", "Delete the selected pixels", K(Key.Delete)),
		P("paint.finish", "Paint", "Finish the figure or selection shape", K(Key.Enter)),
		P("paint.undoPoint", "Paint", "Take back the figure's last point", K(Key.Back)),
		P("paint.cancel", "Paint", "Cancel the figure or shape, or drop the selection", K(Key.Escape)),
	};

	private static readonly Dictionary<string, KeyCommand> ById = Commands.ToDictionary(c => c.Id);

	private readonly Dictionary<string, List<KeyChord>> _keys = new();

	public KeyMap()
	{
		foreach (var command in Commands) _keys[command.Id] = command.Defaults.ToList();
	}

	public static KeyCommand? Find(string id) => ById.GetValueOrDefault(id);

	public IReadOnlyList<KeyChord> KeysOf(string id) =>
		_keys.TryGetValue(id, out var keys) ? keys : Array.Empty<KeyChord>();

	// The command a key does there (null: none)
	public string? CommandFor(KeyChord chord, KeyScope scope)
	{
		foreach (var command in Commands)
			if (command.Scope == scope && _keys[command.Id].Contains(chord)) return command.Id;
		return null;
	}

	// The command (of the same scope, other than `id`) that already uses this key
	public string? UsedBy(KeyChord chord, string id)
	{
		if (Find(id) is not { } command) return null;
		var other = CommandFor(chord, command.Scope);
		return other == id ? null : other;
	}

	// A command's first key, as menus show it ("" when it has none)
	public string Gesture(string id) => KeysOf(id) is { Count: > 0 } keys ? keys[0].ToString() : "";

	// All of a command's keys, for lists ("Ctrl+Y, Ctrl+Shift+Z")
	public string Describe(string id) => string.Join(", ", KeysOf(id));

	// A command's keys become these. Another command (of the same scope) that had one of them
	// loses it: those are returned.
	public List<string> Set(string id, IEnumerable<KeyChord> keys)
	{
		var taken = new List<string>();
		if (Find(id) is not { } command) return taken;
		var mine = keys.Distinct().ToList();
		foreach (var other in Commands)
		{
			if (other.Id == id || other.Scope != command.Scope) continue;
			if (_keys[other.Id].RemoveAll(mine.Contains) > 0) taken.Add(other.Id);
		}
		_keys[id] = mine;
		return taken;
	}

	public List<string> Reset(string id) => Find(id) is { } command ? Set(id, command.Defaults) : new List<string>();

	public void ResetAll()
	{
		foreach (var command in Commands) _keys[command.Id] = command.Defaults.ToList();
	}

	public bool IsDefault(string id) => Find(id) is { } command && KeysOf(id).SequenceEqual(command.Defaults);

	// What differs from the defaults (for settings.json): command id → its keys
	public Dictionary<string, string[]> Changes() => Commands
		.Where(c => !IsDefault(c.Id))
		.ToDictionary(c => c.Id, c => KeysOf(c.Id).Select(k => k.Code).ToArray());

	// The defaults, with these changes (unknown commands and unreadable keys are skipped)
	public static KeyMap From(IReadOnlyDictionary<string, string[]>? changes)
	{
		var map = new KeyMap();
		if (changes == null) return map;
		foreach (var command in Commands)
		{
			if (!changes.TryGetValue(command.Id, out var codes) || codes == null) continue;
			var keys = new List<KeyChord>();
			foreach (var code in codes)
				if (KeyChord.TryParse(code, out var chord)) keys.Add(chord);
			map.Set(command.Id, keys);
		}
		return map;
	}

	public KeyMap Copy() => From(Changes());
}
