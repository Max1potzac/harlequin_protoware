using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

public class Camera
{
    public Vector2 Pan { get; set; }          // scene point shown at the viewport's center
    public float Zoom { get; set; } = 1f;
    public Vector2 ViewSize { get; set; }     // viewport size in WPF units

    // The object being edited: its space → the scene. Tools work in the object's own space
    // ("world" below), so they don't need to know where the object sits in the scene.
    public Matrix3x2 Space
    {
        get => _space;
        set
        {
            _space = value;
            _toSpace = Matrix3x2.Invert(value, out var inverse) ? inverse : Matrix3x2.Identity;
            float det = MathF.Abs(value.M11 * value.M22 - value.M12 * value.M21);
            _spaceScale = det > 1e-12f ? MathF.Sqrt(det) : 1f;
        }
    }
    private Matrix3x2 _space = Matrix3x2.Identity;
    private Matrix3x2 _toSpace = Matrix3x2.Identity;
    private float _spaceScale = 1f;

    // Screen pixels per unit of the edited object (for handle sizes and pick distances)
    public float EditZoom => Zoom * _spaceScale;

    public Vector2 ScreenToScene(Vector2 s) => new(
         (s.X - ViewSize.X / 2) / Zoom + Pan.X,
        -(s.Y - ViewSize.Y / 2) / Zoom + Pan.Y);   // minus sign = the Y flip

    // A screen point in the edited object's space
    public Vector2 ScreenToWorld(Vector2 s) => Vector2.Transform(ScreenToScene(s), _toSpace);

    // Must be the exact mirror of ScreenToScene, or clicks won't line up with drawings
    public void Apply(SKCanvas canvas)
    {
        canvas.Translate(ViewSize.X / 2, ViewSize.Y / 2);
        canvas.Scale(Zoom, -Zoom);
        canvas.Translate(-Pan.X, -Pan.Y);
    }

    // Zoom so the point under the cursor stays under the cursor
    public void ZoomAt(Vector2 screen, float factor)
    {
        var before = ScreenToScene(screen);
        Zoom = Math.Clamp(Zoom * factor, 0.1f, 20f);
        var after = ScreenToScene(screen);
        Pan += before - after;
    }

    public void Reset()
    {
        Pan = Vector2.Zero;
        Zoom = 1f;
    }
}
