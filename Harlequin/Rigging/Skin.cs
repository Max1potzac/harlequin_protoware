namespace Animator.Core.Rigging;

public class Skin
{
    public string Name { get; set; } = "";
    private readonly Dictionary<(string Slot, string Attachment), Attachment> _map = new();

    public void Set(string slot, Attachment a) => _map[(slot, a.Name)] = a;
    public Attachment? Get(string slot, string name) =>
        _map.TryGetValue((slot, name), out var a) ? a : null;
	public IEnumerable<Attachment> Attachments => _map.Values;
	// (slot name, attachment) pairs, for saving
	public IEnumerable<(string Slot, Attachment Attachment)> Entries =>
		_map.Select(kv => (kv.Key.Slot, kv.Value));

	// Takes over another skin's contents (used when opening a file)
	public void ReplaceWith(Skin other)
	{
		_map.Clear();
		foreach (var kv in other._map) _map[kv.Key] = kv.Value;
	}
}