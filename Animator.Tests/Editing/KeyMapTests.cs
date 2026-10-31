using System.Text.Json;
using System.Windows.Input;
using Animator.Viewer;

namespace Animator.Tests;

// Milestone 14: which key does what, changed in Settings and remembered in settings.json
public class KeyMapTests
{
	[Fact]
	public void The_defaults_give_each_key_one_job_where_it_works()
	{
		Assert.Equal(KeyMap.Commands.Count, KeyMap.Commands.Select(c => c.Id).Distinct().Count());
		foreach (var scope in new[] { KeyScope.Main, KeyScope.Paint })
		{
			var used = new Dictionary<KeyChord, string>();
			foreach (var command in KeyMap.Commands.Where(c => c.Scope == scope))
				foreach (var key in command.Defaults)
					Assert.True(used.TryAdd(key, command.Id), $"{key} is {used.GetValueOrDefault(key)} and {command.Id}");
		}
	}

	[Fact]
	public void Keys_read_back_from_either_way_of_writing_them()
	{
		foreach (var key in KeyMap.Commands.SelectMany(c => c.Defaults))
		{
			Assert.True(KeyChord.TryParse(key.Code, out var fromCode), key.Code);
			Assert.Equal(key, fromCode);
			Assert.True(KeyChord.TryParse(key.ToString(), out var fromName), key.ToString());
			Assert.Equal(key, fromName);
		}

		Assert.Equal("Ctrl+Shift+]", new KeyChord(Key.OemCloseBrackets, ModifierKeys.Control | ModifierKeys.Shift).ToString());
		Assert.Equal("Ctrl+,", new KeyChord(Key.OemComma, ModifierKeys.Control).ToString());
		Assert.Equal("Alt+H", new KeyChord(Key.H, ModifierKeys.Alt).ToString());
		Assert.True(KeyChord.TryParse("ctrl+shift+s", out var save));
		Assert.Equal(new KeyChord(Key.S, ModifierKeys.Control | ModifierKeys.Shift), save);
		Assert.True(KeyChord.TryParse("Ctrl++", out var plus));
		Assert.Equal(new KeyChord(Key.OemPlus, ModifierKeys.Control), plus);
		Assert.True(KeyChord.TryParse("Shift+1", out var one));
		Assert.Equal(new KeyChord(Key.D1, ModifierKeys.Shift), one);
		Assert.True(KeyChord.TryParse("Num Plus", out var numPlus));
		Assert.Equal(new KeyChord(Key.Add), numPlus);

		Assert.False(KeyChord.TryParse("Ctrl+", out _));       // no key
		Assert.False(KeyChord.TryParse("Hyper+A", out _));     // no such modifier
		Assert.False(KeyChord.TryParse("Ctrl+Shift", out _));  // modifiers on their own
		Assert.False(KeyChord.TryParse("", out _));
	}

	[Fact]
	public void Giving_a_key_to_another_command_takes_it_from_the_one_that_had_it()
	{
		var keys = new KeyMap();
		var undo = new KeyChord(Key.Z, ModifierKeys.Control);
		Assert.Equal("edit.undo", keys.CommandFor(undo, KeyScope.Main));
		Assert.Equal("edit.undo", keys.UsedBy(undo, "anim.mode"));
		Assert.Null(keys.UsedBy(undo, "edit.undo"));

		var taken = keys.Set("anim.mode", new[] { undo });
		Assert.Equal(new[] { "edit.undo" }, taken);
		Assert.Equal("anim.mode", keys.CommandFor(undo, KeyScope.Main));
		Assert.Empty(keys.KeysOf("edit.undo"));
		Assert.Equal("", keys.Gesture("edit.undo"));
		Assert.Null(keys.CommandFor(new KeyChord(Key.K), KeyScope.Main));   // its old key does nothing now

		keys.Reset("edit.undo");   // the default comes back, from the one that took it
		Assert.Equal("edit.undo", keys.CommandFor(undo, KeyScope.Main));
		Assert.Empty(keys.KeysOf("anim.mode"));

		// Paint keys are on their own: over the canvas B is the brush, everywhere else the elbow
		Assert.Equal("paint.brush", keys.CommandFor(new KeyChord(Key.B), KeyScope.Paint));
		Assert.Equal("ik.flip", keys.CommandFor(new KeyChord(Key.B), KeyScope.Main));
		keys.Set("paint.fill", new[] { new KeyChord(Key.B) });
		Assert.Equal("paint.fill", keys.CommandFor(new KeyChord(Key.B), KeyScope.Paint));
		Assert.Equal("ik.flip", keys.CommandFor(new KeyChord(Key.B), KeyScope.Main));
		Assert.Empty(keys.KeysOf("paint.brush"));
	}

	[Fact]
	public void Only_the_changes_are_saved_and_they_come_back()
	{
		var keys = new KeyMap();
		Assert.Empty(keys.Changes());
		keys.Set("view.onion", new[] { new KeyChord(Key.F8), new KeyChord(Key.O, ModifierKeys.Alt) });
		keys.Set("ik.flip", Array.Empty<KeyChord>());

		var changes = keys.Changes();
		Assert.Equal(new[] { "ik.flip", "view.onion" }, changes.Keys.OrderBy(k => k, StringComparer.Ordinal));
		Assert.Equal(new[] { "F8", "Alt+O" }, changes["view.onion"]);
		Assert.Empty(changes["ik.flip"]);

		string json = JsonSerializer.Serialize(changes);
		var back = KeyMap.From(JsonSerializer.Deserialize<Dictionary<string, string[]>>(json));
		Assert.Equal(new[] { new KeyChord(Key.F8), new KeyChord(Key.O, ModifierKeys.Alt) }, back.KeysOf("view.onion"));
		Assert.Empty(back.KeysOf("ik.flip"));
		Assert.Equal(keys.KeysOf("file.save"), back.KeysOf("file.save"));   // the rest: the defaults
		var again = back.Changes();
		Assert.Equal(changes.Keys.OrderBy(k => k, StringComparer.Ordinal), again.Keys.OrderBy(k => k, StringComparer.Ordinal));
		Assert.Equal(changes["view.onion"], again["view.onion"]);

		// A file from another version: unknown commands and keys are left out
		var odd = KeyMap.From(new Dictionary<string, string[]>
		{
			["no.such.command"] = new[] { "F9" },
			["view.reset"] = new[] { "Hyper+Q", "F9" }
		});
		Assert.Equal(new[] { new KeyChord(Key.F9) }, odd.KeysOf("view.reset"));
		Assert.Single(odd.Changes());
	}
}
