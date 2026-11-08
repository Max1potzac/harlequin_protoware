using Avalonia.Input;

namespace Animator.Desktop;

// Edit > Bring in from another object… (and the Skins tab's + Skin ▾ > From another object…):
// another object's animations and drawings into the open one (EditorSession.BringIn); and Edit >
// Bring in from Animate projects…: many characters' heads as skins at once
// (EditorSession.BringInFromAnimate)
public sealed partial class MainWindow
{
	// What the dialogs are answered with (the tests answer them); null: the user's answer
	internal Func<BringInWindow, Task<bool>>? AnswerBringIn { get; set; }
	internal Func<BatchBringInWindow, Task<bool>>? AnswerBatchBringIn { get; set; }

	// What came in (or why nothing did), as last told
	internal string? BroughtInReport { get; private set; }

	private async Task Report(string title, string text)
	{
		BroughtInReport = text;
		if (AnswerBringIn == null && AnswerBatchBringIn == null) await Dialogs.Tell(this, title, text);
	}

	internal async Task BringIn()
	{
		if (SceneLevel || _session.Active is not { } target)
		{
			await Report("Bring in", "Open the object to bring things into first (double-click it): the animations and skins go into it.");
			return;
		}
		var sources = Scene.Objects.Where(o => o != target).ToList();
		if (sources.Count == 0)
		{
			await Report("Bring in", "There's no other object in this project to bring in from. Import it first (File > Import…: its Animate project, say), then open this object again.");
			return;
		}
		_session.FinishPaintTool();
		var dialog = new BringInWindow(target.Name, State.Skeleton, sources, SkinOps.Categories(State.Skeleton), () => State.PickedBones.ToList());
		bool ok = AnswerBringIn is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok || dialog.Source is not { } source) return;

		if (_session.BringIn(source, dialog.Plan) is not { } result)
		{
			await Report("Bring in", "Nothing came in.");
			return;
		}
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		var lines = new List<string>();
		if (result.Animations > 0) lines.Add($"{result.Animations} animation(s), {result.Keys} keys.");
		if (result.Skin is { } made) lines.Add($"The skin {made.Name}{(made.Category.Length > 0 ? $" (category {made.Category})" : "")}: it's on now. ✎ in the Skins tab to line it up.");
		if (result.NewParts.Count > 0) lines.Add($"New parts: {string.Join(", ", result.NewParts.Select(p => p.Name))}.");
		lines.AddRange(result.Notes);
		await Report($"Brought in from {source.Name}", string.Join("\n\n", lines));
	}

	internal async Task BatchBringIn()
	{
		if (SceneLevel || _session.Active is not { } target)
		{
			await Report("Bring in", "Open the object to bring the heads into first (double-click it), and pick its head bone.");
			return;
		}
		_session.FinishPaintTool();
		var picked = State.PickedBones.ToList();
		var dialog = new BatchBringInWindow(target.Name, picked, SkinOps.Categories(State.Skeleton), _settings.ImportFolder);
		bool ok = AnswerBatchBringIn is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		if (dialog.Files.Count > 0)
		{
			_settings.ImportFolder = Path.GetDirectoryName(dialog.Files[0]);
			SaveSettingsQuietly();
		}

		BatchBroughtIn done;
		Cursor = new Cursor(StandardCursorType.Wait);
		try
		{
			done = _session.BringInFromAnimate(dialog.Files, new BringInOptions(picked, dialog.Category, dialog.Animations, dialog.AsPlaced, dialog.Accessories));
		}
		finally
		{
			Cursor = Cursor.Default;
		}
		var (brought, problems, notes) = done;
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		var lines = new List<string>();
		if (brought.Count > 0) lines.Add($"Brought in ({brought.Count}):\n" + string.Join("\n", brought));
		if (problems.Count > 0) lines.Add("Left out:\n" + string.Join("\n", problems));
		lines.AddRange(notes);
		if (brought.Count > 0) lines.Add("Turn a skin on in the Skins tab to see it; ✎ on it to nudge its drawings into place.");
		await Report("Bring in from Animate projects", string.Join("\n\n", lines));
	}
}
