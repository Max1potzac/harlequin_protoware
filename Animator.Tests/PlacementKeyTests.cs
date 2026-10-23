using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Placement keys (milestone 9.3): moving, turning and scaling a part on its bone over time
public class PlacementKeyTests
{
	// A "hat" part on an "arm" bone; its drawing sits at (10, 5), turned and stretched
	private static (Skeleton Skeleton, Slot Slot, Attachment Drawing, Animation Animation) Rig()
	{
		var skeleton = new Skeleton();
		var arm = new Bone { Name = "arm", Length = 50 };
		skeleton.Roots.Add(arm);

		var slot = new Slot("hat", arm) { SetupAttachment = "hat" };
		skeleton.Slots.Add(slot);
		var asset = new Asset { Name = "hat", Width = 20, Height = 10 };
		skeleton.Assets[asset.Id] = asset;
		var drawing = new Attachment
		{
			Name = "hat", AssetId = asset.Id, Offset = new Vector2(10, 5), Rotation = 0.25f, Scale = new Vector2(2, 1)
		};
		skeleton.DefaultSkin.Set("hat", drawing);

		return (skeleton, slot, drawing, new Animation { Name = "bob", Duration = 1f });
	}

	private static void AssertClose(Vector2 expected, Vector2 actual) =>
		Assert.True(Vector2.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

	[Fact]
	public void Placement_keys_add_to_the_drawings_own_placement()
	{
		var (skeleton, slot, drawing, animation) = Rig();
		var move = new PartTranslateTimeline("hat");
		move.Track.Add(0f, Vector2.Zero);
		move.Track.Add(1f, new Vector2(8, -4));
		var turn = new PartRotateTimeline("hat");
		turn.Track.Add(0.5f, 0.5f);
		var scale = new PartScaleTimeline("hat");
		scale.Track.Add(0.5f, new Vector2(1.5f, 1.5f));
		animation.Timelines.AddRange(new Timeline[] { move, turn, scale });

		IkMatching.Evaluate(skeleton, animation, 0.5f);

		Assert.Equal(new Vector2(4, -2), slot.Offset);
		Assert.Equal(0.5f, slot.Rotation);
		Assert.Equal(new Vector2(1.5f, 1.5f), slot.Scale);
		var placed = slot.ImageToBone(drawing, 20, 10);
		Assert.Equal(drawing.LocalMatrix(20, 10, new Vector2(4, -2), 0.5f, new Vector2(1.5f, 1.5f)), placed);

		// The pivot pixel lands where the drawing puts it, moved by exactly the key
		var pivot = Vector2.Transform(new Vector2(drawing.Pivot.X * 20, drawing.Pivot.Y * 10), placed);
		AssertClose(drawing.Offset + new Vector2(4, -2), pivot);
	}

	[Fact]
	public void Without_keys_a_part_rests_where_its_drawing_is_placed()
	{
		var (skeleton, slot, drawing, animation) = Rig();
		slot.Offset = new Vector2(99, 99);   // left over from another frame

		IkMatching.Evaluate(skeleton, animation, 0.3f);

		Assert.Equal(Vector2.Zero, slot.Offset);
		Assert.Equal(0f, slot.Rotation);
		Assert.Equal(Vector2.One, slot.Scale);
		Assert.Equal(drawing.LocalMatrix(20, 10), slot.ImageToBone(drawing, 20, 10));
	}

	[Fact]
	public void Placement_keys_follow_a_renamed_part_and_go_with_a_deleted_one()
	{
		var (skeleton, slot, _, animation) = Rig();
		animation.Timelines.Add(new PartTranslateTimeline("hat"));
		animation.Timelines.Add(new PartRotateTimeline("hat"));
		animation.Timelines.Add(new PartScaleTimeline("hat"));

		Assert.True(PartOps.RenamePart(skeleton, new[] { animation }, slot, "cap"));
		Assert.All(animation.Timelines, t => Assert.Equal("cap", t.Target));

		PartOps.DeletePart(skeleton, new[] { animation }, slot);
		Assert.Empty(animation.Timelines);
	}

	[Fact]
	public void Deleting_the_bone_removes_its_parts_placement_keys()
	{
		var (skeleton, slot, _, animation) = Rig();
		animation.Timelines.Add(new PartScaleTimeline("hat"));

		RigOps.DeleteBone(skeleton, new[] { animation }, slot.Bone);

		Assert.Empty(animation.Timelines);
	}

	[Fact]
	public void Placement_keys_are_saved_and_loaded()
	{
		var (skeleton, _, _, animation) = Rig();
		var move = new PartTranslateTimeline("hat");
		move.Track.Add(0.5f, new Vector2(3, 4), CurveType.Smooth);
		var turn = new PartRotateTimeline("hat");
		turn.Track.Add(0.25f, 1.5f);
		var scale = new PartScaleTimeline("hat");
		scale.Track.Add(0f, new Vector2(2, 0.5f), CurveType.Stepped);
		animation.Timelines.AddRange(new Timeline[] { move, turn, scale });

		string json = JsonSerializer.Serialize(ProjectMapper.ToDto(skeleton, new[] { animation }, 30), ProjectMapper.Json);
		var (_, loaded) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);

		var timelines = loaded[0].Timelines;
		var moveBack = Assert.IsType<PartTranslateTimeline>(timelines[0]);
		Assert.Equal(new Vector2(3, 4), moveBack.Track.Keys[0].Value);
		Assert.Equal(CurveType.Smooth, moveBack.Track.Keys[0].Curve);
		Assert.Equal(1.5f, Assert.IsType<PartRotateTimeline>(timelines[1]).Track.Keys[0].Value);
		var scaleBack = Assert.IsType<PartScaleTimeline>(timelines[2]);
		Assert.Equal(new Vector2(2, 0.5f), scaleBack.Track.Keys[0].Value);
		Assert.Equal(CurveType.Stepped, scaleBack.Track.Keys[0].Curve);
	}
}
