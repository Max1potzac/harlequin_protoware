using System.Numerics;
using System.Windows.Input;

namespace Animator.Viewer.Tools;

public class BoneTool
{
	private enum Drag { None, Move, Rotate, Pan, Create, PartMove, PartRotate, PartScale, PartPivot }

	private readonly EditorState _state;
	private readonly Camera _camera;
	private readonly SkeletonRenderer _renderer;

	private Drag _drag;
	private Vector2 _lastScreen;
	private bool _changed;

	// Bone drags
	private Vector2 _grabOffset;
	private float _lastAngle;
	private PoseSnapshot? _before;

	// Part drags: the attachment's values at the start, so every move recomputes from them
	private StructureSnapshot? _partBefore;
	private Attachment? _att;
	private int _attW, _attH;
	private Vector2 _attOffset, _attScale, _attPivot;
	private float _attRotation, _rotAccum;
	private Vector2 _startLocal;          // mouse at drag start, in the bone's space
	private Matrix3x2 _toImageAtStart;    // world → image pixels, at drag start

	private const float JointPickPx = 9f;
	private const float BonePickPx = 6f;
	private const float HandlePickPx = 8f;

	public (Vector2 A, Vector2 B)? CreatePreview { get; private set; }

	public BoneTool(EditorState state, Camera camera, SkeletonRenderer renderer)
	{
		_state = state;
		_camera = camera;
		_renderer = renderer;
	}

	// ======================= mouse =======================

	public void MouseDown(Vector2 screen, MouseButton button)
	{
		_lastScreen = screen;
		if (button == MouseButton.Middle) { _drag = Drag.Pan; return; }
		if (button != MouseButton.Left) return;

		var world = _camera.ScreenToWorld(screen);
		bool setup = !_state.AnimateMode;

		// Ctrl+drag draws a new bone
		if (setup && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			_drag = Drag.Create;
			CreatePreview = (world, world);
			return;
		}

		// 1. Handles of the selected part
		if (setup && _state.SelectedSlot is { } selected && TryPartHandle(selected, world)) return;

		// 2. Bones
		var hit = HitJoint(world);
		var mode = Drag.Move;
		if (hit == null) { hit = HitBone(world); mode = Drag.Rotate; }
		if (hit != null) { BeginBoneDrag(hit, mode, world); return; }

		// 3. Anywhere inside the selected part's box (even transparent pixels): move it
		if (setup && _state.SelectedSlot is { } current && InsidePart(current, world))
		{
			BeginPartDrag(current, Drag.PartMove, world);
			return;
		}

		// 4. Any part's visible pixels: select it (and start moving it, in Setup mode)
		if (_renderer.Pick(_state.Skeleton, world) is { } part)
		{
			_state.SelectedSlot = part.Slot;
			_state.Selected = part.Slot.Bone;
			if (setup) BeginPartDrag(part.Slot, Drag.PartMove, world);
			return;
		}

		// 5. Empty space: deselect and pan
		_state.Selected = null;
		_state.SelectedSlot = null;
		_drag = Drag.Pan;
	}

	public void MouseMove(Vector2 screen)
	{
		var world = _camera.ScreenToWorld(screen);
		var bone = _state.Selected;

		switch (_drag)
		{
			case Drag.Pan:
				var d = screen - _lastScreen;
				_camera.Pan -= new Vector2(d.X, -d.Y) / _camera.Zoom;
				break;

			case Drag.Create when CreatePreview is { } preview:
				CreatePreview = (preview.A, world);
				break;

			case Drag.Move when bone != null:
				{
					var local = bone.WorldToParentLocal(world + _grabOffset);
					if (!_state.AnimateMode) bone.SetupPosition += local - bone.Position;
					bone.Position = local;
					if (_state.AnimateMode) KeyTranslate(bone);
					_changed = true;
					break;
				}

			case Drag.Rotate when bone != null:
				{
					float angle = AngleFrom(bone, world);
					float step = MathUtil.ShortestAngle(angle - _lastAngle);
					_lastAngle = angle;
					if (!_state.AnimateMode) bone.SetupRotation += step;
					bone.Rotation += step;
					if (_state.AnimateMode) KeyRotate(bone);
					_changed = true;
					break;
				}

			case Drag.PartMove or Drag.PartRotate or Drag.PartScale or Drag.PartPivot:
				DragPart(world);
				break;
		}

		_lastScreen = screen;
	}

	public void MouseUp()
	{
		switch (_drag)
		{
			case Drag.Create:
				FinishCreate();
				break;
			case Drag.Move or Drag.Rotate:
				FinishBoneDrag();
				break;
			case Drag.PartMove or Drag.PartRotate or Drag.PartScale or Drag.PartPivot:
				FinishPartDrag();
				break;
		}
		_drag = Drag.None;
		_changed = false;
	}

	// ======================= bones =======================

	private void BeginBoneDrag(Bone bone, Drag mode, Vector2 world)
	{
		_state.Selected = bone;
		_state.SelectedSlot = null;
		_drag = mode;
		_grabOffset = bone.World.Translation - world;
		_lastAngle = AngleFrom(bone, world);

		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
		}

		_before = PoseSnapshot.Capture(bone, _state.Animation);
		_changed = false;
	}

	private void FinishBoneDrag()
	{
		if (_changed && _before != null && _state.Selected is { } bone)
		{
			var after = PoseSnapshot.Capture(bone, _state.Animation);
			string verb = _drag == Drag.Move ? "Move" : "Rotate";
			string where = _state.AnimateMode ? $" (key @ frame {_state.Frame})" : "";
			_state.Undo.Push(new SnapshotCommand<PoseSnapshot>(
				$"{verb} {bone.Name}{where}", _before, after, s => s.Restore()));
		}
		_before = null;
	}

	private void FinishCreate()
	{
		var preview = CreatePreview;
		CreatePreview = null;
		if (preview is not { } p || Vector2.Distance(p.A, p.B) * _camera.Zoom < 4) return;

		var parent = _state.Selected;
		_state.EditStructure(() =>
		{
			var bone = RigOps.AddBoneBetween(_state.Skeleton, parent, p.A, p.B);
			_state.Selected = bone;
			_state.SelectedSlot = null;
			return $"Create bone {bone.Name}";
		});
	}

	private void KeyTranslate(Bone b) =>
		_state.Animation.GetOrCreate(b.Name, n => new TranslateTimeline(n))
			  .Track.Add(_state.Time, b.Position - b.SetupPosition);

	private void KeyRotate(Bone b) =>
		_state.Animation.GetOrCreate(b.Name, n => new RotateTimeline(n))
			  .Track.Add(_state.Time, b.Rotation - b.SetupRotation);

	// ======================= parts =======================

	private bool TryPartHandle(Slot slot, Vector2 world)
	{
		if (PartGizmo.For(_state.Skeleton, slot, _camera.Zoom) is not { } h) return false;
		float r = HandlePickPx / _camera.Zoom;

		if (Vector2.Distance(world, h.Pivot) <= r) return BeginPartDrag(slot, Drag.PartPivot, world);
		if (Vector2.Distance(world, h.Rotate) <= r) return BeginPartDrag(slot, Drag.PartRotate, world);
		foreach (var corner in h.Corners)
			if (Vector2.Distance(world, corner) <= r) return BeginPartDrag(slot, Drag.PartScale, world);
		return false;
	}

	private bool InsidePart(Slot slot, Vector2 world)
	{
		var att = _state.Skeleton.ResolveAttachment(slot);
		if (att == null || !_state.Skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return false;
		if (!Matrix3x2.Invert(att.LocalMatrix(asset.Width, asset.Height) * slot.Bone.World, out var toImage))
			return false;
		var p = Vector2.Transform(world, toImage);
		return p.X >= 0 && p.Y >= 0 && p.X <= asset.Width && p.Y <= asset.Height;
	}

	private bool BeginPartDrag(Slot slot, Drag mode, Vector2 world)
	{
		var att = _state.Skeleton.ResolveAttachment(slot);
		if (att == null || !_state.Skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return false;
		if (!Matrix3x2.Invert(slot.Bone.World, out var toBone)) return false;   // bone squashed flat
		if (!Matrix3x2.Invert(att.LocalMatrix(asset.Width, asset.Height) * slot.Bone.World, out _toImageAtStart))
			return false;

		_drag = mode;
		_att = att;
		_attW = asset.Width;
		_attH = asset.Height;
		_attOffset = att.Offset;
		_attRotation = att.Rotation;
		_attScale = att.Scale;
		_attPivot = att.Pivot;
		_startLocal = Vector2.Transform(world, toBone);
		_lastAngle = Angle(_startLocal - _attOffset);
		_rotAccum = 0;

		_partBefore = _state.CaptureStructure();
		_changed = false;
		_state.SelectedSlot = slot;
		_state.Selected = slot.Bone;
		return true;
	}

	private void DragPart(Vector2 world)
	{
		if (_att == null || _state.SelectedSlot is not { } slot) return;
		if (!Matrix3x2.Invert(slot.Bone.World, out var toBone)) return;
		var local = Vector2.Transform(world, toBone);
		bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

		switch (_drag)
		{
			case Drag.PartMove:
				_att.Offset = _attOffset + (local - _startLocal);
				break;

			case Drag.PartRotate:
				{
					// The pivot sits exactly at Offset, so rotate around that
					float angle = Angle(local - _attOffset);
					_rotAccum += MathUtil.ShortestAngle(angle - _lastAngle);
					_lastAngle = angle;
					float r = _attRotation + _rotAccum;
					if (shift) r = MathF.Round(r / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps
					_att.Rotation = r;
					break;
				}

			case Drag.PartScale:
				{
					// Compare mouse positions in the part's own unrotated frame, relative to the pivot
					var v0 = Rotate(_startLocal - _attOffset, -_attRotation);
					var v = Rotate(local - _attOffset, -_attRotation);
					if (shift)
					{
						float f = Vector2.Dot(v, v0) / MathF.Max(Vector2.Dot(v0, v0), 1e-6f);
						_att.Scale = _attScale * f;
					}
					else
					{
						_att.Scale = new Vector2(
							MathF.Abs(v0.X) > 0.5f ? _attScale.X * v.X / v0.X : _attScale.X,
							MathF.Abs(v0.Y) > 0.5f ? _attScale.Y * v.Y / v0.Y : _attScale.Y);
					}
					break;
				}

			case Drag.PartPivot:
				{
					var pixel = Vector2.Transform(world, _toImageAtStart);
					if (shift) pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
					_att.Offset = _attOffset;   // start from the original placement every time
					_att.Pivot = _attPivot;
					PartOps.SetPivotKeepingPlace(_att, _attW, _attH, pixel);
					break;
				}
		}
		_changed = true;
	}

	private void FinishPartDrag()
	{
		if (_changed && _partBefore != null && _state.SelectedSlot is { } slot)
		{
			string verb = _drag switch
			{
				Drag.PartMove => "Move",
				Drag.PartRotate => "Rotate",
				Drag.PartScale => "Scale",
				_ => "Set pivot of"
			};
			_state.PushStructure(_partBefore, $"{verb} part {slot.Name}");
		}
		_partBefore = null;
		_att = null;
	}

	// ======================= hit testing & math =======================

	private Bone? HitJoint(Vector2 world)
	{
		float bestDist = JointPickPx / _camera.Zoom;
		Bone? best = null;
		foreach (var b in _state.Skeleton.AllBones())
		{
			float d = Vector2.Distance(world, b.World.Translation);
			if (d <= bestDist) { best = b; bestDist = d; }
		}
		return best;
	}

	private Bone? HitBone(Vector2 world)
	{
		float bestDist = BonePickPx / _camera.Zoom;
		Bone? best = null;
		foreach (var b in _state.Skeleton.AllBones())
		{
			float d = DistanceToSegment(world, b.World.Translation, b.TipWorld);
			if (d <= bestDist) { best = b; bestDist = d; }
		}
		return best;
	}

	private static float AngleFrom(Bone bone, Vector2 world) => Angle(world - bone.World.Translation);

	private static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

	private static Vector2 Rotate(Vector2 v, float angle)
	{
		float c = MathF.Cos(angle), s = MathF.Sin(angle);
		return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
	}

	private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		float lenSq = ab.LengthSquared();
		if (lenSq < 1e-6f) return Vector2.Distance(p, a);
		float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
		return Vector2.Distance(p, a + ab * t);
	}
}