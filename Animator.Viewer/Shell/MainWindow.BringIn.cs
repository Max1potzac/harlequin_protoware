using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;

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

// Edit → Bring in from Animate projects…: many characters' heads as skins at once
public partial class MainWindow
{
	private void BatchBringIn_Click(object sender, RoutedEventArgs e) => BatchBringIn();

	private void BatchBringIn()
	{
		if (_sceneLevel || _active is not { } target)
		{
			Tell("Open the object to bring the heads into first (double-click it), and pick its head bone.");
			return;
		}
		_paint.Tool.Deactivate(_paint);
		var picked = _state.PickedBones.ToList();
		var dialog = new BatchBringInWindow(target.Name, picked, SkinOps.Categories(_skeleton), _appSettings.ImportFolder) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		if (dialog.Files.Count > 0)
		{
			_appSettings.ImportFolder = Path.GetDirectoryName(dialog.Files[0]);
			_appSettings.Save();
		}

		var brought = new List<string>();
		var problems = new List<string>();
		var notes = new HashSet<string>();
		Mouse.OverrideCursor = Cursors.Wait;
		try
		{
			_state.EditStructure(() =>
			{
				var options = new BringInOptions(picked, dialog.Category, dialog.Animations, dialog.AsPlaced, dialog.Accessories);
				foreach (string file in dialog.Files)
				{
					try
					{
						var b = AnimateBringIn.FromAnimate(file, _skeleton, _state.Animations, _assets, options);
						if (b.Problem != null)
						{
							problems.Add(AnimateBringIn.Describe(b));
							continue;
						}
						brought.Add(AnimateBringIn.Describe(b));
						foreach (string note in b.Result!.Notes.Where(n => !n.StartsWith("Drawings lined up", StringComparison.Ordinal))) notes.Add(note);
					}
					catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
					{
						problems.Add($"{AnimateBringIn.ProjectName(file)}: {ex.Message}");
					}
				}
				return brought.Count > 0 ? $"Bring in {brought.Count} skin(s)" : null;
			});
		}
		finally
		{
			Mouse.OverrideCursor = null;
		}
		_skeleton.SetToSetupPose();
		RefreshRigPanel();
		RefreshAnimationsPanel();
		var lines = new List<string>();
		if (brought.Count > 0) lines.Add($"Brought in ({brought.Count}):\n" + string.Join("\n", brought));
		if (problems.Count > 0) lines.Add("Left out:\n" + string.Join("\n", problems));
		lines.AddRange(notes);
		if (brought.Count > 0) lines.Add("Turn a skin on in the Skins view to see it; ✎ on it to nudge its drawings into place.");
		MessageBox.Show(this, string.Join("\n\n", lines), "Bring in from Animate projects", MessageBoxButton.OK,
			brought.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
	}
}
