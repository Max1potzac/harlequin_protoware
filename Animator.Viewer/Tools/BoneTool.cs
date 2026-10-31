using System.Numerics;
using System.Windows.Input;

namespace Animator.Viewer.Tools;

// The viewport inside an object. The select button picks bones and parts: a click (Ctrl: picked
// or not, the other way round; Shift: picks it too), or a box dragged over them (Shift: picks
// them too; Ctrl: lets them go). The transform button drags them: a joint moves its bone, the bone
// itself turns it, a part's box moves it and its handles turn and scale it; Ctrl+drag draws a new
// bone, Alt+click puts a part on the selected bone. A drag on something picked takes everything
// picked along. In Animate mode a drag keys the playhead, or, where keys of what it drags are
// selected in the timeline, shifts those keys alike (the animation plays as it did, moved).
// The pan button moves the view (so does the transform button on empty space).
public class BoneTool
{
	private enum Drag { None, Select, Move, Rotate, Tip, Pan, Create, PartMove, PartRotate, PartScale, PartPivot, Many }
	private enum Gesture { Move, Turn, Scale }

	private readonly EditorState _state;
	private readonly Camera _camera;
	private readonly SkeletonRenderer _renderer;

	private Drag _drag;
	private Vector2 _lastScreen;
	private bool _changed;

	// Which mouse button does what (the settings can change them)
	public MouseButton SelectButton { get; set; } = MouseButton.Left;
	public MouseButton TransformButton { get; set; } = MouseButton.Right;
	public MouseButton PanButton { get; set; } = MouseButton.Middle;

	// The keys held down (Ctrl, Shift, Alt): the keyboard's, or a test's
	public Func<ModifierKeys> Modifiers { get; set; } = () => Keyboard.Modifiers;
	private bool Held(ModifierKeys key) => Modifiers().HasFlag(key);

	// A box being dragged with the select button: its corners in the world, for drawing
	public Vector2[]? SelectionBox { get; private set; }

	// Snapping to the grid and guides (joints, bone ends, new bones, parts, several at once)
	public Snapping Snap { get; set; } = new();
	private Vector2 Snapped(Vector2 world) => Snap.Snap(world, _camera.EditZoom);

	// Bone drags
	private Vector2 _grabOffset;
	private float _lastAngle;
	private PoseSnapshot? _before;
	private Vector2 _poseAtStart;           // Animate mode: where the bone was at this frame
	private float _turnAtStart, _turned;    // ...and how far it's been turned since (in 15° steps, with Shift)
	private float _rotationAtStart, _turnedFreely;   // its rotation at the start, and the mouse's turn without steps
	private KeyShift<Vector2>? _moveKeys;   // ...and its selected keys, if any
	private KeyShift<float>? _turnKeys;

	// Part drags: where the part is at the start (its drawing's placement plus any placement
	// keys at this frame), so every mouse move recomputes from it
	private StructureSnapshot? _partBefore;    // Setup mode: the drawing's placement changes
	private AnimationSnapshot? _keysBefore;    // Animate mode: placement keys change
	private bool _keyPart;
	private Slot? _partSlot;
	private Attachment? _att;
	private int _attW, _attH;
	private Vector2 _attOffset, _attScale, _attPivot;
	private float _attRotation, _rotAccum;
	private Vector2 _startLocal;          // mouse at drag start, in the bone's space
	private Matrix3x2 _toImageAtStart;    // world → image pixels, at drag start
	private Vector2 _pressScreen;         // a part only starts moving once the mouse really moves
	private bool _partMoving;
	private Vector2 _slotOffset0, _slotScale0;   // Animate mode: the part's placement keys at this frame
	private Vector2[] _partSnapPoints = Array.Empty<Vector2>();   // its pivot and corners at the start (world), for snapping
	private Vector2 _partPressWorld;
	private float _slotRotation0;
	private KeyShift<Vector2>? _partMoveKeys, _partScaleKeys;
	private KeyShift<float>? _partTurnKeys;
	private const float PartDragStartPx = 3f;

	private const float JointPickPx = 9f;
	private const float BonePickPx = 6f;
	private const float HandlePickPx = 8f;
	private const float SnapPx = 12f;

	public (Vector2 A, Vector2 B)? CreatePreview { get; private set; }

	// "Snap to ends": a new bone started near the selected bone's end, or a joint dragged near its
	// parent's end, lands exactly on it and stays joined (Bone.Connected). Pulled further away, it
	// lets go. In Animate mode, a joined bone dragged back near the end loses its offset.
	public bool SnapJoints { get; set; } = true;

	// Off inside a drawn object: its one bone is hidden, so only parts can be picked and moved
	public bool BonesEnabled { get; set; } = true;

	// "Parts stay put" (Setup mode): moving or turning a bone leaves every part, and every other
	// joint, where it is, so bones can be fitted to finished art. Off: the parts move with the bone.
	public bool KeepPartsInPlace { get; set; } = true;
	private FrozenRest? _frozen;                  // where everything was when the drag started
	private StructureSnapshot? _rigBefore;        // for undo: the drag changes more than the one bone
	private Vector2 _pressWorld;

	// The ring at the selected bone's end (Setup mode, when no child sits on that end, and it's the
	// only thing picked): dragging it says where the bone ends
	public Bone? TipHandle =>
		!_state.AnimateMode && BonesEnabled && _state.SelectedSlot == null && _state.Selected is { Locked: false } bone &&
		RestEdits.HasFreeEnd(bone) && _state.Picked.Count <= 1 ? bone : null;

	private bool _createJoined;                              // the bone being drawn starts on the parent's end
	private Vector2 _setupAtStart, _jointAtStart, _poseOffset;   // a joint drag in Setup mode

	// Dragging several picked things at once
	private sealed class Mover
	{
		public Bone? Bone;
		public Slot? Slot;
		// A part's drawings, all of them, with their placement at the start (Setup mode moves
		// those: the one shown now, and the ones shown at other frames, or nowhere just now)
		public List<(Attachment Drawing, Vector2 Offset, float Rotation, Vector2 Scale)> Drawings = new();
		public Vector2 Position, Scale;    // at the start: the bone's rest values (Setup) or this frame's (Animate)
		public float Rotation;
		public Matrix3x2 ToLocal;          // world → the space its position is in (turns and scales only)
		public float Mirror = 1;           // -1 where that space is mirrored: turns go the other way
		public KeyShift<Vector2>? MoveKeys, ScaleKeys;
		public KeyShift<float>? TurnKeys;
	}

	private readonly List<Mover> _movers = new();
	private Gesture _gesture;
	private Vector2 _manyPress, _manyCentre;      // world: where the drag started, and what it turns round
	private IReadOnlyList<Vector2> _manySnapPoints = Array.Empty<Vector2>();   // what's grabbed, for snapping
	private float _manyLastAngle, _manyTurned;
	private Matrix3x2 _scaleToBone;               // scaling: the grabbed part's bone space, at the start
	private Vector2 _scaleOffset;
	private float _scaleRotation;
	private StructureSnapshot? _manyRestBefore;
	private AnimationSnapshot? _manyKeysBefore;
	private FrozenRest? _manyFrozen;
	private List<Bone> _pickedBones = new();
	private List<Slot> _pickedParts = new();

	// Auto key off: a drag in Animate mode sets keys while it goes on (so the pose follows), and
	// when it ends they're taken back and what changed is held instead (see PoseHold). What was
	// held already becomes keys for the drag too, so everything lines up while it lasts.
	private sealed record Holding(AnimationSnapshot Keys, PoseValues Before);
	private Holding? _holding;

	// Keys a drag may shift along (Animate mode, keys selected): none while holding, where drags
	// leave every key alone
	private IReadOnlySet<(Timeline Tl, int Frame)> ShiftableKeys => _holding != null ? NoKeys : _state.SelectedKeys;
	private static readonly HashSet<(Timeline Tl, int Frame)> NoKeys = new();

	private void BeginHolding()
	{
		var animation = _state.Animation;
		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		_holding = new Holding(AnimationSnapshot.Capture(animation), PoseValues.Capture(_state.Skeleton));
		_state.Hold.KeyInto(animation, _state.Time);
		_state.Hold.Suspended = true;
	}

	// The drag's keys taken back, what it changed held. true: it was holding.
	private bool EndHolding()
	{
		if (_holding is not { } holding) return false;
		_holding = null;
		var after = PoseValues.Capture(_state.Skeleton);
		holding.Keys.Restore();
		_state.Hold.Suspended = false;
		_state.Hold.Take(holding.Before, after, _state.Skeleton, _state.Animation, _state.Frame);
		_changed = false;   // nothing for the undo history: no key changed
		return true;
	}

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
		_pressScreen = screen;
		_partMoving = false;
		SelectionBox = null;
		if (button == PanButton) { _drag = Drag.Pan; return; }
		if (button == SelectButton) { _drag = Drag.Select; return; }
		if (button != TransformButton) return;

		var world = _camera.ScreenToWorld(screen);
		bool setup = !_state.AnimateMode;
		if (!setup && !_state.AutoKey) BeginHolding();

		// Ctrl+drag draws a new bone (a child of the selected one). Started near the selected
		// bone's end, it starts exactly there.
		if (setup && BonesEnabled && Held(ModifierKeys.Control))
		{
			_drag = Drag.Create;
			var parent = _state.Selected;
			_createJoined = SnapJoints && parent != null && NearEnd(parent, world);
			CreatePreview = (_createJoined ? parent!.TipWorld : Snapped(world), Snapped(world));
			return;
		}

		// Alt+click a part: it goes on the selected bone, staying where it is
		if (setup && BonesEnabled && Held(ModifierKeys.Alt) && _state.SelectedSlot == null &&
			_state.Selected is { } holder && _renderer.Pick(_state.Skeleton, world) is { } clicked)
		{
			var slot = clicked.Slot;
			_state.EditStructure(() =>
				PartOps.MoveToBone(_state.Skeleton, slot, holder) ? $"Put part {slot.Name} on {holder.Name}" : null);
			_drag = Drag.None;
			return;
		}

		bool many = _state.Picked.Count > 1;

		// 1. Handles of the selected part (in Animate mode, dragging one sets placement keys)
		if (_state.SelectedSlot is { } selected && TryPartHandle(selected, world, many)) return;

		// 2. The ring at the selected bone's end (nearer it than the joint, for a bone with no length)
		if (!many && TipHandle is { } ending && Vector2.Distance(world, ending.TipWorld) <= HandlePickPx / _camera.EditZoom &&
			Vector2.Distance(world, ending.TipWorld) < Vector2.Distance(world, ending.World.Translation))
		{
			BeginTipDrag(ending, world);
			return;
		}

		// 3. Bones: a picked one takes everything picked along
		if (BonesEnabled)
		{
			var hit = HitJoint(world);
			var mode = Drag.Move;
			if (hit == null) { hit = HitBone(world); mode = Drag.Rotate; }
			if (hit != null)
			{
				if (many && _state.IsPicked(hit))
					BeginMany(mode == Drag.Move ? Gesture.Move : Gesture.Turn, world, hit.World.Translation, snapPoints: new[] { hit.World.Translation });
				else BeginBoneDrag(hit, mode, world);
				return;
			}
		}

		// 4. Anywhere inside the selected part's box (even transparent pixels): move it
		if (_state.SelectedSlot is { Locked: false } current && InsidePart(current, world))
		{
			if (many) BeginMany(Gesture.Move, world, world, snapPoints: SnapPointsOf(current));
			else BeginPartDrag(current, Drag.PartMove, world);
			return;
		}

		// 5. Any part's visible pixels: move it (a part that isn't picked gets picked on its own)
		if (_renderer.Pick(_state.Skeleton, world) is { } part)
		{
			if (many && _state.IsPicked(part.Slot))
			{
				BeginMany(Gesture.Move, world, world, snapPoints: SnapPointsOf(part.Slot));
				return;
			}
			_state.Pick(new object[] { part.Slot });
			BeginPartDrag(part.Slot, Drag.PartMove, world);
			return;
		}

		// 6. Empty space: pan
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
				_camera.Pan -= new Vector2(d.X, -d.Y) / _camera.Zoom;   // the view pans in the scene
				break;

			case Drag.Select:
				if (SelectionBox == null && Vector2.Distance(screen, _pressScreen) < PartDragStartPx) break;   // a click, so far
				SelectionBox = new[]
				{
					_camera.ScreenToWorld(_pressScreen), _camera.ScreenToWorld(new Vector2(screen.X, _pressScreen.Y)),
					world, _camera.ScreenToWorld(new Vector2(_pressScreen.X, screen.Y))
				};
				break;

			case Drag.Create when CreatePreview is { } preview:
				CreatePreview = (preview.A, Snapped(world));
				break;

			case Drag.Move when bone != null && _frozen != null:
				// Parts stay put: the joint goes where the mouse took it, nothing else moves
				RestEdits.MoveJoint(_frozen, bone, Snapped(_frozen.HeadOf(bone) + (world - _pressWorld)),
					SnapJoints ? SnapPx / _camera.EditZoom : 0f);
				_changed = true;
				break;

			case Drag.Move when bone != null:
				{
					var target = Snapped(world + _grabOffset);
					if (_state.AnimateMode) KeyJoint(bone, target);
					else MoveRestJoint(bone, target);
					_changed = true;
					break;
				}

			case Drag.Tip when bone != null:
				{
					// Where the bone ends: with parts staying put, snapping onto a child's joint
					var target = Snapped(world + _grabOffset);
					if (_frozen != null) RestEdits.MoveTip(_frozen, bone, target, SnapJoints ? SnapPx / _camera.EditZoom : 0f);
					else FrozenRest.Aim(bone, bone.World.Translation, target);   // what's on it turns with it
					_changed = true;
					break;
				}

			case Drag.Rotate when bone != null:
				{
					float angle = AngleFrom(bone, world);
					_turnedFreely += MathUtil.ShortestAngle(angle - _lastAngle);
					_lastAngle = angle;
					// Shift: the bone's angle in 15° steps
					float turn = Held(ModifierKeys.Shift)
						? Snapping.StepAngle(_rotationAtStart + _turnedFreely) - _rotationAtStart
						: _turnedFreely;
					float step = turn - _turned;
					_turned = turn;
					if (_frozen != null)
					{
						RestEdits.Turn(_frozen, bone, _rotationAtStart + turn);   // what's on it stays put
					}
					else
					{
						if (!_state.AnimateMode) bone.SetupRotation += step;
						bone.Rotation += step;
						if (_state.AnimateMode) KeyRotate(bone);
					}
					_changed = true;
					break;
				}

			case Drag.PartMove or Drag.PartRotate or Drag.PartScale or Drag.PartPivot:
				// A click that only selects a part shouldn't nudge it
				if (!_partMoving && Vector2.Distance(screen, _pressScreen) < PartDragStartPx) break;
				_partMoving = true;
				DragPart(world);
				break;

			case Drag.Many:
				if (!_partMoving && Vector2.Distance(screen, _pressScreen) < PartDragStartPx) break;
				_partMoving = true;
				DragMany(world);
				break;
		}

		_lastScreen = screen;
	}

	public void MouseUp()
	{
		EndHolding();
		switch (_drag)
		{
			case Drag.Select:
				if (SelectionBox is { } box) BoxSelect(box);
				else ClickSelect(_camera.ScreenToWorld(_pressScreen));
				SelectionBox = null;
				break;
			case Drag.Create:
				FinishCreate();
				break;
			case Drag.Move or Drag.Rotate:
				FinishBoneDrag();
				break;
			case Drag.Tip:
				FinishTipDrag();
				break;
			case Drag.PartMove or Drag.PartRotate or Drag.PartScale or Drag.PartPivot:
				FinishPartDrag();
				break;
			case Drag.Many:
				FinishMany();
				break;
		}
		_drag = Drag.None;
		_changed = false;
	}

	// ======================= picking =======================

	// What a click picks: a bone (its joint, then the bone itself), else a part's visible pixels
	public object? Hit(Vector2 world)
	{
		if (BonesEnabled && (HitJoint(world) ?? HitBone(world)) is { } bone) return bone;
		return _renderer.Pick(_state.Skeleton, world)?.Slot;
	}

	private void ClickSelect(Vector2 world)
	{
		var hit = Hit(world);
		if (Held(ModifierKeys.Control))
		{
			if (hit != null) _state.TogglePick(hit);
		}
		else if (Held(ModifierKeys.Shift))
		{
			if (hit != null) _state.PickMore(new[] { hit });
		}
		else if (hit != null) _state.Pick(new[] { hit });
		else _state.ClearPicks();
	}

	// Everything the box covers: bones whose joint or end is in it, parts whose middle is
	public IReadOnlyList<object> InBox(IReadOnlyList<Vector2> box)
	{
		var found = new List<object>();
		var skeleton = _state.Skeleton;
		if (BonesEnabled)
			foreach (var bone in skeleton.AllBones())
				if (!bone.IsHidden && !bone.Locked && (Inside(box, bone.World.Translation) || Inside(box, bone.TipWorld))) found.Add(bone);
		foreach (var slot in skeleton.DrawOrder)
		{
			if (slot.IsHidden || slot.Locked || skeleton.ResolveAttachment(slot) is not { } att || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			var middle = Vector2.Transform(new Vector2(asset.Width / 2f, asset.Height / 2f), SkeletonRenderer.ImageToWorld(slot, att, asset.Width, asset.Height));
			if (Inside(box, middle)) found.Add(slot);
		}
		return found;
	}

	private void BoxSelect(IReadOnlyList<Vector2> box)
	{
		var found = InBox(box);
		if (Held(ModifierKeys.Control)) _state.Unpick(found);
		else if (Held(ModifierKeys.Shift)) _state.PickMore(found);
		else _state.Pick(found);
	}

	// Inside a convex four-cornered box (its corners in order, either way round)
	private static bool Inside(IReadOnlyList<Vector2> box, Vector2 p)
	{
		int sign = 0;
		for (int i = 0; i < box.Count; i++)
		{
			var a = box[i];
			var b = box[(i + 1) % box.Count];
			float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
			if (MathF.Abs(cross) < 1e-9f) continue;
			int s = Math.Sign(cross);
			if (sign != 0 && s != sign) return false;
			sign = s;
		}
		return true;
	}

	// ======================= bones =======================

	private void BeginBoneDrag(Bone bone, Drag mode, Vector2 world)
	{
		_state.Pick(new object[] { bone });
		_drag = mode;
		_grabOffset = bone.World.Translation - world;
		_lastAngle = AngleFrom(bone, world);
		_setupAtStart = bone.SetupPosition;
		_jointAtStart = ToParentSpace(bone, bone.World.Translation);
		_poseOffset = bone.Position - bone.SetupPosition;   // what the animation adds at this frame
		_moveKeys = null;
		_turnKeys = null;
		_turned = _turnedFreely = 0;
		_rotationAtStart = _state.AnimateMode ? bone.Rotation : bone.SetupRotation;

		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			_poseAtStart = bone.Position;
			_turnAtStart = bone.Rotation;
			_moveKeys = KeyShift<Vector2>.Of(_state.Animation.Find<TranslateTimeline>(bone.Name), ShiftableKeys, _state.Fps);
			_turnKeys = KeyShift<float>.Of(_state.Animation.Find<RotateTimeline>(bone.Name), ShiftableKeys, _state.Fps);
		}

		_before = PoseSnapshot.Capture(bone, _state.Animation);
		_changed = false;

		// Parts stay put (Setup mode): the drag works on the rest pose, keeping everything else
		// where it is, so it's undone as a change of the whole rig
		_frozen = null;
		_rigBefore = null;
		if (!_state.AnimateMode && KeepPartsInPlace)
		{
			_rigBefore = _state.CaptureStructure();
			_pressWorld = world;
			_frozen = FrozenRest.Capture(_state.Skeleton);   // puts the rig in its rest pose
			_lastAngle = AngleFrom(bone, world);             // measured from the rest joint, then
		}
	}

	private void FinishBoneDrag()
	{
		if (_changed && _state.Selected is { } bone)
		{
			string verb = _drag == Drag.Move ? "Move" : "Rotate";
			if (_rigBefore != null)
			{
				_state.PushStructure(_rigBefore, $"{verb} {bone.Name} (parts stay put)");
			}
			else if (_before != null)
			{
				var after = PoseSnapshot.Capture(bone, _state.Animation);
				string where = !_state.AnimateMode ? "" : _moveKeys != null || _turnKeys != null ? " (selected keys)" : $" (key @ frame {_state.Frame})";
				_state.Undo.Push(new SnapshotCommand<PoseSnapshot>(
					$"{verb} {bone.Name}{where}", _before, after, s => s.Restore()));
			}
		}
		_before = null;
		_rigBefore = null;
		_frozen = null;
		_moveKeys = null;
		_turnKeys = null;
	}

	// Setup mode: the end of a bone no child sits on, dragged to where it should end
	private void BeginTipDrag(Bone bone, Vector2 world)
	{
		_drag = Drag.Tip;
		_changed = false;
		_grabOffset = bone.TipWorld - world;
		_rigBefore = _state.CaptureStructure();
		_frozen = KeepPartsInPlace ? FrozenRest.Capture(_state.Skeleton) : null;
	}

	private void FinishTipDrag()
	{
		if (_changed && _rigBefore != null && _state.Selected is { } bone)
			_state.PushStructure(_rigBefore, $"Set where {bone.Name} ends" + (_frozen != null ? " (parts stay put)" : ""));
		_rigBefore = null;
		_frozen = null;
	}

	private void FinishCreate()
	{
		var preview = CreatePreview;
		CreatePreview = null;
		if (preview is not { } p || Vector2.Distance(p.A, p.B) * _camera.EditZoom < 4) return;

		var parent = _state.Selected;
		bool joined = _createJoined && parent != null;
		_state.EditStructure(() =>
		{
			var bone = RigOps.AddBoneBetween(_state.Skeleton, parent, p.A, p.B);
			if (joined) bone.Connected = true;   // exactly on the parent's end, and it stays there
			_state.Pick(new object[] { bone });
			return $"Create bone {bone.Name}";
		});
	}

	// Setup mode: the rest position follows the mouse. The rest pose is always measured from the
	// parent, even when the animation has this bone detached or held at the current frame.
	// With snapping on, near the parent's end the joint snaps onto it and stays joined.
	private void MoveRestJoint(Bone bone, Vector2 target)
	{
		var free = _setupAtStart + ToParentSpace(bone, target) - _jointAtStart;   // where the mouse puts it
		var parent = bone.Parent;
		if (SnapJoints && parent != null && NearEnd(parent, Vector2.Transform(free, parent.World)))
		{
			bone.Connected = true;
		}
		else
		{
			bone.Connected = false;
			bone.SetupPosition = free;
		}

		// Show it right away: more mouse moves can arrive before the next frame poses the rig
		bone.Position = bone.SpaceOverridden ? bone.WorldToParentLocal(target) : bone.SetupPosition + _poseOffset;
		bone.UpdateWorld();
	}

	// Animate mode: a translate key at this frame (or the selected keys shifted by as much). A
	// bone joined to its parent's end that's dragged back near the end sits exactly on it again,
	// with no offset left.
	private void KeyJoint(Bone bone, Vector2 target)
	{
		bone.Position = bone.WorldToParentLocal(target);
		if (SnapJoints && bone.Connected && !bone.SpaceOverridden && bone.Parent is { } parent && NearEnd(parent, target))
			bone.Position = bone.SetupPosition;
		bone.UpdateWorld();   // keep World current: more mouse moves can arrive before the next frame
		if (_moveKeys != null)
		{
			var moved = bone.Position - _poseAtStart;
			_moveKeys.Apply(v => v + moved);
		}
		else KeyTranslate(bone);
	}

	private bool NearEnd(Bone parent, Vector2 world) =>
		Vector2.Distance(world, parent.TipWorld) <= SnapPx / _camera.EditZoom;

	private void KeyTranslate(Bone b) =>
		_state.Animation.GetOrCreate(b.Name, n => new TranslateTimeline(n))
			  .Track.Add(_state.Time, b.Position - b.SetupPosition);

	private void KeyRotate(Bone b)
	{
		if (_turnKeys != null)
		{
			// Counted from the start: the pose here may not follow those keys (they can be elsewhere)
			float turned = _turned;
			_turnKeys.Apply(v => v + turned);
			b.Rotation = _turnAtStart + turned;
			b.UpdateWorld();
			return;
		}
		_state.Animation.GetOrCreate(b.Name, n => new RotateTimeline(n))
			  .Track.Add(_state.Time, b.Rotation - b.SetupRotation);
	}

	// ======================= parts =======================

	private bool TryPartHandle(Slot slot, Vector2 world, bool many)
	{
		if (slot.Locked || PartGizmo.For(_state.Skeleton, slot, _camera.EditZoom) is not { } h) return false;
		float r = HandlePickPx / _camera.EditZoom;

		// The pivot belongs to the rest setup, so it can only be moved in Setup mode (one part's)
		if (!_state.AnimateMode && !many && Vector2.Distance(world, h.Pivot) <= r) return BeginPartDrag(slot, Drag.PartPivot, world);
		if (Vector2.Distance(world, h.Rotate) <= r)
		{
			if (!many) return BeginPartDrag(slot, Drag.PartRotate, world);
			BeginMany(Gesture.Turn, world, h.Pivot);
			return true;
		}
		foreach (var corner in h.Corners)
			if (Vector2.Distance(world, corner) <= r)
			{
				if (!many) return BeginPartDrag(slot, Drag.PartScale, world);
				BeginMany(Gesture.Scale, world, h.Pivot, slot);
				return true;
			}
		return false;
	}

	// A part's pivot and the corners of its box (world), for snapping; none when it shows nothing
	private Vector2[] SnapPointsOf(Slot slot) =>
		PartGizmo.For(_state.Skeleton, slot, _camera.EditZoom) is { } h ? h.Corners.Append(h.Pivot).ToArray() : Array.Empty<Vector2>();

	// Inside the part's rectangle (see-through pixels too), at the drawing it shows now
	public bool InsidePart(Slot slot, Vector2 world)
	{
		var att = _state.Skeleton.ResolveAttachment(slot);
		if (att == null || !_state.Skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return false;
		if (!Matrix3x2.Invert(slot.ImageToBone(att, asset.Width, asset.Height) * slot.Bone.World, out var toImage))
			return false;
		var p = Vector2.Transform(world, toImage);
		return p.X >= 0 && p.Y >= 0 && p.X <= asset.Width && p.Y <= asset.Height;
	}

	private bool BeginPartDrag(Slot slot, Drag mode, Vector2 world)
	{
		var att = _state.Skeleton.ResolveAttachment(slot);
		if (att == null || !_state.Skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return false;
		if (!Matrix3x2.Invert(slot.Bone.World, out var toBone)) return false;   // bone squashed flat
		if (!Matrix3x2.Invert(slot.ImageToBone(att, asset.Width, asset.Height) * slot.Bone.World, out _toImageAtStart))
			return false;

		_keyPart = _state.AnimateMode;
		_drag = mode;
		_att = att;
		_partSlot = slot;
		_attW = asset.Width;
		_attH = asset.Height;
		_attOffset = att.Offset + slot.Offset;   // where the pivot is now, in the bone's space
		_attRotation = att.Rotation + slot.Rotation;
		_attScale = att.Scale * slot.Scale;
		_attPivot = att.Pivot;
		_startLocal = Vector2.Transform(world, toBone);
		_lastAngle = Angle(_startLocal - _attOffset);
		_rotAccum = 0;
		_partMoveKeys = _partScaleKeys = null;
		_partTurnKeys = null;
		_partSnapPoints = SnapPointsOf(slot);
		_partPressWorld = world;

		if (_keyPart)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);   // keys land exactly on a frame
			_keysBefore = AnimationSnapshot.Capture(_state.Animation);
			_slotOffset0 = slot.Offset;
			_slotRotation0 = slot.Rotation;
			_slotScale0 = slot.Scale;
			var animation = _state.Animation;
			_partMoveKeys = KeyShift<Vector2>.Of(animation.Find<PartTranslateTimeline>(slot.Name), ShiftableKeys, _state.Fps);
			_partTurnKeys = KeyShift<float>.Of(animation.Find<PartRotateTimeline>(slot.Name), ShiftableKeys, _state.Fps);
			_partScaleKeys = KeyShift<Vector2>.Of(animation.Find<PartScaleTimeline>(slot.Name), ShiftableKeys, _state.Fps);
		}
		else
		{
			_partBefore = _state.CaptureStructure();
		}

		_changed = false;
		if (!_state.IsPicked(slot)) _state.Pick(new object[] { slot });
		else
		{
			_state.SelectedSlot = slot;
			_state.Selected = slot.Bone;
		}
		return true;
	}

	private void DragPart(Vector2 world)
	{
		if (_att == null || _partSlot is not { } slot) return;
		if (!Matrix3x2.Invert(slot.Bone.World, out var toBone)) return;
		var local = Vector2.Transform(world, toBone);
		bool shift = Held(ModifierKeys.Shift);

		switch (_drag)
		{
			case Drag.PartMove:
				{
					// Its pivot or a corner of its box near a grid line or guide lands on it
					var moved = world + Snap.Offset(_partSnapPoints.Select(p => p + (world - _partPressWorld)).ToList(), _camera.EditZoom);
					PlacePart(offset: _attOffset + (Vector2.Transform(moved, toBone) - _startLocal));
					break;
				}

			case Drag.PartRotate:
				{
					// The pivot sits exactly at Offset, so rotate around that
					float angle = Angle(local - _attOffset);
					_rotAccum += MathUtil.ShortestAngle(angle - _lastAngle);
					_lastAngle = angle;
					float r = _attRotation + _rotAccum;
					if (shift) r = MathF.Round(r / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps
					PlacePart(rotation: r);
					break;
				}

			case Drag.PartScale:
				PlacePart(scale: _attScale * ScaleFactor(_startLocal, local, _attOffset, _attRotation, shift));
				break;

			case Drag.PartPivot when !_keyPart:
				{
					var pixel = Vector2.Transform(Snapped(world), _toImageAtStart);
					if (shift) pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
					_att.Offset = _attOffset - slot.Offset;   // start from the original placement every time
					_att.Pivot = _attPivot;
					PartOps.SetPivotKeepingPlace(_att, _attW, _attH, pixel);
					break;
				}
		}
		_changed = true;
	}

	// How much a corner dragged from `from` to `to` (bone space) scales a part whose pivot is at
	// `pivot`, turned by `rotation`: each way on its own, or (Shift) both alike
	private static Vector2 ScaleFactor(Vector2 from, Vector2 to, Vector2 pivot, float rotation, bool alike)
	{
		var v0 = Rotate(from - pivot, -rotation);
		var v = Rotate(to - pivot, -rotation);
		if (alike)
		{
			float f = Vector2.Dot(v, v0) / MathF.Max(Vector2.Dot(v0, v0), 1e-6f);
			return new Vector2(f, f);
		}
		return new Vector2(MathF.Abs(v0.X) > 0.5f ? v.X / v0.X : 1f, MathF.Abs(v0.Y) > 0.5f ? v.Y / v0.Y : 1f);
	}

	// Where the part should be (its pivot's position, rotation and scale on the bone).
	// Setup mode moves the drawing itself; Animate mode keys the part's placement at this frame,
	// or shifts its selected placement keys by as much.
	private void PlacePart(Vector2? offset = null, float? rotation = null, Vector2? scale = null)
	{
		if (_att == null || _partSlot is not { } slot) return;

		if (!_keyPart)
		{
			if (offset is { } o) _att.Offset = o - slot.Offset;
			if (rotation is { } r) _att.Rotation = r - slot.Rotation;
			if (scale is { } sc) _att.Scale = new Vector2(Ratio(sc.X, slot.Scale.X), Ratio(sc.Y, slot.Scale.Y));
			return;
		}

		// Keys are what's added on top of the drawing's own placement. Setting the slot too
		// shows the change right away, before the next frame poses the rig from the keys.
		var animation = _state.Animation;
		float time = _state.Time;
		if (offset is { } off)
		{
			slot.Offset = off - _att.Offset;
			var moved = slot.Offset - _slotOffset0;
			if (_partMoveKeys != null) _partMoveKeys.Apply(v => v + moved);
			else animation.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track.Add(time, slot.Offset);
		}
		if (rotation is { } rot)
		{
			slot.Rotation = rot - _att.Rotation;
			float turned = slot.Rotation - _slotRotation0;
			if (_partTurnKeys != null) _partTurnKeys.Apply(v => v + turned);
			else animation.GetOrCreate(slot.Name, n => new PartRotateTimeline(n)).Track.Add(time, slot.Rotation);
		}
		if (scale is { } scl)
		{
			slot.Scale = new Vector2(Ratio(scl.X, _att.Scale.X), Ratio(scl.Y, _att.Scale.Y));
			var grown = new Vector2(Ratio(slot.Scale.X, _slotScale0.X), Ratio(slot.Scale.Y, _slotScale0.Y));
			if (_partScaleKeys != null) _partScaleKeys.Apply(v => v * grown);
			else animation.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track.Add(time, slot.Scale);
		}
	}

	private static float Ratio(float value, float rest) => MathF.Abs(rest) < 1e-6f ? value : value / rest;

	private void FinishPartDrag()
	{
		if (_changed && _state.SelectedSlot is { } slot)
		{
			string verb = _drag switch
			{
				Drag.PartMove => "Move",
				Drag.PartRotate => "Rotate",
				Drag.PartScale => "Scale",
				_ => "Set pivot of"
			};
			bool shifted = _partMoveKeys != null || _partTurnKeys != null || _partScaleKeys != null;
			if (_keyPart && _keysBefore != null)
				_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
					$"{verb} part {slot.Name}" + (shifted ? " (selected keys)" : $" (key @ frame {_state.Frame})"),
					_keysBefore, AnimationSnapshot.Capture(_state.Animation), k => k.Restore()));
			else if (_partBefore != null)
				_state.PushStructure(_partBefore, $"{verb} part {slot.Name}");
		}
		_partBefore = null;
		_keysBefore = null;
		_att = null;
		_partSlot = null;
		_partMoveKeys = _partScaleKeys = null;
		_partTurnKeys = null;
	}

	// ======================= several at once =======================

	// A drag on something picked, with more picked: each picked thing moves, turns (round its own
	// joint or pivot) or scales (parts) alike; a picked bone takes the picked things under it along
	private void BeginMany(Gesture gesture, Vector2 world, Vector2 centre, Slot? scaled = null, IReadOnlyList<Vector2>? snapPoints = null)
	{
		_drag = Drag.Many;
		_gesture = gesture;
		_changed = false;
		_manyPress = world;
		_manyCentre = centre;
		_manySnapPoints = snapPoints ?? Array.Empty<Vector2>();
		_manyLastAngle = Angle(world - centre);
		_manyTurned = 0;
		var picked = _state.Picked.Where(item => !LockOps.IsLocked(item)).ToList();   // locked ones stay where they are
		_pickedBones = picked.OfType<Bone>().ToList();
		_pickedParts = picked.OfType<Slot>().ToList();
		bool animate = _state.AnimateMode;

		if (scaled != null && _state.Skeleton.ResolveAttachment(scaled) is { } att && Matrix3x2.Invert(scaled.Bone.World, out var toBone))
		{
			_scaleToBone = toBone;
			_scaleOffset = att.Offset + scaled.Offset;
			_scaleRotation = att.Rotation + scaled.Rotation;
			_startLocal = Vector2.Transform(world, toBone);
		}

		_manyKeysBefore = null;
		_manyRestBefore = null;
		if (animate)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			_manyKeysBefore = AnimationSnapshot.Capture(_state.Animation);
		}
		else _manyRestBefore = _state.CaptureStructure();

		// Setup mode with parts staying put: the picked bones move and nothing else does
		_manyFrozen = !animate && KeepPartsInPlace && BonesEnabled && gesture != Gesture.Scale && _pickedBones.Count > 0
			? FrozenRest.Capture(_state.Skeleton)
			: null;

		_movers.Clear();
		if (_manyFrozen != null) return;
		// Scaling: every picked part (bones don't scale). Moving and turning: the picked bones no
		// other picked bone is above, and the picked parts not on one of those (they ride along).
		var bones = gesture == Gesture.Scale ? new HashSet<Bone>() : _pickedBones.ToHashSet();
		foreach (var item in picked)
		{
			if (item is Bone bone)
			{
				if (gesture != Gesture.Scale && !Above(bone).Any(bones.Contains)) _movers.Add(BoneMover(bone, animate));
			}
			else if (item is Slot slot && !bones.Contains(slot.Bone) && !Above(slot.Bone).Any(bones.Contains) && PartMover(slot, animate) is { } part)
				_movers.Add(part);
		}
	}

	private static IEnumerable<Bone> Above(Bone bone)
	{
		for (var p = bone.Parent; p != null; p = p.Parent) yield return p;
	}

	// A space's turns and scales only, turned round (world → that space), and whether it's mirrored
	private static (Matrix3x2 ToLocal, float Mirror) Space(Matrix3x2 world)
	{
		world.Translation = Vector2.Zero;
		float det = world.M11 * world.M22 - world.M12 * world.M21;
		return (Matrix3x2.Invert(world, out var back) ? back : Matrix3x2.Identity, det < 0 ? -1f : 1f);
	}

	private Mover BoneMover(Bone bone, bool animate)
	{
		var (toLocal, mirror) = Space((animate ? bone.SpaceParent : bone.Parent)?.World ?? Matrix3x2.Identity);
		var mover = new Mover
		{
			Bone = bone,
			ToLocal = toLocal,
			Mirror = bone.InheritRotation && bone.InheritScale ? mirror : 1f,   // (otherwise a parent never mirrors it)
			Position = animate ? bone.Position : bone.SetupPosition,
			Rotation = animate ? bone.Rotation : bone.SetupRotation
		};
		if (animate)
		{
			mover.MoveKeys = KeyShift<Vector2>.Of(_state.Animation.Find<TranslateTimeline>(bone.Name), ShiftableKeys, _state.Fps);
			mover.TurnKeys = KeyShift<float>.Of(_state.Animation.Find<RotateTimeline>(bone.Name), ShiftableKeys, _state.Fps);
		}
		return mover;
	}

	// A part moves even when it shows nothing at this frame (an effect between its bursts, say)
	private Mover? PartMover(Slot slot, bool animate)
	{
		var (toLocal, mirror) = Space(slot.Bone.World);
		var mover = new Mover
		{
			Slot = slot,
			ToLocal = toLocal,
			Mirror = mirror,
			Position = slot.Offset,
			Rotation = slot.Rotation,
			Scale = slot.Scale
		};
		foreach (var drawing in _state.Skeleton.AllSkins().SelectMany(s => s.AttachmentsFor(slot.Name)).Distinct())
			mover.Drawings.Add((drawing, drawing.Offset, drawing.Rotation, drawing.Scale));
		if (!animate && mover.Drawings.Count == 0) return null;
		if (animate)
		{
			var animation = _state.Animation;
			mover.MoveKeys = KeyShift<Vector2>.Of(animation.Find<PartTranslateTimeline>(slot.Name), ShiftableKeys, _state.Fps);
			mover.TurnKeys = KeyShift<float>.Of(animation.Find<PartRotateTimeline>(slot.Name), ShiftableKeys, _state.Fps);
			mover.ScaleKeys = KeyShift<Vector2>.Of(animation.Find<PartScaleTimeline>(slot.Name), ShiftableKeys, _state.Fps);
		}
		return mover;
	}

	private void DragMany(Vector2 world)
	{
		bool animate = _state.AnimateMode;
		switch (_gesture)
		{
			case Gesture.Move:
				{
					var delta = world - _manyPress;
					delta += Snap.Offset(_manySnapPoints.Select(p => p + delta).ToList(), _camera.EditZoom);   // what's grabbed snaps
					if (_manyFrozen != null)
					{
						RestEdits.MovePicked(_manyFrozen, _pickedBones, _pickedParts, delta);
						break;
					}
					foreach (var mover in _movers) Move(mover, Vector2.TransformNormal(delta, mover.ToLocal), animate);
					break;
				}
			case Gesture.Turn:
				{
					float angle = Angle(world - _manyCentre);
					_manyTurned += MathUtil.ShortestAngle(angle - _manyLastAngle);
					_manyLastAngle = angle;
					float turn = _manyTurned;
					if (Held(ModifierKeys.Shift)) turn = MathF.Round(turn / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps
					if (_manyFrozen != null)
					{
						RestEdits.TurnPicked(_manyFrozen, _pickedBones, _pickedParts, turn);
						break;
					}
					foreach (var mover in _movers) Turn(mover, turn * mover.Mirror, animate);
					break;
				}
			case Gesture.Scale:
				{
					var factor = ScaleFactor(_startLocal, Vector2.Transform(world, _scaleToBone), _scaleOffset, _scaleRotation, Held(ModifierKeys.Shift));
					foreach (var mover in _movers) Scale(mover, factor, animate);
					break;
				}
		}
		_changed = true;
	}

	private void Move(Mover mover, Vector2 local, bool animate)
	{
		if (mover.Bone is { } bone)
		{
			if (!animate)
			{
				bone.Connected = false;   // off its parent's end, if it was on it
				bone.SetupPosition = mover.Position + local;
				return;
			}
			bone.Position = mover.Position + local;
			bone.UpdateWorld();
			if (mover.MoveKeys != null) mover.MoveKeys.Apply(v => v + local);
			else KeyTranslate(bone);
		}
		else if (mover.Slot is { } slot)
		{
			if (!animate)
			{
				foreach (var (drawing, offset, _, _) in mover.Drawings) drawing.Offset = offset + local;
				return;
			}
			slot.Offset = mover.Position + local;
			if (mover.MoveKeys != null) mover.MoveKeys.Apply(v => v + local);
			else _state.Animation.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track.Add(_state.Time, slot.Offset);
		}
	}

	private void Turn(Mover mover, float turn, bool animate)
	{
		if (mover.Bone is { } bone)
		{
			if (!animate)
			{
				bone.SetupRotation = mover.Rotation + turn;
				return;
			}
			bone.Rotation = mover.Rotation + turn;
			bone.UpdateWorld();
			if (mover.TurnKeys != null) mover.TurnKeys.Apply(v => v + turn);
			else _state.Animation.GetOrCreate(bone.Name, n => new RotateTimeline(n)).Track.Add(_state.Time, bone.Rotation - bone.SetupRotation);
		}
		else if (mover.Slot is { } slot)
		{
			if (!animate)
			{
				foreach (var (drawing, _, rotation, _) in mover.Drawings) drawing.Rotation = rotation + turn;
				return;
			}
			slot.Rotation = mover.Rotation + turn;
			if (mover.TurnKeys != null) mover.TurnKeys.Apply(v => v + turn);
			else _state.Animation.GetOrCreate(slot.Name, n => new PartRotateTimeline(n)).Track.Add(_state.Time, slot.Rotation);
		}
	}

	private void Scale(Mover mover, Vector2 factor, bool animate)
	{
		if (mover.Slot is not { } slot) return;
		if (!animate)
		{
			foreach (var (drawing, _, _, scale) in mover.Drawings) drawing.Scale = scale * factor;
			return;
		}
		slot.Scale = mover.Scale * factor;
		if (mover.ScaleKeys != null) mover.ScaleKeys.Apply(v => v * factor);
		else _state.Animation.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track.Add(_state.Time, slot.Scale);
	}

	private void FinishMany()
	{
		if (_changed)
		{
			int count = _pickedBones.Count + _pickedParts.Count;
			string verb = _gesture switch { Gesture.Move => "Move", Gesture.Turn => "Turn", _ => "Scale" };
			string name = $"{verb} {count} picked";
			if (_manyKeysBefore != null)
			{
				bool shifted = _movers.Any(m => m.MoveKeys != null || m.TurnKeys != null || m.ScaleKeys != null);
				_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
					name + (shifted ? " (selected keys)" : $" (keys @ frame {_state.Frame})"),
					_manyKeysBefore, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
			}
			else if (_manyRestBefore != null)
			{
				_state.PushStructure(_manyRestBefore, name + (_manyFrozen != null ? " (parts stay put)" : ""));
			}
		}
		_movers.Clear();
		_manyFrozen = null;
		_manyKeysBefore = null;
		_manyRestBefore = null;
	}

	// ======================= hit testing & math =======================

	private Bone? HitJoint(Vector2 world)
	{
		float bestDist = JointPickPx / _camera.EditZoom;
		Bone? best = null;
		foreach (var b in _state.Skeleton.AllBones())
		{
			if (b.IsHidden || b.Locked) continue;
			float d = Vector2.Distance(world, b.World.Translation);
			if (d <= bestDist) { best = b; bestDist = d; }
		}
		return best;
	}

	private Bone? HitBone(Vector2 world)
	{
		float bestDist = BonePickPx / _camera.EditZoom;
		Bone? best = null;
		foreach (var b in _state.Skeleton.AllBones())
		{
			if (b.IsHidden || b.Locked) continue;
			float d = DistanceToSegment(world, b.World.Translation, b.TipWorld);
			if (d <= bestDist) { best = b; bestDist = d; }
		}
		return best;
	}

	private static float AngleFrom(Bone bone, Vector2 world) => Angle(world - bone.World.Translation);

	// A world point in the bone's hierarchy parent's space (ignoring detach / attach keys)
	private static Vector2 ToParentSpace(Bone bone, Vector2 world) =>
		bone.Parent != null && Matrix3x2.Invert(bone.Parent.World, out var inverse)
			? Vector2.Transform(world, inverse)
			: world;

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
