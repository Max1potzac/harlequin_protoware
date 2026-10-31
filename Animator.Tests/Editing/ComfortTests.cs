using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Animator.Core.Serialization;
using Animator.Viewer;
using Animator.Viewer.Files;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using SkiaSharp;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// Step 15: locking bones and parts, bone colours and selection sets, the grid, guides and
// snapping, and keeping work safe (autosaved copies, backups). The rig is the picking tests' one:
//   root: joint (0, 0), pointing right, 40 long        body (on the root): 20×20 around (0, -100)
//   upper: joint (0, 100), 40 long                     hand (on the forearm): 20×20 around (60, 50)
//   lower: joint (60, 100), 40 long
public class ComfortTests
{
	private static string[] Names(IEnumerable<object> items) =>
		items.Select(i => i switch { Bone b => b.Name, Slot s => s.Name, _ => "?" }).OrderBy(n => n, StringComparer.Ordinal).ToArray();

	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	private static string TempFolder()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"harlequin-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		return folder;
	}

	private static void DeleteFolder(string folder)
	{
		try { Directory.Delete(folder, recursive: true); }
		catch (Exception) { }   // (in the temp folder anyway)
	}

	// ======================= locks =======================

	[Fact]
	public void Locked_bones_and_parts_cant_be_clicked_boxed_or_dragged()
	{
		var rig = new Rig();
		rig.Renderer.SkipLocked = true;   // as inside an object
		rig.Lower.Locked = true;
		rig.Hand.Locked = true;

		rig.Click(60, 100);   // the forearm's joint
		Assert.Empty(rig.State.Picked);
		rig.Click(60, 50);    // the hand's pixels
		Assert.Empty(rig.State.Picked);
		rig.Drag(-200, 200, 200, -200, MouseButton.Left);   // a box over everything
		Assert.Equal(new[] { "body", "root", "upper" }, Names(rig.State.Picked));

		rig.State.ClearPicks();
		var hand = rig.CentreOf(rig.Hand);
		rig.Drag(60, 50, 90, 50, MouseButton.Right);     // the hand: nothing to take hold of, so the view moves instead
		Assert.NotEqual(Vector2.Zero, rig.Camera.Pan);
		rig.Camera.Pan = Vector2.Zero;                   // (back where the screen points are)
		rig.Drag(60, 100, 90, 100, MouseButton.Right);   // the forearm's joint: the same
		Assert.NotEqual(Vector2.Zero, rig.Camera.Pan);
		rig.Camera.Pan = Vector2.Zero;
		rig.Pose();
		AssertNear(hand, rig.CentreOf(rig.Hand));
		Assert.Equal(new Vector2(60, 0), rig.Lower.SetupPosition);
	}

	[Fact]
	public void A_click_on_a_locked_part_goes_through_to_the_one_under_it()
	{
		var rig = new Rig();
		rig.Renderer.SkipLocked = true;
		var shield = rig.AddPart("shield", rig.Root, new Vector2(0, -100));   // in front of the body
		rig.Pose();

		rig.Click(0, -100);
		Assert.Equal(new object[] { shield }, rig.State.Picked);
		shield.Locked = true;
		rig.Click(0, -100);
		Assert.Equal(new object[] { rig.Body }, rig.State.Picked);
	}

	[Fact]
	public void Dragging_the_picked_leaves_the_locked_ones_where_they_are()
	{
		var rig = new Rig();
		rig.Body.Locked = true;   // the rig panel can still pick it
		rig.State.Pick(new object[] { rig.Upper, rig.Body });

		rig.Drag(0, 100, 0, 120, MouseButton.Right);   // by the upper arm's joint
		rig.Pose();

		AssertNear(new Vector2(0, 120), rig.Upper.World.Translation);
		AssertNear(new Vector2(0, -100), rig.CentreOf(rig.Body));
	}

	[Fact]
	public void Locking_the_others_locks_everything_else_and_undo_brings_it_back()
	{
		var rig = new Rig();
		Assert.True(LockOps.LockOthers(rig.Skeleton, new object[] { rig.Hand }));
		Assert.False(rig.Hand.Locked);
		Assert.True(rig.Root.Locked && rig.Upper.Locked && rig.Lower.Locked && rig.Body.Locked);   // its bone too: locks aren't inherited
		Assert.False(LockOps.LockOthers(rig.Skeleton, new object[] { rig.Hand }));   // nothing more to do

		var before = EditAidsSnapshot.Capture(rig.Skeleton);
		Assert.True(LockOps.UnlockAll(rig.Skeleton));
		Assert.False(rig.Root.Locked || rig.Body.Locked);
		Assert.False(LockOps.UnlockAll(rig.Skeleton));
		before.Restore();
		Assert.True(rig.Root.Locked && rig.Body.Locked);
		Assert.False(rig.Hand.Locked);
	}

	// ======================= bone colours and sets =======================

	[Fact]
	public void A_bone_without_a_colour_of_its_own_is_the_usual_yellow()
	{
		var bone = new Bone { Name = "arm" };
		Assert.Equal(SkeletonRenderer.DefaultBoneColor, SkeletonRenderer.BoneColor(bone));
		bone.EditorColor = new Vector3(1f, 0f, 0.2f);
		Assert.Equal(new SKColor(255, 0, 51), SkeletonRenderer.BoneColor(bone));

		var before = EditAidsSnapshot.Capture(new Skeleton { Roots = { bone } });
		bone.EditorColor = null;
		before.Restore();
		Assert.Equal(new Vector3(1f, 0f, 0.2f), bone.EditorColor);
	}

	[Fact]
	public void A_selection_set_leaves_out_what_was_deleted_and_undo_puts_it_back_as_it_was()
	{
		var rig = new Rig();
		var arm = new SelectionSet { Name = "arm" };
		arm.Bones.AddRange(new[] { rig.Upper, rig.Lower });
		arm.Slots.Add(rig.Hand);
		rig.Skeleton.Sets.Add(arm);
		Assert.Equal(new[] { "hand", "lower", "upper" }, Names(arm.MembersIn(rig.Skeleton)));

		rig.Skeleton.Slots.Remove(rig.Hand);   // the hand deleted
		Assert.Equal(new[] { "lower", "upper" }, Names(arm.MembersIn(rig.Skeleton)));
		rig.Skeleton.Slots.Add(rig.Hand);      // (and back)

		var before = SetsSnapshot.Capture(rig.Skeleton);
		arm.Name = "renamed";
		arm.Bones.Remove(rig.Lower);
		rig.Skeleton.Sets.Add(new SelectionSet { Name = "another" });
		before.Restore();
		Assert.Same(arm, Assert.Single(rig.Skeleton.Sets));
		Assert.Equal("arm", arm.Name);
		Assert.Equal(new[] { rig.Upper, rig.Lower }, arm.Bones);
	}

	// ======================= the file =======================

	private static Scene RoundTrip(Scene scene) =>
		ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(
			JsonSerializer.Serialize(ProjectMapper.SceneToDto(scene), ProjectMapper.Json), ProjectMapper.Json)!);

	[Fact]
	public void Locks_colours_sets_the_grid_and_guides_are_saved_and_loaded()
	{
		var rig = new Rig();
		rig.Upper.Locked = true;
		rig.Hand.Locked = true;
		rig.Lower.EditorColor = new Vector3(0.25f, 0.5f, 1f);
		var arm = new SelectionSet { Name = "arm" };
		arm.Bones.AddRange(new[] { rig.Upper, rig.Lower });
		arm.Slots.Add(rig.Hand);
		var other = new SelectionSet { Name = "other" };
		other.Bones.Add(rig.Root);
		other.Slots.Add(new Slot("spare", rig.Root));   // never in the rig (or deleted since): left out
		rig.Skeleton.Sets.AddRange(new[] { arm, other });

		var scene = new Scene { Fps = 30, GridSize = 16 };
		scene.Guides.Add(new Guide(vertical: true, 12.5f));
		scene.Guides.Add(new Guide(vertical: false, -40f));
		scene.Objects.Add(new SceneObject("Character", ObjectKind.Rigged, rig.Skeleton));

		var loaded = RoundTrip(scene);

		Assert.Equal(16f, loaded.GridSize);
		Assert.Collection(loaded.Guides,
			g => { Assert.True(g.Vertical); Assert.Equal(12.5f, g.At); },
			g => { Assert.False(g.Vertical); Assert.Equal(-40f, g.At); });
		var skeleton = loaded.Objects[0].Skeleton;
		Assert.True(skeleton.FindBone("upper")!.Locked);
		Assert.False(skeleton.FindBone("lower")!.Locked);
		Assert.True(skeleton.FindSlot("hand")!.Locked);
		Assert.False(skeleton.FindSlot("body")!.Locked);
		Assert.Equal(new Vector3(0.25f, 0.5f, 1f), skeleton.FindBone("lower")!.EditorColor);
		Assert.Null(skeleton.FindBone("upper")!.EditorColor);

		Assert.Equal(new[] { "arm", "other" }, skeleton.Sets.Select(s => s.Name));
		Assert.Equal(new[] { "upper", "lower" }, skeleton.Sets[0].Bones.Select(b => b.Name));
		Assert.Equal(new[] { "hand" }, skeleton.Sets[0].Slots.Select(s => s.Name));
		Assert.Equal(new[] { "root" }, skeleton.Sets[1].Bones.Select(b => b.Name));
		Assert.Empty(skeleton.Sets[1].Slots);
	}

	[Fact]
	public void A_project_that_uses_none_of_them_has_nothing_about_them_in_its_file()
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root" });
		skeleton.Slots.Add(new Slot("body", skeleton.Roots[0]));
		var scene = new Scene { Fps = 30 };
		scene.Objects.Add(new SceneObject("Character", ObjectKind.Rigged, skeleton));

		var dto = ProjectMapper.SceneToDto(scene);
		Assert.Null(dto.GridSize);
		Assert.Null(dto.Guides);
		var rig = dto.Objects!.Single();
		Assert.Null(rig.Sets);
		Assert.Null(rig.Bones.Single().Locked);
		Assert.Null(rig.Bones.Single().EditorColor);
		Assert.Null(rig.Slots.Single().Locked);

		var loaded = RoundTrip(scene);
		Assert.Equal(10f, loaded.GridSize);
		Assert.Empty(loaded.Guides);
		Assert.Empty(loaded.Objects[0].Skeleton.Sets);
	}

	// ======================= snapping =======================

	[Fact]
	public void A_point_snaps_to_the_nearest_grid_line_or_guide_within_reach_each_way()
	{
		var snap = new Snapping { ToGrid = true, GridSize = 10, Guides = new[] { new Guide(vertical: true, 33f) } };
		AssertNear(new Vector2(10, 30), snap.Snap(new Vector2(12, 27), zoom: 1));     // a grid line each way
		AssertNear(new Vector2(33, 30), snap.Snap(new Vector2(32, 27), zoom: 1));     // the guide's nearer than the grid
		AssertNear(new Vector2(13, 0), snap.Snap(new Vector2(13, 1), zoom: 4));       // zoomed in: 3 away is 12 px, too far
		AssertNear(new Vector2(10, 30), snap.Snap(new Vector2(14, 27), zoom: 0.5f));  // zoomed out: 4 away is 2 px

		snap.ToGrid = false;
		AssertNear(new Vector2(12, 27), snap.Snap(new Vector2(12, 27), zoom: 1));     // just the guide, and it's far
		AssertNear(new Vector2(33, 27), snap.Snap(new Vector2(30, 27), zoom: 1));     // (it only catches x)
		snap.ToGuides = false;
		Assert.False(snap.On);
		AssertNear(new Vector2(30, 27), snap.Snap(new Vector2(30, 27), zoom: 1));
	}

	[Fact]
	public void Of_several_points_the_one_nearest_a_line_decides_how_far_they_all_go()
	{
		var snap = new Snapping { ToGrid = true, GridSize = 10 };
		// x: 12 is 2 from 10, 25 is 5 from 20; y: 3 is 3 from 0, 18 is 2 from 20
		AssertNear(new Vector2(-2, 2), snap.Offset(new[] { new Vector2(12, 3), new Vector2(25, 18) }, zoom: 1));
		Assert.Equal(Vector2.Zero, snap.Offset(Array.Empty<Vector2>(), zoom: 1));
	}

	[Fact]
	public void Shift_steps_are_15_degrees()
	{
		Assert.Equal(MathF.PI / 12, Snapping.StepAngle(0.3f), 5);                  // 17° → 15°
		Assert.Equal(MathF.PI / 6, Snapping.StepAngle(MathF.PI / 6 + 0.1f), 5);   // 36° → 30°
		Assert.Equal(-MathF.PI / 4, Snapping.StepAngle(-0.8f), 5);                // -46° → -45°
	}

	[Fact]
	public void A_dragged_joint_lands_on_the_grid()
	{
		var rig = new Rig();
		rig.Tool.Snap = new Snapping { ToGrid = true, GridSize = 10 };
		rig.Drag(60, 100, 73, 104, MouseButton.Right);   // the forearm's joint, to near (70, 100)
		AssertNear(new Vector2(70, 0), rig.Lower.SetupPosition);   // (on the upper arm, from its joint at (0, 100))
	}

	[Fact]
	public void A_dragged_part_lands_with_its_pivot_or_a_corner_on_the_grid()
	{
		var rig = new Rig();
		rig.Tool.Snap = new Snapping { ToGrid = true, GridSize = 10 };
		rig.Drag(0, -100, 8, -93, MouseButton.Right);   // the body: its middle (the pivot) to near (10, -90)
		AssertNear(new Vector2(10, -90), rig.CentreOf(rig.Body));
	}

	[Fact]
	public void Dragging_several_snaps_what_was_taken_hold_of_and_the_rest_go_alike()
	{
		var rig = new Rig();
		rig.Tool.Snap = new Snapping { ToGrid = true, GridSize = 10 };
		rig.State.Pick(new object[] { rig.Upper, rig.Body });

		rig.Drag(0, 100, 4, 117, MouseButton.Right);   // by the upper arm's joint, to near (0, 120)
		rig.Pose();

		AssertNear(new Vector2(0, 120), rig.Upper.World.Translation);
		AssertNear(new Vector2(0, -80), rig.CentreOf(rig.Body));
	}

	[Fact]
	public void Shift_turns_a_bone_in_15_degree_steps()
	{
		var rig = new Rig { Held = ModifierKeys.Shift };
		rig.Tool.MouseDown(Rig.Screen(30, 100), MouseButton.Right);   // on the upper arm: it turns round its joint (0, 100)
		foreach (var degrees in new[] { 10, 25, 37 })
		{
			float a = degrees * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(30 * MathF.Cos(a), 100 + 30 * MathF.Sin(a)));
			rig.Pose();
		}
		rig.Tool.MouseUp();
		Assert.Equal(MathF.PI / 6, rig.Upper.SetupRotation, 4);   // 37° → 30°
	}

	[Fact]
	public void A_dragged_object_lands_with_its_origin_or_a_corner_of_its_box_on_the_grid()
	{
		var rig = new Rig();
		var scene = new Scene { Fps = 30 };
		var character = new SceneObject("Character", ObjectKind.Rigged, rig.Skeleton);
		scene.Objects.Add(character);
		var snap = new Snapping { ToGrid = true, GridSize = 10 };
		var tool = new ObjectTool(() => scene, new SceneState(), rig.Camera, rig.Renderer, rig.State.Undo)
		{
			Modifiers = () => ModifierKeys.None,
			Snap = snap
		};

		// What it should come to: moved (13, 4), then as far again as puts the nearest of its box's
		// corners and its origin on a line
		var handles = ObjectGizmo.For(scene, character, rig.Renderer, rig.Camera.Zoom);
		var moved = new Vector2(13, 4);
		var expected = moved + snap.Offset(handles.Corners.Append(handles.Origin).Select(p => p + moved).ToList(), rig.Camera.Zoom);
		Assert.NotEqual(moved, expected);

		tool.MouseDown(Rig.Screen(0, -100), MouseButton.Left, 1);   // its body: selected
		tool.MouseUp();
		tool.MouseDown(Rig.Screen(0, -100), MouseButton.Right, 1);
		tool.MouseMove(Rig.Screen(6, -98));
		tool.MouseMove(Rig.Screen(13, -96));
		tool.MouseUp();

		AssertNear(expected, character.Position);
	}

	// ======================= autosave and backups =======================

	[Fact]
	public void Copies_left_by_a_run_that_is_over_are_offered_and_this_runs_own_are_not()
	{
		string folder = TempFolder();
		string was = Recovery.Folder;
		Recovery.Folder = folder;
		try
		{
			string mine = Recovery.NewFile("hero");
			File.WriteAllText(mine, "a copy");
			Recovery.WriteNote(mine, Recovery.NoteForNow(@"C:\art\hero.hqnap", "hero"));

			string old = Recovery.NewFile("old: one?");
			Assert.DoesNotContain(":", Path.GetFileName(old));
			Assert.DoesNotContain("?", Path.GetFileName(old));
			File.WriteAllText(old, "an older copy");
			var note = Recovery.NoteForNow(null, "old: one?");
			note.ProcessStartUtc = note.ProcessStartUtc.AddDays(-1);   // a run long over that had this process's id
			Recovery.WriteNote(old, note);

			string lost = Recovery.NewFile("lost");
			Recovery.WriteNote(lost, Recovery.NoteForNow(null, "lost"));   // a note whose copy is gone

			var left = Assert.Single(Recovery.Leftovers());
			Assert.Equal(old, left.File);
			Assert.Equal("old: one?", left.Note.Name);
			Assert.Null(left.Note.Project);
			Assert.False(File.Exists(lost + ".json"));   // cleared away

			Recovery.Remove(old);
			Assert.False(File.Exists(old));
			Assert.False(File.Exists(old + ".json"));
			Assert.Empty(Recovery.Leftovers());
			Assert.True(File.Exists(mine));
		}
		finally
		{
			Recovery.Folder = was;
			DeleteFolder(folder);
		}
	}

	[Fact]
	public void A_kept_copy_is_the_project_as_it_was_when_it_was_taken()
	{
		string folder = TempFolder();
		string was = Recovery.Folder;
		Recovery.Folder = Path.Combine(folder, "Recovery");   // not there yet
		try
		{
			var store = new AssetStore();
			var skeleton = new Skeleton();
			skeleton.Roots.Add(new Bone { Name = "root" });
			var asset = new Asset { Name = "spot", Width = 4, Height = 4 };
			using (var pixels = new SKBitmap(AssetStore.InfoFor(4, 4)))
			{
				pixels.Erase(SKColors.Red);
				store.AddLayer(asset, "Base", pixels);
			}
			skeleton.Assets[asset.Id] = asset;
			var scene = new Scene { Fps = 30 };
			scene.Objects.Add(new SceneObject("Character", ObjectKind.Rigged, skeleton));

			using var snapshot = ProjectFile.Take(scene, store, thumbnail: null, copyPixels: true);
			store.GetLayer(asset.Layers[0])!.Erase(SKColors.Blue);   // painted on and changed meanwhile
			skeleton.Roots[0].Name = "renamed";
			string file = Recovery.NewFile("Untitled");
			Recovery.Keep(file, snapshot, Recovery.NoteForNow(null, "Untitled"));

			var loaded = ProjectFile.Load(file);
			var loadedSkeleton = loaded.Scene.Objects.Single().Skeleton;
			Assert.NotNull(loadedSkeleton.FindBone("root"));
			var layer = loadedSkeleton.Assets.Values.Single().Layers.Single();
			Assert.Equal(SKColors.Red, loaded.Store.GetLayer(layer)!.GetPixel(1, 1));
			Assert.Empty(Recovery.Leftovers());   // this run's own: not offered
		}
		finally
		{
			Recovery.Folder = was;
			DeleteFolder(folder);
		}
	}

	[Fact]
	public void Saving_over_a_project_keeps_the_newest_versions_it_replaced()
	{
		string folder = TempFolder();
		try
		{
			string project = Path.Combine(folder, "hero.hqnap");
			ProjectFile.KeepBackup(project, 2);   // nothing there yet: nothing to keep
			Assert.Empty(ProjectFile.Backups(project));

			// Another project whose name starts the same way: its backups aren't these
			string backups = Path.Combine(folder, "Backups");
			Directory.CreateDirectory(backups);
			string another = Path.Combine(backups, "hero 2 2026-01-01 09.00.00.hqnap");
			File.WriteAllText(another, "another project");

			for (int day = 1; day <= 3; day++)
			{
				File.WriteAllText(project, $"version {day}");
				File.SetLastWriteTime(project, new DateTime(2026, 1, day, 10, 0, 0));
				ProjectFile.KeepBackup(project, 2);
			}

			var kept = ProjectFile.Backups(project);
			Assert.Equal(new[] { "hero 2026-01-03 10.00.00.hqnap", "hero 2026-01-02 10.00.00.hqnap" }, kept.Select(f => f.Name));
			Assert.Equal("version 3", File.ReadAllText(kept[0].FullName));
			Assert.Equal("version 2", File.ReadAllText(kept[1].FullName));
			Assert.True(File.Exists(another));

			ProjectFile.KeepBackup(project, 0);   // backups off: none made, none thrown away
			Assert.Equal(2, ProjectFile.Backups(project).Count);
		}
		finally
		{
			DeleteFolder(folder);
		}
	}
}
