using System.Windows.Controls;

namespace Animator.Viewer;

// The number fields that can be dragged (see NumberScrub): each drag is one undo step
public partial class MainWindow
{
	private readonly List<NumberScrub> _scrubs = new();

	private void InitScrub()
	{
		void Begin() => _state.Undo.BeginGroup();
		void End() => _state.Undo.EndGroup();
		void S(TextBox box, double step, double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool whole = false) =>
			_scrubs.Add(NumberScrub.Attach(box, step, Begin, End, min, max, whole));

		// The scene: objects and clips
		S(ObjectX, 1); S(ObjectY, 1); S(ObjectRotation, 1); S(ObjectScaleX, 0.01); S(ObjectScaleY, 0.01);
		S(ClipStart, 1, 0, whole: true); S(ClipLength, 1, 1, whole: true); S(ClipOffset, 1, 0, whole: true); S(ClipMixIn, 1, 0, whole: true);
		S(SceneLength, 1, 1, whole: true); S(StageWidthBox, 1, 0, whole: true); S(StageHeightBox, 1, 0, whole: true);

		// A bone
		S(BoneX, 1); S(BoneY, 1); S(BoneRotation, 1); S(BoneLength, 1, 0); S(BoneScaleX, 0.01); S(BoneScaleY, 0.01);
		S(BoneTurn, 1); S(BoneTilt, 1); S(LookFollow, 0.01, 0); S(LookRangeX, 1, 0); S(LookRangeY, 1, 0);

		// A part
		S(PartX, 1); S(PartY, 1); S(PartRotation, 1); S(PartScaleX, 0.01); S(PartScaleY, 0.01);
		S(PartPivotX, 1); S(PartPivotY, 1); S(PartOpacity, 1, 0, 100);

		// The animation
		S(AnimLength, 1, 1, whole: true);
	}
}
