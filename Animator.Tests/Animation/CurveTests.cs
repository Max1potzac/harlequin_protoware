using System.Text.Json;
using System.Windows.Input;
using Animator.Core.Serialization;
using Animator.Viewer;
using Animator.Viewer.Panels;
using SkiaSharp;

namespace Animator.Tests;

// Step 17: Bézier curves between keys, and the graph editor that shapes them
public class CurveTests
{
	private const int Fps = 30;

	[Fact]
	public void A_Bezier_goes_from_0_to_1_and_its_handles_shape_the_way()
	{
		foreach (var curve in new[] { Bezier.EaseIn, Bezier.EaseOut, Bezier.EaseInOut, Bezier.Overshoot, Bezier.Anticipate, Bezier.Straight })
		{
			Assert.Equal(0f, curve.Ease(0f), 4);
			Assert.Equal(1f, curve.Ease(1f), 4);
		}
		Assert.Equal(0.3f, Bezier.Straight.Ease(0.3f), 3);   // straight handles: as linear
		Assert.True(Bezier.EaseIn.Ease(0.5f) < 0.4f);
		Assert.True(Bezier.EaseOut.Ease(0.5f) > 0.6f);
		Assert.True(Enumerable.Range(1, 99).Max(i => Bezier.Overshoot.Ease(i / 100f)) > 1.05f);       // past the next value
		Assert.True(Enumerable.Range(1, 99).Min(i => Bezier.Anticipate.Ease(i / 100f)) < -0.05f);    // back first
		Assert.Equal(new Bezier(0f, 2f, 1f, -1f), new Bezier(-0.5f, 2f, 1.5f, -1f).Clamped);        // time never runs back
	}

	[Fact]
	public void A_key_with_a_Bezier_eases_its_stretch_and_keeps_it_when_moved_or_re_keyed()
	{
		var turn = new RotateTimeline("arm");
		turn.Track.Add(0f, 0f, CurveType.Bezier, Bezier.EaseIn);
		turn.Track.Add(1f, 10f);
		Assert.True(turn.Track.TrySample(0.5f, out var halfway));
		Assert.Equal(10 * Bezier.EaseIn.Ease(0.5f), halfway, 4);

		turn.Track.Add(0f, 2f);   // a new value, same easing
		Assert.Equal(Bezier.EaseIn, turn.Track.Keys[0].Bezier);
		Assert.Equal(CurveType.Bezier, turn.Track.Keys[0].Curve);

		KeyTimeOps.Move(new[] { (turn as Timeline, 0) }, f => f + 3, Fps);   // moved in the timeline
		var moved = turn.Track.Keys[0];
		Assert.Equal(3, KeyTimeOps.FrameOf(moved.Time, Fps));
		Assert.Equal((CurveType.Bezier, Bezier.EaseIn), (moved.Curve, moved.Bezier));

		Assert.Equal(Bezier.Straight, turn.HandlesAt(1f));   // the last key's own curve: linear
		turn.SetCurve(1f, CurveType.Stepped);
		Assert.Null(turn.HandlesAt(1f));
	}

	[Fact]
	public void Bezier_keys_are_saved_with_their_handles()
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "arm" });
		var animation = new Animation { Name = "wave", Duration = 1f };
		var turn = new RotateTimeline("arm");
		turn.Track.Add(0f, 0f, CurveType.Bezier, new Bezier(0.2f, 1.3f, 0.7f, 0.9f));
		turn.Track.Add(1f, 1f, CurveType.Smooth);
		animation.Timelines.Add(turn);

		var dto = ProjectMapper.ToDto(skeleton, new[] { animation }, Fps);
		Assert.Null(dto.Animations[0].Timelines[0].Keys[1].B);   // only Bézier keys have handles
		var (_, loaded) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var keys = loaded.Single().Find<RotateTimeline>("arm")!.Track.Keys;
		Assert.Equal((CurveType.Bezier, new Bezier(0.2f, 1.3f, 0.7f, 0.9f)), (keys[0].Curve, keys[0].Bezier));
		Assert.Equal(CurveType.Smooth, keys[1].Curve);
	}

	[Fact]
	public void Cutting_frames_out_of_a_Bezier_stretch_keeps_the_frames_looking_the_same()
	{
		var animation = new Animation { Name = "swing", Duration = 1f };
		var turn = new RotateTimeline("arm");
		turn.Track.Add(0f, 0f, CurveType.Bezier, Bezier.Overshoot);
		turn.Track.Add(1f, 1f);
		animation.Timelines.Add(turn);
		var before = Enumerable.Range(0, 31).Select(f => { turn.Track.TrySample(f / 30f, out var v); return v; }).ToArray();

		var piece = AnimationOps.Extract(animation, 10, 20, Fps, "middle");
		var cut = piece.Find<RotateTimeline>("arm")!;
		for (int f = 0; f <= 10; f++)
		{
			Assert.True(cut.Track.TrySample(f / 30f, out var v));
			Assert.Equal(before[f + 10], v, 3);
		}
	}

	// ======================= the graph editor =======================

	// A rotation from 0° at frame 0 to 90° at frame 30, in an 800 × 200 graph: frames 21 wide from
	// x = 160; values from -7.2° to 97.2° (fitted, with a little room), y 40 (top) to 184 (bottom)
	private static (EditorState State, GraphPanel Graph, RotateTimeline Turn) Graph()
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "arm" });
		var animation = new Animation { Name = "wave", Duration = 1f };
		var turn = new RotateTimeline("arm");
		turn.Track.Add(0f, 0f);
		turn.Track.Add(1f, MathF.PI / 2);
		animation.Timelines.Add(turn);
		var state = new EditorState(skeleton, animation) { Playing = false, AnimateMode = true };
		var graph = new GraphPanel(state);
		Draw(graph);
		return (state, graph, turn);
	}

	private static void Draw(GraphPanel graph)
	{
		using var bitmap = new SKBitmap(800, 200);
		using var canvas = new SKCanvas(bitmap);
		graph.Draw(canvas, new Vector2(800, 200));
	}

	private static Vector2 At(float frame, float degrees) =>
		new(160 + frame * 21, 40 + (97.2f - degrees) / 104.4f * 144);

	[Fact]
	public void In_the_graph_a_click_selects_a_key_and_dragging_it_changes_its_value()
	{
		var (state, graph, turn) = Graph();
		graph.MouseDown(At(30, 90), ModifierKeys.None, MouseButton.Left, 1);
		Assert.Equal(new (Timeline, int)[] { (turn, 30) }, state.SelectedKeys);
		graph.MouseMove(At(30, 99), ModifierKeys.Shift);   // up only
		graph.MouseUp();
		Assert.Equal(99f, turn.Track.Keys[1].Value * 180 / MathF.PI, 1);
		Assert.Equal(30, KeyTimeOps.FrameOf(turn.Track.Keys[1].Time, Fps));

		state.Undo.Undo();
		Assert.Equal(MathF.PI / 2, turn.Track.Keys[1].Value, 4);
	}

	[Fact]
	public void Dragging_a_keys_handle_eases_its_stretch_with_a_Bezier()
	{
		var (state, graph, turn) = Graph();
		graph.MouseDown(At(0, 0), ModifierKeys.None, MouseButton.Left, 1);   // the first key: its handles show
		graph.MouseUp();
		Draw(graph);

		// Its way-out handle, a third of the way along (straight, for a linear stretch): to halfway, flat
		graph.MouseDown(At(10, 30), ModifierKeys.None, MouseButton.Left, 1);
		graph.MouseMove(At(15, 0), ModifierKeys.None);
		graph.MouseUp();

		var key = turn.Track.Keys[0];
		Assert.Equal(CurveType.Bezier, key.Curve);
		Assert.Equal(0.5f, key.Bezier.X1, 2);
		Assert.Equal(0f, key.Bezier.Y1, 2);
		Assert.Equal(2 / 3f, key.Bezier.X2, 3);   // the other handle as it was
		Assert.Equal("Ease a stretch (Bézier handle)", state.Undo.NextUndoName);
	}
}
