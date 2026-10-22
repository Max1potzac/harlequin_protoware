namespace Animator.Core.Rigging;

public class Slot
{
    public string Name { get; set; } = "";
    public Bone Bone { get; }
    public string? ActiveAttachment { get; set; }   // this is what the attachment timeline will change

    public string? SetupAttachment { get; set; }
    public Slot(string name, Bone bone) { Name = name; Bone = bone; }
}