namespace Animator.Core.Editing;

public interface IEditCommand
{
	string Name { get; }
	void Undo();
	void Redo();
}

public interface IMergeableCommand
{
	bool TryMerge(IEditCommand next);
}

public sealed class SnapshotCommand<T> : IEditCommand, IMergeableCommand
{
	private readonly T _before;
	private T _after;
	private readonly Action<T> _restore;
	private DateTime _lastChange = DateTime.UtcNow;

	public string Name { get; }
	public string? MergeKey { get; }

	public SnapshotCommand(string name, T before, T after, Action<T> restore, string? mergeKey = null)
	{
		Name = name;
		_before = before;
		_after = after;
		_restore = restore;
		MergeKey = mergeKey;
	}

	public void Undo() => _restore(_before);
	public void Redo() => _restore(_after);

	public bool TryMerge(IEditCommand next)
	{
		if (MergeKey == null || next is not SnapshotCommand<T> other || other.MergeKey != MergeKey) return false;
		if (DateTime.UtcNow - _lastChange > TimeSpan.FromSeconds(1)) return false;
		_after = other._after;
		_lastChange = DateTime.UtcNow;
		return true;
	}
}

// Several commands as one undo step (for example a rig edit that also changes the scene).
// Undone in reverse order, redone in order.
public sealed class CompositeCommand : IEditCommand
{
	private readonly IEditCommand[] _parts;
	public string Name { get; }

	public CompositeCommand(string name, params IEditCommand[] parts)
	{
		Name = name;
		_parts = parts;
	}

	public void Undo()
	{
		for (int i = _parts.Length - 1; i >= 0; i--) _parts[i].Undo();
	}

	public void Redo()
	{
		foreach (var part in _parts) part.Redo();
	}
}

public class UndoStack
{
	// Each step, with where it was made (see Place)
	private readonly LinkedList<(IEditCommand Command, object? Place)> _undo = new();
	private readonly Stack<(IEditCommand Command, object? Place)> _redo = new();

	private IEditCommand? _savedAt;   // the command on top when the file was last saved
	private bool _blockMerge;         // don't let the saved command absorb new edits

	public int Limit { get; set; } = 300;

	public event Action? Changed;
	public string? NextUndoName => _undo.Last?.Value.Command.Name;

	// Where you are in the program (which object, animation, mode… the program says what that
	// is), and how to go back to such a place. Every step remembers where it was made; undo and
	// redo first take you back there, as a step of their own, when you've moved on since. So an
	// edit to one animation can't be undone, unseen, while you work on another.
	public Func<object?>? Place { get; set; }
	public Action<object>? GoTo { get; set; }

	// Where the next undo was made (null: nowhere in particular), and whether undo would only
	// take you back there this time
	public object? NextUndoPlace => _undo.Last?.Value.Place;
	public bool UndoGoesBackFirst => _undo.Last is { } node && Elsewhere(node.Value.Place);

	// "Unsaved changes" = the top of the history is not where it was at save time
	public bool IsDirty => _undo.Last?.Value.Command != _savedAt;

	public void Push(IEditCommand cmd)
	{
		bool hadRedo = _redo.Count > 0;
		_redo.Clear();

		if (!hadRedo && !_blockMerge && _undo.Last?.Value.Command is IMergeableCommand top && top.TryMerge(cmd))
		{
			Changed?.Invoke();
			return;
		}

		_blockMerge = false;
		_undo.AddLast((cmd, Place?.Invoke()));
		while (_undo.Count > Limit) _undo.RemoveFirst();
		Changed?.Invoke();
	}

	public void Undo()
	{
		if (_undo.Last is not { } node) return;
		if (WentBack(node.Value.Place))
		{
			Changed?.Invoke();
			return;
		}
		_undo.RemoveLast();
		node.Value.Command.Undo();
		_redo.Push(node.Value);
		_blockMerge = true;
		Changed?.Invoke();
	}

	public void Redo()
	{
		if (_redo.Count == 0) return;
		if (WentBack(_redo.Peek().Place))
		{
			Changed?.Invoke();
			return;
		}
		var step = _redo.Pop();
		step.Command.Redo();
		_undo.AddLast(step);
		_blockMerge = true;
		Changed?.Invoke();
	}

	private bool Elsewhere(object? place) => place != null && Place != null && GoTo != null && !Equals(Place(), place);

	// Takes you back to where a step was made, if you're elsewhere. false: you were there
	// already, or it can't be got back to (gone, say): then the step itself goes right away.
	private bool WentBack(object? place)
	{
		if (!Elsewhere(place)) return false;
		GoTo!(place!);
		return Equals(Place!(), place);
	}

	public void MarkSaved()
	{
		_savedAt = _undo.Last?.Value.Command;
		_blockMerge = true;
		Changed?.Invoke();
	}

	public void Clear()
	{
		_undo.Clear();
		_redo.Clear();
		_savedAt = null;
		_blockMerge = false;
		Changed?.Invoke();
	}
}
