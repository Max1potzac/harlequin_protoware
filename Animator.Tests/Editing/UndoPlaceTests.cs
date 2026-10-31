namespace Animator.Tests;

// Undo and redo first go back to where a step was made (another animation, mode, the paint
// editor…), as a step of their own, so nothing is undone out of sight
public class UndoPlaceTests
{
	private sealed class Program
	{
		public string Where = "idle";
		public int Value;
		public readonly UndoStack Undo = new();

		public Program(bool reachable = true)
		{
			Undo.Place = () => Where;
			Undo.GoTo = place => { if (reachable) Where = (string)place; };
		}

		public void Set(int value)
		{
			int before = Value;
			Value = value;
			Undo.Push(new SnapshotCommand<int>($"Set {value}", before, value, v => Value = v));
		}
	}

	[Fact]
	public void Undo_and_redo_first_go_back_to_where_the_step_was_made()
	{
		var program = new Program();
		program.Set(1);
		program.Where = "walk";
		Assert.True(program.Undo.UndoGoesBackFirst);
		Assert.Equal("idle", program.Undo.NextUndoPlace);

		program.Undo.Undo();   // back to idle, nothing undone yet
		Assert.Equal("idle", program.Where);
		Assert.Equal(1, program.Value);
		Assert.False(program.Undo.UndoGoesBackFirst);
		program.Undo.Undo();
		Assert.Equal(0, program.Value);

		program.Where = "walk";
		program.Undo.Redo();   // redo too
		Assert.Equal("idle", program.Where);
		Assert.Equal(0, program.Value);
		program.Undo.Redo();
		Assert.Equal(1, program.Value);
	}

	[Fact]
	public void Steps_made_here_undo_right_away_and_a_place_thats_gone_holds_nothing_up()
	{
		var program = new Program(reachable: false);
		program.Set(1);
		program.Where = "walk";
		program.Set(2);
		program.Undo.Undo();   // made here: undone at once
		Assert.Equal(1, program.Value);
		program.Undo.Undo();   // made in idle, which can't be got back to: undone at once too
		Assert.Equal(0, program.Value);
		Assert.Equal("walk", program.Where);
	}

	[Fact]
	public void Going_back_doesnt_count_as_a_change()
	{
		var program = new Program();
		program.Set(1);
		program.Undo.MarkSaved();
		Assert.False(program.Undo.IsDirty);
		program.Where = "walk";
		program.Undo.Undo();   // only goes back
		Assert.False(program.Undo.IsDirty);
		program.Undo.Undo();
		Assert.True(program.Undo.IsDirty);
	}
}
