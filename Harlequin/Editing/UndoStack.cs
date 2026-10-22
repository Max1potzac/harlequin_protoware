namespace Animator.Core.Editing;

public interface IEditCommand
{
	string Name { get; }
	void Undo();
	void Redo();
}

// A command that can absorb the command pushed right after it
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

	// Commands with the same MergeKey pushed back-to-back (within a second)
	// become a single undo step, e.g. one whole slider drag
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
		if (DateTime.UtcNow - _lastChange > TimeSpan.FromSeconds(1)) return false;   // a pause starts a new step

		_after = other._after;   // keep our "before", take their "after"
		_lastChange = DateTime.UtcNow;
		return true;
	}
}

public class UndoStack
{
	// A linked list instead of a Stack, so the OLDEST entry can be dropped cheaply
	private readonly LinkedList<IEditCommand> _undo = new();
	private readonly Stack<IEditCommand> _redo = new();

	public int Limit { get; set; } = 300;

	public event Action? Changed;
	public string? NextUndoName => _undo.Last?.Value.Name;

	public void Push(IEditCommand cmd)
	{
		bool hadRedo = _redo.Count > 0;
		_redo.Clear();

		// Merge only with a command that is genuinely the latest edit (not one exposed by undo)
		if (!hadRedo && _undo.Last?.Value is IMergeableCommand top && top.TryMerge(cmd))
		{
			Changed?.Invoke();
			return;
		}

		_undo.AddLast(cmd);
		while (_undo.Count > Limit) _undo.RemoveFirst();   // forget the oldest steps
		Changed?.Invoke();
	}

	public void Undo()
	{
		if (_undo.Last is not { } node) return;
		_undo.RemoveLast();
		node.Value.Undo();
		_redo.Push(node.Value);
		Changed?.Invoke();
	}

	public void Redo()
	{
		if (_redo.Count == 0) return;
		var cmd = _redo.Pop();
		cmd.Redo();
		_undo.AddLast(cmd);
		Changed?.Invoke();
	}
}