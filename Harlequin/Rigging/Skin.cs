namespace Animator.Core.Rigging;

public class Skin
{
    public string Name { get; set; } = "";
    private readonly Dictionary<(string Slot, string Attachment), Attachment> _map = new();

    public void Set(string slot, Attachment a) => _map[(slot, a.Name)] = a;
    public Attachment? Get(string slot, string name) =>
        _map.TryGetValue((slot, name), out var a) ? a : null;
	public IEnumerable<Attachment> Attachments => _map.Values;
}