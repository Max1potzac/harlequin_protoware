using System.Numerics;
using Animator.Core.Import;
using Animator.Core.Rigging;
using Animator.Core.Scenes;
using Animator.Viewer.Files;
using Animator.Viewer.Import;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The editor window, shown on the headless platform: what it shows (saved as pictures for a
// person to look at), and that its mouse, keys and files reach the engine
public class EditorWindowTests
{
	[AvaloniaFact]
	public void A_new_character_shows_its_bone_and_the_timeline()
	{
		var window = Windows.Show();
		Assert.Equal("Untitled — Harlequin", window.Title);
		Assert.False(window.Session.SceneLevel);
		Assert.True(Windows.Shot(window, "new-character") > 8);

		// The list has the bone; picking it there picks it for the tools
		window.RigList.SelectedIndex = 0;
		Assert.Equal("root", window.State.Selected?.Name);
	}

	[AvaloniaFact]
	public void A_project_opens_saves_and_opens_again()
	{
		var window = Windows.Show();
		string path = Windows.LayersProject();
		Assert.True(window.TryOpen(path, out string? problem), problem);
		Assert.Equal("layers — Harlequin", window.Title);
		Assert.NotEmpty(window.Session.Scene.Objects);
		Assert.True(Windows.Shot(window, "layers-project") > 8);

		// Changed, saved (the title says so), and read back
		Assert.True(window.State.EditStructure(() =>
		{
			window.State.Skeleton.Roots[0].SetupRotation += 0.5f;
			return "Turn";
		}));
		Assert.EndsWith("* — Harlequin", window.Title);
		Assert.True(window.TrySave(path, out problem), problem);
		Assert.Equal("layers — Harlequin", window.Title);
		Assert.True(window.TryOpen(path, out problem), problem);
		Assert.False(window.TryOpen(path + ".missing", out problem));
		Assert.NotNull(problem);
	}

	[AvaloniaFact]
	public void The_keys_reach_the_editor()
	{
		var window = Windows.Show();
		Assert.False(window.State.AnimateMode);
		Assert.True(window.HandleKey(Key.K, KeyModifiers.None));
		Assert.True(window.State.AnimateMode);

		// A frame on, and back to the first
		Assert.True(window.HandleKey(Key.Right, KeyModifiers.None));
		Assert.Equal(1, window.State.Frame);
		Assert.True(window.HandleKey(Key.R, KeyModifiers.None));
		Assert.Equal(0, window.State.Frame);
		Assert.False(window.HandleKey(Key.F12, KeyModifiers.None));

		// Space plays, and again stops
		Assert.True(window.HandleKey(Key.Space, KeyModifiers.None));
		Assert.True(window.State.Playing);
		window.HandleKey(Key.Space, KeyModifiers.None);
		Assert.False(window.State.Playing);

		// An edit, undone with Ctrl+Z
		float was = window.State.Skeleton.Roots[0].Length;
		window.State.EditStructure(() =>
		{
			window.State.Skeleton.Roots[0].Length = was * 2;
			return "Longer";
		});
		Assert.True(window.HandleKey(Key.Z, KeyModifiers.Control));
		Assert.Equal(was, window.State.Skeleton.Roots[0].Length);
	}

	[AvaloniaFact]
	public void A_click_on_the_timelines_ruler_moves_the_playhead()
	{
		var window = Windows.Show();
		window.State.AnimateMode = true;   // (Build puts the timeline away)
		Windows.Settle(window);
		var view = window.TimelineView;
		Assert.True(view.Bounds.Width > 200);

		// Through the headless platform's mouse, at the window's own coordinates
		var at = view.TranslatePoint(new Point(view.Bounds.Width * 0.6, 8), window)!.Value;
		window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
		window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
		Assert.True(window.State.Frame > 0);
		Windows.Shot(window, "timeline-clicked");
	}

	[AvaloniaFact]
	public void Out_to_the_scene_and_back_into_the_object()
	{
		var window = Windows.Show();
		var character = window.Session.Active!;
		window.LeaveObject();
		Assert.True(window.Session.SceneLevel);
		Assert.Single(window.RigList.Items);
		Windows.Shot(window, "scene-level");

		window.EnterObject(character);
		Assert.False(window.Session.SceneLevel);
		Assert.Same(character, window.Session.Active);
	}

	[AvaloniaFact]
	public void A_bone_is_dragged_round_in_the_viewport()
	{
		var window = Windows.Show();
		var bone = window.State.Skeleton.Roots[0];
		window.State.Selected = bone;
		Windows.Settle(window);

		// Rotate tool: a drag from the bone's middle turns it, one undo step
		window.HandleKey(Key.D3, KeyModifiers.None);
		var middle = window.Camera.WorldToScreen(Vector2.Transform(new Vector2(bone.Length / 2, 0), bone.World));
		float was = bone.SetupRotation;
		window.ViewportDown(middle, PointerButton.Left, 1);
		window.ViewportMove(middle + new Vector2(40, 0));
		window.ViewportMove(middle + new Vector2(60, 10));
		window.ViewportUp(middle + new Vector2(60, 10));
		Assert.NotEqual(was, bone.SetupRotation);
		Assert.True(window.State.Undo.IsDirty);
		Windows.Shot(window, "bone-turned");
	}
}
