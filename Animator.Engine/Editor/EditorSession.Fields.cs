using System.Globalization;

namespace Animator.Viewer;

// The numbers of the bone and part panels, as they show and are typed (EditorSession's side of
// the panels' fields, for every editor): angles in degrees, a part's placement as the drawing's
// own plus its keyed offset. In Build they're the rest values; in Animate the pose's at the
// playhead, and typing one keys it (auto key on) or poses it until S (auto key off).
public sealed partial class EditorSession
{
	private KeyEditor? _keys;
	private KeyEditor Keys => _keys ??= new KeyEditor(State);   // (for holding a pose until it's keyed)

	// ======================= numbers as text =======================

	public static string Num(float? v) => v is { } f ? f.ToString("0.##", CultureInfo.CurrentCulture) : "";

	// (the way this computer writes numbers, or with a point)
	public static bool TryNum(string text, out float value) =>
		float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
		float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	public static float Deg(float radians) => radians * 180f / MathF.PI;
	public static float Rad(float degrees) => degrees * MathF.PI / 180f;
	private static float Ratio(float a, float b) => MathF.Abs(b) < 1e-6f ? a : a / b;

	// ======================= bones =======================

	public static bool IsAngle(BoneField f) => f is BoneField.Rotation or BoneField.Turn or BoneField.Tilt;

	// The pose has it (the rest are the bone's own: its length, its look-at's numbers)
	public static bool IsPose(BoneField f) =>
		f is BoneField.X or BoneField.Y or BoneField.Rotation or BoneField.ScaleX or BoneField.ScaleY or BoneField.Turn or BoneField.Tilt;

	// What the panel shows for the bone (null: it has no such number, like a look-at's without one)
	public float? BoneValue(Bone bone, BoneField field)
	{
		bool pose = State.AnimateMode && IsPose(field);
		var look = LookOf(bone);
		return field switch
		{
			BoneField.X => pose ? bone.Position.X : bone.SetupPosition.X,
			BoneField.Y => pose ? bone.Position.Y : bone.SetupPosition.Y,
			BoneField.Rotation => Deg(pose ? bone.Rotation : bone.SetupRotation),
			BoneField.Length => bone.Length,
			BoneField.ScaleX => pose ? bone.Scale.X : bone.SetupScale.X,
			BoneField.ScaleY => pose ? bone.Scale.Y : bone.SetupScale.Y,
			BoneField.Turn => Deg(pose ? bone.Turn : bone.SetupTurn),
			BoneField.Tilt => Deg(pose ? bone.Tilt : bone.SetupTilt),
			BoneField.Follow => look?.Follow,
			BoneField.RangeX => look?.Radius.X,
			BoneField.RangeY => look?.Radius.Y,
			_ => null
		};
	}

	// A number typed for the bone (as the panel shows it). keepParts: Build, the parts on it stay
	// where they are. False: nothing changed.
	public bool TypeBoneValue(Bone bone, BoneField field, float shown, bool keepParts)
	{
		if (BoneValue(bone, field) is not { } current || MathF.Abs(current - shown) < 1e-5f) return false;
		float v = IsAngle(field) ? Rad(shown) : shown;
		if (!(State.AnimateMode && IsPose(field))) return SetBoneRest(bone, field, v, keepParts);

		// Auto key off: the pose changes, no key yet
		if (Keys.HoldInsteadOfKey(() =>
		{
			switch (field)
			{
				case BoneField.X: bone.Position = new Vector2(v, bone.Position.Y); break;
				case BoneField.Y: bone.Position = new Vector2(bone.Position.X, v); break;
				case BoneField.Rotation: bone.Rotation = v; break;
				case BoneField.ScaleX: bone.Scale = new Vector2(v, bone.Scale.Y); break;
				case BoneField.ScaleY: bone.Scale = new Vector2(bone.Scale.X, v); break;
				case BoneField.Turn: bone.Turn = v; break;
				case BoneField.Tilt: bone.Tilt = v; break;
			}
		})) return true;
		return KeyBone(bone, field, v);
	}

	// ======================= parts =======================

	// The drawing the part shows now, and its picture
	public (Attachment? Drawing, Asset? Asset) PartShown(Slot? slot)
	{
		if (slot == null || Rig.ResolveAttachment(slot) is not { } drawing) return (null, null);
		return (drawing, Rig.Assets.TryGetValue(drawing.AssetId, out var asset) ? asset : null);
	}

	// Keyed in Animate (the pivot is part of the rest setup)
	public static bool IsPlacement(PartField f) => f is not (PartField.PivotX or PartField.PivotY);

	public float? PartValue(Slot slot, PartField field)
	{
		var (drawing, asset) = PartShown(slot);
		if (drawing == null) return null;
		bool pose = State.AnimateMode && IsPlacement(field);
		return field switch
		{
			PartField.X => drawing.Offset.X + (pose ? slot.Offset.X : 0f),
			PartField.Y => drawing.Offset.Y + (pose ? slot.Offset.Y : 0f),
			PartField.Rotation => Deg(drawing.Rotation + (pose ? slot.Rotation : 0f)),
			PartField.ScaleX => drawing.Scale.X * (pose ? slot.Scale.X : 1f),
			PartField.ScaleY => drawing.Scale.Y * (pose ? slot.Scale.Y : 1f),
			PartField.PivotX => asset != null ? drawing.Pivot.X * asset.Width : null,
			PartField.PivotY => asset != null ? drawing.Pivot.Y * asset.Height : null,
			_ => null
		};
	}

	// A number typed for the part (as the panel shows it). False: nothing changed.
	public bool TypePartValue(Slot slot, PartField field, float shown)
	{
		var (drawing, _) = PartShown(slot);
		if (drawing == null || PartValue(slot, field) is not { } current || MathF.Abs(current - shown) < 1e-5f) return false;
		float v = field == PartField.Rotation ? Rad(shown) : shown;
		if (!(State.AnimateMode && IsPlacement(field))) return SetPartRest(slot, field, v);

		// Auto key off: the placement changes, no key yet
		if (Keys.HoldInsteadOfKey(() =>
		{
			switch (field)
			{
				case PartField.X: slot.Offset = new Vector2(v - drawing.Offset.X, slot.Offset.Y); break;
				case PartField.Y: slot.Offset = new Vector2(slot.Offset.X, v - drawing.Offset.Y); break;
				case PartField.Rotation: slot.Rotation = v - drawing.Rotation; break;
				case PartField.ScaleX: slot.Scale = new Vector2(Ratio(v, drawing.Scale.X), slot.Scale.Y); break;
				case PartField.ScaleY: slot.Scale = new Vector2(slot.Scale.X, Ratio(v, drawing.Scale.Y)); break;
			}
		})) return true;
		return KeyPart(slot, field, v);
	}
}
