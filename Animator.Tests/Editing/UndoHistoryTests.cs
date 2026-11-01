namespace Animator.Tests;

public class UndoHistoryTests
{
	private sealed class Box { public int Value; }

	private static readonly Action<(Box Box, int Value)> Restore = s => s.Box.Value = s.Value;

	private static void Set(UndoStack undo, Box box, int value, string name)
	{
		var before = (box, box.Value);
		box.Value = value;
		undo.Push(new SnapshotCommand<(Box, int)>(name, before, (box, value), Restore));
	}

	[Fact]
	public void A_group_is_one_step()
	{
		var undo = new UndoStack();
		var box = new Box();
		Set(undo, box, 1, "First");
		undo.BeginGroup();
		for (int i = 2; i <= 50; i++) Set(undo, box, i, "Drag");
		undo.EndGroup();

		Assert.Equal(2, undo.UndoCount);
		Assert.Equal(new[] { "First", "Drag" }, undo.UndoNames);
		undo.Undo();
		Assert.Equal(1, box.Value);
		undo.Redo();
		Assert.Equal(50, box.Value);
	}

	[Fact]
	public void An_empty_group_leaves_no_step()
	{
		var undo = new UndoStack();
		undo.BeginGroup();
		undo.EndGroup();
		Assert.Equal(0, undo.UndoCount);
		Assert.False(undo.InGroup);
	}

	[Fact]
	public void Different_things_in_a_group_undo_together()
	{
		var undo = new UndoStack();
		var a = new Box();
		var b = new Box();
		undo.BeginGroup();
		Set(undo, a, 5, "A");
		undo.Push(new SnapshotCommand<int>("B", 0, 7, v => b.Value = v));
		b.Value = 7;
		undo.EndGroup();
		Assert.Equal(1, undo.UndoCount);
		undo.Undo();
		Assert.Equal((0, 0), (a.Value, b.Value));
	}

	[Fact]
	public void Going_to_a_point_in_the_history_undoes_or_redoes_the_steps_between()
	{
		var undo = new UndoStack();
		var box = new Box();
		for (int i = 1; i <= 5; i++) Set(undo, box, i, $"Set {i}");

		undo.MoveTo(2);
		Assert.Equal(2, box.Value);
		Assert.Equal(new[] { "Set 1", "Set 2" }, undo.UndoNames);
		Assert.Equal(new[] { "Set 3", "Set 4", "Set 5" }, undo.RedoNames);

		undo.MoveTo(4);
		Assert.Equal(4, box.Value);
		undo.MoveTo(0);
		Assert.Equal(0, box.Value);
		undo.MoveTo(99);
		Assert.Equal(5, box.Value);
	}
}
