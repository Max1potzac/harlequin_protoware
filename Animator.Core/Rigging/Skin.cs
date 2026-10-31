namespace Animator.Core.Rigging;

// A skin: drawings for some parts (one or several), shown in place of the object's own when the
// skin is on. A part whose drawings are all in skins shows only while one of them is on (a hat,
// a bucket). Skins in the same category are alternatives, one on at a time (the bucket and its
// dents); skins in no category are outfits, one at a time too. Overlays stack: any number of them
// show at once, over whatever else is on (see SkinOps).
public class Skin
{
    public string Name { get; set; } = "";
	public string Category { get; set; } = "";   // "" = none (an outfit)
	public bool Overlay { get; set; }
	// The object's colour layers that apply only while this skin is on (a colour layer is in one
	// skin at most; it stays in the object's list, which keeps the order they apply in)
	public List<ColorLayer> ColorLayers { get; } = new();
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
	public IEnumerable<Attachment> AttachmentsFor(string slot) =>
	_map.Where(kv => kv.Key.Slot == slot).Select(kv => kv.Value);

	public bool Remove(string slot, string name) => _map.Remove((slot, name));

	// Gives one attachment a new name (the same object, so everything holding it stays valid)
	public void RenameAttachment(string slot, string oldName, string newName)
	{
		if (!_map.Remove((slot, oldName), out var attachment)) return;
		attachment.Name = newName;
		_map[(slot, newName)] = attachment;
	}

	public void RemoveSlot(string slot)
	{
		foreach (var key in _map.Keys.Where(k => k.Slot == slot).ToList())
			_map.Remove(key);
	}

	// Entries are keyed by slot name, so a renamed slot needs its entries re-keyed
	public void RenameSlot(string oldName, string newName)
	{
		foreach (var (key, attachment) in _map.Where(kv => kv.Key.Slot == oldName).ToList())
		{
			_map.Remove(key);
			_map[(newName, key.Attachment)] = attachment;
		}
	}

	// For undo snapshots
	internal Dictionary<(string Slot, string Attachment), Attachment> CopyMap() => new(_map);

	internal void RestoreMap(Dictionary<(string Slot, string Attachment), Attachment> map)
	{
		_map.Clear();
		foreach (var kv in map) _map[kv.Key] = kv.Value;
	}
}