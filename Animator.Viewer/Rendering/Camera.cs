using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

public class Camera
{
    public Vector2 Pan { get; set; }          // world point shown at the viewport's center
    public float Zoom { get; set; } = 1f;
    public Vector2 ViewSize { get; set; }     // viewport size in WPF units

    public Vector2 ScreenToWorld(Vector2 s) => new(
         (s.X - ViewSize.X / 2) / Zoom + Pan.X,
        -(s.Y - ViewSize.Y / 2) / Zoom + Pan.Y);   // minus sign = the Y flip

    // Must be the exact mirror of ScreenToWorld, or clicks won't line up with drawings
    public void Apply(SKCanvas canvas)
    {
        canvas.Translate(ViewSize.X / 2, ViewSize.Y / 2);
        canvas.Scale(Zoom, -Zoom);
        canvas.Translate(-Pan.X, -Pan.Y);
    }

    // Zoom so the point under the cursor stays under the cursor
    public void ZoomAt(Vector2 screen, float factor)
    {
        var before = ScreenToWorld(screen);
        Zoom = Math.Clamp(Zoom * factor, 0.1f, 20f);
        var after = ScreenToWorld(screen);
        Pan += before - after;
    }

    public void Reset()
    {
        Pan = Vector2.Zero;
        Zoom = 1f;
    }
}