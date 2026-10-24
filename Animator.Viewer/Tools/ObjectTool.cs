using System.Numerics;
using System.Windows.Input;

namespace Animator.Viewer.Tools;

// The viewport at scene level: click an object to select it, drag to move it, drag a corner
// of its box to scale it, the round handle to turn it (Shift: keep proportions / 15° steps).
// Double-click an object to open it for editing. Locked and hidden objects can't be picked.
public class ObjectTool
{
	private enum Drag { None, Pan, Move, Rotate, Scale }

	private readonly Func<Scene> _scene;
	private readonly SceneState _sceneState;
	private readonly Camera _camera;
	private readonly SkeletonRenderer _renderer;
	private readonly UndoStack _undo;

	private Drag _drag;
	private Vector2 _lastScreen, _pressScreen;
	private bool _moving;
	private SceneSnapshot? _before;

	// The dragged object's values at the start, in the space its position is measured in
	private Matrix3x2 _toParent;
	private Vector2 _startLocal, _startPosition, _startScale;
	private float _startRotation, _lastAngle, _rotAccum;

	private const float HandlePickPx = 8f;
	private const float BonePickPx = 6f;
	private const float DragStartPx = 3f;

	// Shared with the Objects panel and the scene timeline
	public SceneObject? Selected
	{
		get => _sceneState.Selected;
		set => _sceneState.Selected = value;
	}

	// Double-click on an object: the main window opens it
	public event Action<SceneObject>? EnterRequested;

	public ObjectTool(Func<Scene> scene, SceneState sceneState, Camera camera, SkeletonRenderer renderer, UndoStack undo)
	{
		_scene = scene;
		_sceneState = sceneState;
		_camera = camera;
		_renderer = renderer;
		_undo = undo;
	}

	public void MouseDown(Vector2 screen, MouseButton button, int clickCount)
	{
		_lastScreen = _pressScreen = screen;
		_moving = false;
		if (button == MouseButton.Middle) { _drag = Drag.Pan; return; }
		if (button != MouseButton.Left) return;

		var scene = _scene();
		var p = _camera.ScreenToScene(screen);

		// 1. Handles of the selected object
		if (Selected is { Visible: true, Locked: false } selected && scene.Objects.Contains(selected))
		{
			var h = ObjectGizmo.For(scene, selected, _renderer, _camera.Zoom);
			float r = HandlePickPx / _camera.Zoom;
			if (Vector2.Distance(p, h.Rotate) <= r) { Begin(selected, Drag.Rotate, p); return; }
			if (h.Corners.Any(c => Vector2.Distance(p, c) <= r)) { Begin(selected, Drag.Scale, p); return; }
		}

		// 2. An object under the mouse, front first
		if (Pick(p) is { } hit)
		{
			Selected = hit;
			if (clickCount >= 2) { _drag = Drag.None; EnterRequested?.Invoke(hit); return; }
			Begin(hit, Drag.Move, p);
			return;
		}

		// 3. Empty space: deselect and pan
		Selected = null;
		_drag = Drag.Pan;
	}

	public void MouseMove(Vector2 screen)
	{
		var p = _camera.ScreenToScene(screen);
		switch (_drag)
		{
			case Drag.Pan:
				var d = screen - _lastScreen;
				_camera.Pan -= new Vector2(d.X, -d.Y) / _camera.Zoom;
				break;

			case Drag.Move or Drag.Rotate or Drag.Scale when Selected is { } obj:
				if (!_moving && Vector2.Distance(screen, _pressScreen) < DragStartPx) break;   // a click only selects
				_moving = true;
				DragObject(obj, Vector2.Transform(p, _toParent));
				break;
		}
		_lastScreen = screen;
	}

	public void MouseUp()
	{
		if (_moving && _before != null && Selected is { } obj)
		{
			string verb = _drag switch { Drag.Rotate => "Turn", Drag.Scale => "Scale", _ => "Move" };
			_undo.Push(new SnapshotCommand<SceneSnapshot>($"{verb} object {obj.Name}",
				_before, SceneSnapshot.Capture(_scene()), s => s.Restore()));
		}
		_drag = Drag.None;
		_before = null;
		_moving = false;
	}

	// The front-most visible, unlocked object with a part pixel or a bone under the point.
	// Failing that, a drawn object whose canvas is there (so a blank drawing can still be grabbed).
	public SceneObject? Pick(Vector2 scenePoint) => Pick(scenePoint, canvas: false) ?? Pick(scenePoint, canvas: true);

	private SceneObject? Pick(Vector2 scenePoint, bool canvas)
	{
		var scene = _scene();
		for (int i = scene.Objects.Count - 1; i >= 0; i--)
		{
			var obj = scene.Objects[i];
			if (!obj.Visible || obj.Locked) continue;
			if (!Matrix3x2.Invert(scene.WorldTransform(obj), out var toObject)) continue;
			var local = Vector2.Transform(scenePoint, toObject);

			if (canvas)
			{
				if (obj.Kind == ObjectKind.Drawn && _renderer.InsidePartBox(obj.Skeleton, local)) return obj;
				continue;
			}

			if (_renderer.Pick(obj.Skeleton, local) != null) return obj;

			// Rigged objects without pictures yet can still be grabbed by their bones
			if (obj.Kind == ObjectKind.Drawn) continue;
			float reach = BonePickPx / (_camera.Zoom * ScaleOf(scene.WorldTransform(obj)));
			if (obj.Skeleton.AllBones().Any(b => DistanceToSegment(local, b.World.Translation, b.TipWorld) <= reach))
				return obj;
		}
		return null;
	}

	private void Begin(SceneObject obj, Drag mode, Vector2 scenePoint)
	{
		var scene = _scene();
		Selected = obj;
		_drag = mode;
		_before = SceneSnapshot.Capture(scene);
		_toParent = Matrix3x2.Invert(scene.ParentTransform(obj), out var inverse) ? inverse : Matrix3x2.Identity;
		_startLocal = Vector2.Transform(scenePoint, _toParent);
		_startPosition = obj.Position;
		_startRotation = obj.Rotation;
		_startScale = obj.Scale;
		_lastAngle = Angle(_startLocal - _startPosition);
		_rotAccum = 0;
	}

	private void DragObject(SceneObject obj, Vector2 local)
	{
		bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		switch (_drag)
		{
			case Drag.Move:
				obj.Position = _startPosition + (local - _startLocal);
				break;

			case Drag.Rotate:
				{
					float angle = Angle(local - _startPosition);
					_rotAccum += MathUtil.ShortestAngle(angle - _lastAngle);
					_lastAngle = angle;
					float r = _startRotation + _rotAccum;
					if (shift) r = MathF.Round(r / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps
					obj.Rotation = r;
					break;
				}

			case Drag.Scale:
				{
					// Compare the mouse in the object's unturned frame, relative to its origin
					var v0 = Rotate(_startLocal - _startPosition, -_startRotation);
					var v = Rotate(local - _startPosition, -_startRotation);
					if (shift)
					{
						float f = Vector2.Dot(v, v0) / MathF.Max(Vector2.Dot(v0, v0), 1e-6f);
						obj.Scale = _startScale * f;
					}
					else
					{
						obj.Scale = new Vector2(
							MathF.Abs(v0.X) > 0.5f ? _startScale.X * v.X / v0.X : _startScale.X,
							MathF.Abs(v0.Y) > 0.5f ? _startScale.Y * v.Y / v0.Y : _startScale.Y);
					}
					break;
				}
		}
	}

	private static float ScaleOf(Matrix3x2 m)
	{
		float det = MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21);
		return det > 1e-12f ? MathF.Sqrt(det) : 1f;
	}

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
