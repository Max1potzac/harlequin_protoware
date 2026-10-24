using System.Numerics;

namespace Animator.Core.Editing;

// Fitting bones to finished art (Setup mode): remember where every joint, bone end and part is in
// the rest pose, change some bones, then put everything else back exactly where it was. That's
// "parts stay put": moving a joint doesn't drag the drawings, or the other joints, along.
public sealed class FrozenRest
{
	private readonly Skeleton _skeleton;
	private readonly Dictionary<Bone, (Vector2 Head, Vector2 Tip, float Angle, bool Connected)> _bones = new();
	private readonly List<(Attachment Drawing, Slot Slot, Matrix3x2 Frame)> _drawings = new();

	private FrozenRest(Skeleton skeleton) => _skeleton = skeleton;

	public static FrozenRest Capture(Skeleton skeleton)
	{
		var frozen = new FrozenRest(skeleton);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		foreach (var bone in skeleton.AllBones())
			frozen._bones[bone] = (bone.World.Translation, bone.TipWorld, bone.WorldRotation, bone.Connected);
		foreach (var slot in skeleton.Slots)
			foreach (var drawing in skeleton.AllSkins().SelectMany(s => s.AttachmentsFor(slot.Name)).Distinct())
				frozen._drawings.Add((drawing, slot, PartOps.FrameOf(drawing) * slot.Bone.World));
		return frozen;
	}

	// Where a bone's joint and end were when this was captured
	public Vector2 HeadOf(Bone bone) => _bones.TryGetValue(bone, out var b) ? b.Head : bone.World.Translation;
	public Vector2 TipOf(Bone bone) => _bones.TryGetValue(bone, out var b) ? b.Tip : bone.TipWorld;
	public bool WasConnected(Bone bone) => _bones.TryGetValue(bone, out var b) ? b.Connected : bone.Connected;

	// Everything back where it was, except the `moved` bones (whose rest values were just set).
	// A bone glued to its parent's end follows that end and turns and stretches to keep its own
	// end where it was; any other bone keeps its joint and angle. Every drawing keeps its place.
	public void Restore(IReadOnlyCollection<Bone> moved)
	{
		foreach (var root in _skeleton.Roots) Restore(root, moved);
		foreach (var (drawing, slot, frame) in _drawings)
			PartOps.PlaceFrame(drawing, frame, slot.Bone.World);
	}

	private void Restore(Bone bone, IReadOnlyCollection<Bone> moved)
	{
		if (!moved.Contains(bone) && _bones.TryGetValue(bone, out var was))
		{
			if (bone.Connected && bone.Parent is { } parent)
			{
				Aim(bone, parent.TipWorld, was.Tip);
			}
			else
			{
				bone.SetupPosition = bone.Parent is { } p && Matrix3x2.Invert(p.World, out var toParent)
					? Vector2.Transform(was.Head, toParent)
					: was.Head;
				bone.SetupRotation = was.Angle - ParentAngle(bone);
			}
		}
		bone.ResetToSetup();
		bone.UpdateWorld();
		foreach (var child in bone.Children) Restore(child, moved);
	}

	// Turns and stretches a bone whose joint is at `head` so it ends at `tip` (its parent's world
	// transform must be up to date). A tip on the joint leaves it as it is.
	public static void Aim(Bone bone, Vector2 head, Vector2 tip)
	{
		var along = tip - head;
		float distance = along.Length();
		if (distance < 1e-3f) return;
		bone.SetupRotation = MathF.Atan2(along.Y, along.X) - ParentAngle(bone);
		bone.ResetToSetup();
		bone.UpdateWorld();
		float unit = new Vector2(bone.World.M11, bone.World.M12).Length();   // world units per unit of length
		bone.Length = unit > 1e-6f ? distance / unit : distance;
	}

	private static float ParentAngle(Bone bone) =>
		bone.Parent != null && bone.InheritRotation ? bone.Parent.WorldRotation : 0f;
}

// Joint edits that keep everything else where it is (see FrozenRest). The rest pose changes; the
// caller captures `frozen` before the edit starts and computes every step of a drag from it.
public static class RestEdits
{
	// Moves a bone's joint to `target` (world). A joint that was glued to its parent's end when
	// `frozen` was captured is that end too (an elbow): the parent turns and stretches to it. The
	// bone itself turns and stretches so its own end stays where it was. `snapTo`: a free joint
	// glues on to its parent's end when it's that near, and lets go again when pulled away.
	public static void MoveJoint(FrozenRest frozen, Bone bone, Vector2 target, float snapTo = 0f)
	{
		var moved = new List<Bone> { bone };
		var parent = bone.Parent;
		if (frozen.WasConnected(bone) && parent != null)
		{
			bone.Connected = true;
			FrozenRest.Aim(parent, frozen.HeadOf(parent), target);
			moved.Add(parent);
		}
		else if (parent != null && snapTo > 0 && Vector2.Distance(target, frozen.TipOf(parent)) <= snapTo)
		{
			bone.Connected = true;
		}
		else
		{
			bone.Connected = false;
			bone.SetupPosition = parent != null && Matrix3x2.Invert(parent.World, out var toParent)
				? Vector2.Transform(target, toParent)
				: target;
		}

		var top = parent != null && moved.Contains(parent) ? parent : bone;
		top.ResetToSetup();
		bone.ResetToSetup();   // on its parent's end, its place follows the parent's new length
		top.UpdateWorld();
		if (Vector2.Distance(frozen.HeadOf(bone), frozen.TipOf(bone)) > 1e-3f)   // a bone with no length just moves
			FrozenRest.Aim(bone, bone.World.Translation, frozen.TipOf(bone));
		frozen.Restore(moved);
	}

	// Moves a bone's end to `target` (world): it turns and stretches to reach it from its joint,
	// and what's on it stays put. `snapTo`: a child whose joint is that near (one not on the end
	// already) gets the end put exactly on its joint and is glued there; pulled away again in the
	// same drag, it lets go. Returns the child it's glued to, if any.
	public static Bone? MoveTip(FrozenRest frozen, Bone bone, Vector2 target, float snapTo = 0f)
	{
		Bone? glued = null;
		float nearest = snapTo;
		foreach (var child in bone.Children)
		{
			if (frozen.WasConnected(child)) continue;
			float d = Vector2.Distance(target, frozen.HeadOf(child));
			if (d > nearest) continue;
			nearest = d;
			glued = child;
		}
		if (glued != null) target = frozen.HeadOf(glued);
		foreach (var child in bone.Children)
			if (!frozen.WasConnected(child)) child.Connected = child == glued;

		var moved = new List<Bone> { bone };
		bone.ResetToSetup();
		bone.UpdateWorld();
		FrozenRest.Aim(bone, frozen.HeadOf(bone), target);
		if (glued != null)
		{
			// Its joint is the end now: where it was. It turns to keep its own end where it was too.
			moved.Add(glued);
			glued.ResetToSetup();
			glued.UpdateWorld();
			if (Vector2.Distance(frozen.HeadOf(glued), frozen.TipOf(glued)) > 1e-3f)
				FrozenRest.Aim(glued, glued.World.Translation, frozen.TipOf(glued));
		}
		frozen.Restore(moved);
		return glued;
	}

	// Whether a bone's end is its own to move: no child sits on it
	public static bool HasFreeEnd(Bone bone) => bone.Children.All(c => !c.Connected);

	// Turns a bone to a rest angle (relative to its parent); its parts and child bones stay put
	public static void Turn(FrozenRest frozen, Bone bone, float setupRotation)
	{
		bone.SetupRotation = setupRotation;
		frozen.Restore(new[] { bone });
	}

	// Changes a bone's length; what's on it stays put (bones on its end follow the end)
	public static void Stretch(FrozenRest frozen, Bone bone, float length)
	{
		bone.Length = MathF.Max(0f, length);
		frozen.Restore(new[] { bone });
	}
}
