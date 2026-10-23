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

public class UndoStack
{
	private readonly LinkedList<IEditCommand> _undo = new();
	private readonly Stack<IEditCommand> _redo = new();

	private IEditCommand? _savedAt;   // the command on top when the file was last saved
	private bool _blockMerge;         // don't let the saved command absorb new edits

	public int Limit { get; set; } = 300;

	public event Action? Changed;
	public string? NextUndoName => _undo.Last?.Value.Name;

	// "Unsaved changes" = the top of the history is not where it was at save time
	public bool IsDirty => _undo.Last?.Value != _savedAt;

	public void Push(IEditCommand cmd)
	{
		bool hadRedo = _redo.Count > 0;
		_redo.Clear();

		if (!hadRedo && !_blockMerge && _undo.Last?.Value is IMergeableCommand top && top.TryMerge(cmd))
		{
			Changed?.Invoke();
			return;
		}

		_blockMerge = false;
		_undo.AddLast(cmd);
		while (_undo.Count > Limit) _undo.RemoveFirst();
		Changed?.Invoke();
	}

	public void Undo()
	{
		if (_undo.Last is not { } node) return;
		_undo.RemoveLast();
		node.Value.Undo();
		_redo.Push(node.Value);
		_blockMerge = true;
		Changed?.Invoke();
	}

	public void Redo()
	{
		if (_redo.Count == 0) return;
		var cmd = _redo.Pop();
		cmd.Redo();
		_undo.AddLast(cmd);
		_blockMerge = true;
		Changed?.Invoke();
	}

	public void MarkSaved()
	{
		_savedAt = _undo.Last?.Value;
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