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

// A step that can take a later one into itself, both as one (see UndoStack.BeginGroup)
public interface IJoinableCommand
{
	IEditCommand? JoinedWith(IEditCommand later);
}

public sealed class SnapshotCommand<T> : IEditCommand, IMergeableCommand, IJoinableCommand
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

	// Two snapshots of the same thing, one after the other, as one: the first's before, the
	// second's after (a group of them, like a drag in a number field, then keeps just two). Only
	// when they restore the same way: null otherwise.
	public IEditCommand? JoinedWith(IEditCommand later) =>
		later is SnapshotCommand<T> other && ReferenceEquals(_restore, other._restore)
			? new SnapshotCommand<T>(other.Name, _before, other._after, _restore, MergeKey)
			: null;

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

	public IReadOnlyList<IEditCommand> Parts => _parts;

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

	// The history (Edit → History): what can be undone, oldest first, and what can be redone,
	// the next one first
	public IReadOnlyList<string> UndoNames => _undo.Select(s => s.Command.Name).ToList();
	public IReadOnlyList<string> RedoNames => _redo.Select(s => s.Command.Name).ToList();
	public int UndoCount => _undo.Count;
	public int RedoCount => _redo.Count;

	// A group: the steps pushed until EndGroup become one (a drag in a number field: every
	// value it went through, one undo). Snapshots of the same thing are joined as they come,
	// so a long drag keeps two, not hundreds.
	private List<IEditCommand>? _group;
	public bool InGroup => _group != null;

	public void BeginGroup() => _group ??= new List<IEditCommand>();

	public void EndGroup()
	{
		var group = _group;
		_group = null;
		if (group is not { Count: > 0 }) return;
		Push(group.Count == 1 ? group[0] : new CompositeCommand(group[^1].Name, group.ToArray()));
	}

	// Undo or redo until this many steps can be undone (a click in the history). Steps made
	// elsewhere take you back there first, as undo does; a step that can't be got to stops it.
	public void MoveTo(int undoCount)
	{
		undoCount = Math.Clamp(undoCount, 0, _undo.Count + _redo.Count);
		int stuck = 0;
		while (_undo.Count != undoCount && stuck < 2)
		{
			int before = _undo.Count;
			if (_undo.Count > undoCount) Undo();
			else Redo();
			stuck = _undo.Count == before ? stuck + 1 : 0;   // (once: it went back to a place first)
		}
	}

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
		if (_group != null)
		{
			_redo.Clear();
			if (_group.Count > 0 && _group[^1] is IJoinableCommand last && last.JoinedWith(cmd) is { } joined) _group[^1] = joined;
			else _group.Add(cmd);
			Changed?.Invoke();
			return;
		}

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
		if (_group != null) EndGroup();   // (a drag still going: it's a step of its own first)
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
		if (_group != null) EndGroup();
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
