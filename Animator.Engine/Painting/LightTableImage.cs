using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

// One onion-skin drawing under the one being painted: its pixels, where they sit in the
// painted drawing's pixel space, which side of the playhead it's from, and how faint it is
public sealed record LightTableImage(SKBitmap Image, Matrix3x2 Placement, bool Before, float Opacity);
