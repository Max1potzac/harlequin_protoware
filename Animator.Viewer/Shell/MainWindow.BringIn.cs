using System.Windows;

namespace Animator.Viewer;

// Edit → Bring in from another object… (and + Skin ▾ → From another object…): another object's
// animations and drawings into this one (see ObjectMerge and BringInWindow)
public partial class MainWindow
{
	private void BringIn_Click(object sender, RoutedEventArgs e) => BringIn();

	private void BringIn()
	{
		if (_sceneLevel || _active is not { } target)
		{
			Tell("Open the object to bring things into first (double-click it): the animations and skins go into it.");
			return;
		}
		var sources = _scene.Objects.Where(o => o != target).ToList();
		if (sources.Count == 0)
		{
			Tell("There's no other object in this project to bring in from. Import it first (File → Import…: its Animate project, say), then open this object again.");
			return;
		}
		_paint.Tool.Deactivate(_paint);
		var dialog = new BringInWindow(target.Name, _skeleton, sources, SkinOps.Categories(_skeleton), () => _state.PickedBones.ToList()) { Owner = this };
		if (dialog.ShowDialog() != true || dialog.Source is not { } source) return;

		MergeResult? result = null;
		bool done = _state.EditStructure(() =>
		{
			result = ObjectMerge.Merge(source.Skeleton, source.Animations, _skeleton, _state.Animations, dialog.Plan);
			foreach (var (newLayer, oldLayer) in result.LayerSources) _assets.CopyPixels(oldLayer, newLayer);
			return result.Animations == 0 && result.Skin == null && result.NewParts.Count == 0 ? null : $"Bring in from {source.Name}";
		});
		if (!done || result == null)
		{
			Tell("Nothing came in.");
			return;
		}
		if (result.Skin is { } skin) SkinOps.Show(_skeleton, skin);
		_skeleton.SetToSetupPose();
		RefreshRigPanel();
		RefreshAnimationsPanel();
		var lines = new List<string>();
		if (result.Animations > 0) lines.Add($"{result.Animations} animation(s), {result.Keys} keys.");
		if (result.Skin is { } made) lines.Add($"The skin {made.Name}{(made.Category.Length > 0 ? $" (category {made.Category})" : "")}: it's on now. ✎ in the Skins view to line it up.");
		if (result.NewParts.Count > 0) lines.Add($"New parts: {string.Join(", ", result.NewParts.Select(p => p.Name))}.");
		lines.AddRange(result.Notes);
		MessageBox.Show(this, string.Join("\n\n", lines), $"Brought in from {source.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
	}
}
