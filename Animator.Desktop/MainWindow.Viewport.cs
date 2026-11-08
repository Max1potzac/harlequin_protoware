using Avalonia.Input;

namespace Animator.Desktop;

// The viewport: drawn by ViewportPainter, its mouse going to the guides, then the object tool
// (scene level) or the bone tool (inside an object), as in the Windows editor
public sealed partial class MainWindow
{
	private Vector2? _mouseOnView;
	private bool _objectDrag;    // the current press went to the object tool
	private bool _guideDrag;     // ...to the guides
	private bool _viewPressed;   // a button is down on the viewport

	private void WireViewport()
	{
		Viewport.Draw = DrawViewport;
		Viewport.PointerPressed += Viewport_PointerPressed;
		Viewport.PointerMoved += Viewport_PointerMoved;
		Viewport.PointerReleased += Viewport_PointerReleased;
		Viewport.PointerExited += (_, _) => _mouseOnView = null;
		Viewport.PointerWheelChanged += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			if (e.Delta.Y != 0) _camera.ZoomAt(Input.At(e, Viewport), e.Delta.Y > 0 ? 1.1f : 1 / 1.1f);
			e.Handled = true;
		};
	}

	private void DrawViewport(SKCanvas canvas, Vector2 size, float dpi)
	{
		canvas.Clear(CanvasTheme.Sk("Viewport"));
		_snap.GridSize = Scene.GridSize;   // (the project's: it changes with undo, or another file)
		_snap.Guides = Scene.Guides;
		_painter.MouseOnView = _mouseOnView;
		_painter.ShowHitboxes = _showHitboxes;
		_painter.BoxesView = ShowingBoxes;
		_painter.BoxPicked = _boxItem;
		_painter.BoxHover = _boxHover;
		_painter.Draw(canvas, size, dpi);
	}

	// A bone, part (or an object, at scene level) there to pick: guides give way to it
	private bool SomethingToPickAt(Vector2 screen) => SceneLevel
		? _objectTool.Pick(_camera.ScreenToScene(screen)) != null
		: _tool.Hit(_camera.ScreenToWorld(screen)) != null;

	private void Viewport_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		Viewport.Focus();
		var point = e.GetCurrentPoint(Viewport);
		_mods = Input.ModsOf(e.KeyModifiers);
		if (Input.ButtonOf(point.Properties.PointerUpdateKind) is not { } button) return;
		var pos = Input.At(e, Viewport);
		e.Handled = true;
		ViewportDown(pos, button, e.ClickCount);
		if (_viewPressed) e.Pointer.Capture(Viewport);
	}

	// (the tests press here too)
	internal void ViewportDown(Vector2 pos, PointerButton button, int clicks)
	{
		_camera.ViewSize = Viewport.SizeInUnits;
		_viewPressed = true;

		// The rulers and guides come first: out of a ruler comes a guide, a guide moves
		if (button != _tool.PanButton && _guides.MouseDown(pos) != GuideTool.Press.NotMine)
		{
			_guideDrag = true;
			return;
		}

		// Scene level: whole objects
		_objectDrag = SceneLevel;
		if (SceneLevel)
		{
			_objectTool.MouseDown(pos, button, clicks);
			if (!SceneLevel) _viewPressed = false;   // a double-click just opened an object
			return;
		}

		// Inside an object: a double-click on a part paints it; on another object, opens that one
		if (button == _tool.SelectButton && clicks == 2)
		{
			if (TryOpenPaintAt(pos))
			{
				_viewPressed = false;
				return;
			}
			if (_objectTool.Pick(_camera.ScreenToScene(pos)) is { } other && other != _session.Active)
			{
				EnterObject(other);
				_viewPressed = false;
				return;
			}
		}

		// The mesh tools (the part panel's Mesh box): its points, or its weights
		if (_mesh.MouseDown(pos, button, subtract: _mods.HasFlag(KeyMods.Alt)))
		{
			_meshDrag = true;
			return;
		}

		// The Boxes view open: a hitbox or point under the mouse is picked and dragged
		if (BoxMouseDown(pos, button)) return;

		_tool.MouseDown(pos, button);
	}

	private bool _meshDrag;   // the current press went to the mesh tool

	private void Viewport_PointerMoved(object? sender, PointerEventArgs e)
	{
		_mods = Input.ModsOf(e.KeyModifiers);
		ViewportMove(Input.At(e, Viewport));
	}

	internal void ViewportMove(Vector2 pos)
	{
		_mouseOnView = pos;
		if (_guideDrag)
		{
			_guides.MouseMove(pos);
			return;
		}
		if (_meshDrag || _mesh.Dragging)
		{
			_mesh.MouseMove(pos);
			return;
		}
		if (_session.DraggingBox)
		{
			_session.DragBoxTo(_camera.ScreenToWorld(pos));
			return;
		}
		if (!_viewPressed)
		{
			_mesh.MouseMove(pos);   // (which point the mouse is over)
			BoxHoverAt(pos);        // (which box)
		}
		if (_objectDrag || SceneLevel) _objectTool.MouseMove(pos);
		else _tool.MouseMove(pos);
	}

	private void Viewport_PointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		_mods = Input.ModsOf(e.KeyModifiers);
		ViewportUp(Input.At(e, Viewport));
		e.Pointer.Capture(null);
	}

	internal void ViewportUp(Vector2 pos)
	{
		if (!_viewPressed) return;
		_viewPressed = false;
		if (_guideDrag)
		{
			_guideDrag = false;
			_guides.MouseUp(pos);
			return;
		}
		if (_meshDrag)
		{
			_meshDrag = false;
			if (_mesh.MouseUp()) SyncMeshPanel(force: true);
			return;
		}
		if (_session.DraggingBox)
		{
			_session.EndBoxDrag();   // (one undo step, if it moved)
			SyncBoxesPanel(force: true);
			return;
		}
		if (_objectDrag) _objectTool.MouseUp();
		else _tool.MouseUp();
		_objectDrag = false;
	}
}
