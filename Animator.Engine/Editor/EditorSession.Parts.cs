using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer;

// A part's placement on its bone (offset, turn, scale) and its pivot, at rest or keyed
public enum PartField { X, Y, Rotation, ScaleX, ScaleY, PivotX, PivotY }

// The open object's parts and their drawings, edited (EditorSession's side of the part properties
// and the drawing controls): parts made, renamed, placed (at rest or keyed), put on another bone,
// deleted; drawings shown, made (blank or copied), renamed, deleted, held longer or shorter. Each
// edit is one undo step and says whether it changed anything. Angles are in radians.
public sealed partial class EditorSession
{
	// ======================= parts =======================

	// A new part on a bone, of a picture this size (these pixels, or a blank layer): halfway along
	// the bone, turned to look upright as the rig is posed now, selected. Editing a skin: it's the skin's.
	public Slot CreatePart(Bone bone, string name, SKBitmap? pixels, int width, int height)
	{
		Slot? made = null;
		State.EditStructure(() =>
		{
			var asset = new Asset { Name = name, Width = width, Height = height };
			Assets.AddLayer(asset, "Base", pixels);
			made = PartOps.CreatePart(Rig, bone, asset, name, new Vector2(bone.Length / 2, 0), -bone.WorldRotation);
			if (State.EditSkin is { } skin) SkinOps.AddParts(Rig, skin, new[] { made });
			State.SelectedSlot = made;
			State.Selected = bone;
			return $"Create part {made.Name}";
		});
		return made!;
	}

	// Gone, with its keys
	public bool DeletePart(Slot slot)
	{
		State.SelectedKeys.Clear();
		return State.EditStructure(() =>
		{
			PartOps.DeletePart(Rig, State.Animations, slot);
			State.SelectedSlot = null;
			return $"Delete part {slot.Name}";
		});
	}

	// False: empty, unchanged, or another part's name
	public bool RenamePart(Slot slot, string newName)
	{
		newName = newName.Trim();
		return newName != slot.Name && State.EditStructure(() =>
			PartOps.RenamePart(Rig, State.Animations, slot, newName) ? $"Rename part to {newName}" : null);
	}

	// Its drawing's rest placement, or its pivot (in pixels: the drawing stays where it is).
	// Editing a skin: the skin's own copy of the drawing.
	public bool SetPartRest(Slot slot, PartField field, float v)
	{
		var shown = Rig.ResolveAttachment(slot);
		if (shown == null) return false;
		var asset = Rig.Assets.GetValueOrDefault(shown.AssetId);
		return State.EditStructure(() =>
		{
			var att = State.EditableDrawing(slot) ?? shown;
			switch (field)
			{
				case PartField.X: att.Offset = new Vector2(v, att.Offset.Y); break;
				case PartField.Y: att.Offset = new Vector2(att.Offset.X, v); break;
				case PartField.Rotation: att.Rotation = v; break;
				case PartField.ScaleX: att.Scale = new Vector2(v, att.Scale.Y); break;
				case PartField.ScaleY: att.Scale = new Vector2(att.Scale.X, v); break;
				case PartField.PivotX when asset != null:
					PartOps.SetPivotKeepingPlace(att, asset.Width, asset.Height, new Vector2(v, att.Pivot.Y * asset.Height));
					break;
				case PartField.PivotY when asset != null:
					PartOps.SetPivotKeepingPlace(att, asset.Width, asset.Height, new Vector2(att.Pivot.X * asset.Width, v));
					break;
				default: return null;
			}
			return $"Set part {slot.Name} {field}";
		});
	}

	// A placement keyed at the playhead, so the part shows exactly there (keys hold what's added
	// on top of its drawing's own placement)
	public bool KeyPart(Slot slot, PartField field, float v)
	{
		if (Rig.ResolveAttachment(slot) is not { } att) return false;
		State.Playing = false;
		State.SetFrame(State.Frame);
		var anim = State.Animation;
		float t = State.Time;
		int frame = State.Frame;
		static float Ratio(float a, float b) => MathF.Abs(b) < 1e-6f ? a : a / b;
		return State.EditStructure(() =>
		{
			switch (field)
			{
				case PartField.X:
					anim.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track.Add(t, new Vector2(v - att.Offset.X, slot.Offset.Y));
					break;
				case PartField.Y:
					anim.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track.Add(t, new Vector2(slot.Offset.X, v - att.Offset.Y));
					break;
				case PartField.Rotation:
					anim.GetOrCreate(slot.Name, n => new PartRotateTimeline(n)).Track.Add(t, v - att.Rotation);
					break;
				case PartField.ScaleX:
					anim.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track.Add(t, new Vector2(Ratio(v, att.Scale.X), slot.Scale.Y));
					break;
				case PartField.ScaleY:
					anim.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track.Add(t, new Vector2(slot.Scale.X, Ratio(v, att.Scale.Y)));
					break;
				default:
					return null;
			}
			return $"Key part {slot.Name} {field} @ frame {frame}";
		});
	}

	// On another bone, staying where it is (the bone selected)
	public bool MovePartToBone(Slot slot, Bone bone) =>
		State.EditStructure(() =>
		{
			if (!PartOps.MoveToBone(Rig, slot, bone)) return null;
			State.Selected = bone;
			return $"Move part {slot.Name} to {bone.Name}";
		});

	// ======================= drawings =======================

	// The drawing the drawing controls work on: the one at the playhead (Animate), or the rest one
	public string? CurrentDrawing(Slot slot) =>
		State.AnimateMode ? DrawingOps.DrawingAt(State.Animation, slot, State.Time) : slot.SetupAttachment;

	// Stops playback, the playhead exactly on its frame (when keying); the frame
	private int SnapToFrame(bool key)
	{
		if (key)
		{
			State.Playing = false;
			State.SetFrame(State.Frame);
		}
		return State.Frame;
	}

	// A drawing shown (null: hidden): from the playhead on (Animate), or at rest
	public bool ShowDrawing(Slot slot, string? name)
	{
		bool key = State.AnimateMode;
		int frame = SnapToFrame(key);
		return State.EditStructure(() =>
			DrawingOps.Show(State.Animation, slot, name, frame, State.Fps, key)
				? key ? $"Show {name ?? "(hidden)"} on {slot.Name} @ frame {frame}" : $"{slot.Name} rests on {name ?? "(hidden)"}"
				: null);
	}

	// False: it's the part's only drawing
	public bool DeleteDrawing(Slot slot, string name) =>
		State.EditStructure(() => DrawingOps.DeleteDrawing(Rig, State.Animations, slot, name) ? $"Delete drawing {name}" : null);

	// False: empty, or another drawing of the part has it
	public bool RenameDrawing(Slot slot, string oldName, string newName)
	{
		newName = newName.Trim();
		return newName != oldName && State.EditStructure(() =>
			DrawingOps.RenameDrawing(Rig, State.Animations, slot, oldName, newName) ? $"Rename drawing {oldName} to {newName}" : null);
	}

	// F7 (blank) / F6 (copy): a new drawing for the part, the same size and layers as the one it
	// shows (or its rest one, or any): shown from the playhead on (Animate), or its rest drawing
	// (Setup). Editing a skin: the skin's. Null: the part has no drawing to start from.
	public Attachment? NewDrawing(Slot slot, bool copy)
	{
		bool key = State.AnimateMode;
		int frame = SnapToFrame(key);
		var template = DrawingOps.Find(Rig, slot, CurrentDrawing(slot))
			?? DrawingOps.Find(Rig, slot, slot.SetupAttachment)
			?? DrawingOps.DrawingNames(Rig, slot).Select(n => DrawingOps.Find(Rig, slot, n)).FirstOrDefault(a => a != null);
		if (template == null || !Rig.Assets.TryGetValue(template.AssetId, out var source)) return null;

		Attachment? created = null;
		State.EditStructure(() =>
		{
			var pixels = new Asset { Width = source.Width, Height = source.Height };
			foreach (var layer in source.Layers)
			{
				var added = Assets.AddLayer(pixels, layer.Name, copy ? Assets.GetLayer(layer) : null);
				added.Visible = layer.Visible;
				added.Opacity = layer.Opacity;
			}
			created = DrawingOps.AddDrawing(Rig, slot, template, pixels, State.EditSkin);
			if (State.EditSkin is { } skin) SkinOps.Show(Rig, skin);
			DrawingOps.Show(State.Animation, slot, created.Name, frame, State.Fps, key);
			return $"{(copy ? "Duplicate" : "New")} drawing {created.Name}{(key ? $" @ frame {frame}" : "")}";
		});
		return created;
	}

	// F5 (+1) / Shift+F5 (-1): the drawing at the playhead held a frame longer or shorter, the
	// part's later drawings moving with it (Animate mode; null otherwise)
	public HoldChange? ChangeHold(Slot slot, int frames)
	{
		if (!State.AnimateMode) return null;
		int frame = SnapToFrame(key: true);
		var result = HoldChange.NothingAfter;
		State.EditStructure(() =>
		{
			result = frames > 0
				? DrawingOps.InsertFrame(State.Animation, slot, frame, State.Fps, State.FrameCount)
				: DrawingOps.RemoveFrame(State.Animation, slot, frame, State.Fps);
			return result == HoldChange.Done ? $"{(frames > 0 ? "Longer" : "Shorter")} hold on {slot.Name} @ frame {frame}" : null;
		});
		return result;
	}
}
