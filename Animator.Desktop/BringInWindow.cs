using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Edit > Bring in from another object… (the Windows editor's dialog): another object's
// animations onto this one's bones, and its drawings (a head, an outfit) as a skin here. Bones,
// parts and drawings are matched by name, past each object's prefix ("zombie_egypt_jaw" ↔
// "zombie_pirate_basic_jaw"); every match can be changed. OK leaves the source and the plan in
// Source and Plan.
public sealed class BringInWindow : Window
{
	private const string None = "(none)";
	private const string LeaveOut = "(leave it out)";
	private const string AddNew = "(add it as a new part)";

	private sealed record SourceChoice(SceneObject Object)
	{
		public override string ToString() => Object.Name;
	}

	private readonly Skeleton _target;
	private readonly Func<IReadOnlyCollection<Bone>> _pickedBones;
	private readonly IReadOnlyList<string> _categories;
	internal readonly ComboBox From = new() { Width = 260 };
	internal readonly TextBox SourcePrefix = new() { Width = 170 };
	internal readonly TextBox TargetPrefix = new() { Width = 170 };
	private readonly Grid _bones = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 4, 0, 0) };
	private readonly Grid _parts = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,*"), Margin = new Thickness(0, 4, 0, 0) };
	private readonly Expander _bonesBox = new() { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly CheckBox MakeSkin = new() { Content = "Its ticked parts' drawings become a skin here", IsChecked = true, Margin = new Thickness(0, 6, 0, 4) };
	internal readonly TextBox SkinName = new() { Width = 170 };
	internal readonly TextBox Category = new() { Width = 140, Text = "Head" };
	private readonly CheckBox _overlay = new() { Content = "Overlay (shows over the other skins)", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
	private readonly StackPanel _animations = new();
	internal readonly TextBox Suffix = new() { Width = 170 };
	private readonly RadioButton _absolute = new() { Content = "Poses as they look (worked out again from this object's rest pose)", IsChecked = true, GroupName = "how" };
	private readonly RadioButton _offsets = new() { Content = "Keys as they are (changes from the rest pose)", GroupName = "how" };
	internal readonly TextBlock Summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

	private readonly List<(string Source, ComboBox To)> _boneRows = new();
	internal readonly List<(string Source, CheckBox Skin, ComboBox To)> PartRows = new();
	internal readonly List<(string Name, CheckBox Box)> AnimationRows = new();

	public SceneObject? Source => (From.SelectedItem as SourceChoice)?.Object;
	public MergePlan Plan { get; private set; } = new();

	public BringInWindow(string targetName, Skeleton target, IReadOnlyList<SceneObject> sources, IEnumerable<string> categories,
		Func<IReadOnlyCollection<Bone>> pickedBones)
	{
		_target = target;
		_pickedBones = pickedBones;
		_categories = categories.Append("Head").Append("Body").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		Title = $"Bring in from another object, into {targetName}";
		Width = 760;
		Height = 820;
		MinWidth = 560;
		MinHeight = 420;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		var panel = new StackPanel { Margin = new Thickness(14) };
		panel.Children.Add(Note("Another object's animations come onto this object's bones, and its drawings (a head, an outfit) can become a skin here. " +
			"Bones, parts and drawings are matched by name, setting aside what each object's names start with. Change any match below. " +
			"Import the other object first (File > Import…), into this project."));

		panel.Children.Add(Heading("From"));
		From.ItemsSource = sources.Select(s => new SourceChoice(s)).ToList();
		From.SelectionChanged += (_, _) => Suggest();
		panel.Children.Add(From);

		panel.Children.Add(Heading("Names"));
		var again = new Button { Content = "Match again", Margin = new Thickness(10, 0, 0, 0) };
		again.Click += (_, _) => Suggest(keepPrefixes: true);
		panel.Children.Add(new WrapPanel { Children = { Label("Its names start with"), SourcePrefix, Label("   this object's with"), TargetPrefix, again } });

		_bonesBox.Content = _bones;
		panel.Children.Add(_bonesBox);

		panel.Children.Add(Heading("Parts"));
		panel.Children.Add(Note("Ticked: its drawings go into the skin. A part with no match here can be added as a new part, shown only while the skin is on " +
			"(or always, if it isn't ticked)."));
		var ticks = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
		void Tick(string text, string tip, Func<string, bool> which)
		{
			var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
			if (tip.Length > 0) ToolTip.SetTip(b, tip);
			b.Click += (_, _) =>
			{
				foreach (var row in PartRows) row.Skin.IsChecked = which(row.Source);
			};
			ticks.Children.Add(b);
		}
		Tick("Tick all", "Every part's drawings go into the skin", _ => true);
		Tick("Tick none", "", _ => false);
		Tick("Tick those on the picked bones", "The parts that would go on the bones picked in this object (and the bones under them): pick the head bone first for a head",
			OnPickedBones);
		panel.Children.Add(ticks);
		panel.Children.Add(_parts);

		panel.Children.Add(Heading("Skin"));
		panel.Children.Add(MakeSkin);
		var pickCategory = new Button { Content = "▾", Margin = new Thickness(2, 0, 0, 0) };
		pickCategory.Click += (_, _) =>
		{
			var menu = new MenuFlyout();
			foreach (string c in _categories)
			{
				var item = new MenuItem { Header = c };
				item.Click += (_, _) => Category.Text = c;
				menu.Items.Add(item);
			}
			menu.ShowAt(pickCategory);
		};
		panel.Children.Add(new WrapPanel { Children = { Label("Name"), SkinName, Label("   Category"), Category, pickCategory, _overlay } });

		panel.Children.Add(Heading("Animations"));
		panel.Children.Add(_animations);
		panel.Children.Add(new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Children = { Label("Their names here end with"), Suffix } });
		panel.Children.Add(_absolute);
		panel.Children.Add(_offsets);
		panel.Children.Add(Summary);

		var ok = new Button { Content = "Bring in", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } });

		Content = new ScrollViewer { Content = panel };
		if (sources.Count > 0) From.SelectedIndex = 0;
		UiSize.Fit(this);
	}

	private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

	private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

	private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) };

	// ======================= filling it in =======================

	private void Suggest(bool keepPrefixes = false)
	{
		if (Source is not { } source) return;
		var plan = ObjectMerge.Suggest(source.Skeleton, source.Animations, _target);
		if (keepPrefixes)
		{
			plan.SourcePrefix = SourcePrefix.Text ?? "";
			plan.TargetPrefix = TargetPrefix.Text ?? "";
			plan.Bones.Clear();
			plan.Parts.Clear();
			foreach (var (s, t) in ObjectMerge.Match(source.Skeleton.AllBones().Select(b => b.Name), _target.AllBones().Select(b => b.Name), plan.SourcePrefix, plan.TargetPrefix))
				plan.Bones[s] = t;
			foreach (var (s, t) in ObjectMerge.Match(source.Skeleton.Slots.Select(x => x.Name), _target.Slots.Select(x => x.Name), plan.SourcePrefix, plan.TargetPrefix))
				plan.Parts[s] = t;
		}
		SourcePrefix.Text = plan.SourcePrefix;
		TargetPrefix.Text = plan.TargetPrefix;

		// Bones
		_bones.Children.Clear();
		_bones.RowDefinitions.Clear();
		_boneRows.Clear();
		var targetBones = new List<string> { None };
		targetBones.AddRange(_target.AllBones().Select(b => b.Name));
		foreach (var bone in source.Skeleton.AllBones())
		{
			var to = new ComboBox { ItemsSource = targetBones, SelectedItem = plan.Bones.GetValueOrDefault(bone.Name) ?? None, Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
			Row(_bones, bone.Name, null, to);
			_boneRows.Add((bone.Name, to));
		}
		int matchedBones = plan.Bones.Values.Count(v => v != null);
		_bonesBox.Header = $"Bones: {matchedBones} of {plan.Bones.Count} matched (open to change)";
		_bonesBox.IsExpanded = matchedBones < plan.Bones.Count / 2;

		// Parts, front first (as the layers list them)
		_parts.Children.Clear();
		_parts.RowDefinitions.Clear();
		PartRows.Clear();
		var targetParts = new List<string> { LeaveOut, AddNew };
		targetParts.AddRange(_target.Slots.Select(s => s.Name));
		foreach (var slot in Enumerable.Reverse(source.Skeleton.Slots))
		{
			var match = plan.Parts.GetValueOrDefault(slot.Name);
			var skin = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 2) };
			ToolTip.SetTip(skin, "Its drawings go into the skin");
			var to = new ComboBox { ItemsSource = targetParts, SelectedItem = match ?? AddNew, Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
			Row(_parts, slot.Name, skin, to);
			PartRows.Add((slot.Name, skin, to));
		}

		// Animations
		_animations.Children.Clear();
		AnimationRows.Clear();
		foreach (var animation in source.Animations)
		{
			var box = new CheckBox { Content = animation.Name, IsChecked = true, Margin = new Thickness(0, 0, 0, 2) };
			_animations.Children.Add(box);
			AnimationRows.Add((animation.Name, box));
		}
		string word = ObjectMerge.Core(source.Name, "zombie_").Replace(".pam", "");
		Suffix.Text = "_" + word;
		SkinName.Text = word;
		Summary.Text = $"{plan.Parts.Values.Count(v => v != null)} of {plan.Parts.Count} parts and {matchedBones} of {plan.Bones.Count} bones match by name.";
	}

	private static void Row(Grid grid, string name, CheckBox? tick, ComboBox to)
	{
		int row = grid.RowDefinitions.Count;
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		int column = 0;
		if (tick != null)
		{
			Grid.SetRow(tick, row);
			grid.Children.Add(tick);
			column = 1;
		}
		var label = new TextBlock { Text = name + "  →", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
		Grid.SetRow(label, row);
		Grid.SetColumn(label, column);
		grid.Children.Add(label);
		Grid.SetRow(to, row);
		Grid.SetColumn(to, column + 1);
		grid.Children.Add(to);
	}

	// Would this source part go on one of the picked bones here (or under one)?
	private bool OnPickedBones(string sourcePart)
	{
		var picked = _pickedBones();
		if (picked.Count == 0 || Source is not { } source) return false;
		var row = PartRows.First(r => r.Source == sourcePart);
		Bone? bone = row.To.SelectedItem is string name && _target.FindSlot(name) is { } slot ? slot.Bone : null;
		if (bone == null && source.Skeleton.FindSlot(sourcePart) is { } theirs)
			for (var b = theirs.Bone; b != null && bone == null; b = b.Parent)
				if (_boneRows.FirstOrDefault(r => r.Source == b.Name).To?.SelectedItem is string mapped && mapped != None)
					bone = _target.FindBone(mapped);
		return bone != null && picked.Any(p => bone == p || bone.IsDescendantOf(p));
	}

	internal void TryOk()
	{
		if (Source == null)
		{
			Summary.Text = "There's no other object to bring in from: import it first (File > Import…).";
			return;
		}
		string skinName = SkinName.Text?.Trim() ?? "";
		var plan = new MergePlan
		{
			SourcePrefix = SourcePrefix.Text ?? "",
			TargetPrefix = TargetPrefix.Text ?? "",
			Suffix = Suffix.Text ?? "",
			Absolute = _absolute.IsChecked == true,
			SkinName = MakeSkin.IsChecked == true ? (skinName.Length > 0 ? skinName : "Skin") : null,
			SkinCategory = Category.Text?.Trim() ?? "",
			SkinOverlay = _overlay.IsChecked == true
		};
		foreach (var (source, to) in _boneRows)
			plan.Bones[source] = to.SelectedItem is string name && name != None ? name : null;
		foreach (var (source, skin, to) in PartRows)
		{
			string? choice = to.SelectedItem as string;
			plan.Parts[source] = choice is null or LeaveOut or AddNew ? null : choice;
			if (choice == AddNew) plan.NewParts.Add(source);
			if (skin.IsChecked == true && choice != LeaveOut) plan.SkinParts.Add(source);
		}
		foreach (var (name, box) in AnimationRows)
			if (box.IsChecked == true) plan.Animations.Add(name);
		if (plan.Animations.Count == 0 && (plan.SkinName == null || plan.SkinParts.Count == 0))
		{
			Summary.Text = "Nothing to bring in: tick some animations, or parts for a skin.";
			return;
		}
		Plan = plan;
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}

// Edit > Bring in from Animate projects… (the Windows editor's dialog): many characters' heads (or
// any other part of them) as skins here, in one go, without adding them to the project as objects.
// Each file is imported aside, matched to this object by name, and the parts on the picked bones
// (and the accessories that ride on them) become a skin named after the file.
public sealed class BatchBringInWindow : Window
{
	internal readonly ListBox FileList = new() { Height = 200, SelectionMode = SelectionMode.Multiple };
	internal readonly TextBox CategoryBox = new() { Width = 170, Text = "Head" };
	private readonly CheckBox _animations = new() { Content = "Their animations too (named …_egypt, …_pirate and so on)", Margin = new Thickness(0, 6, 0, 0) };
	private readonly CheckBox _asPlaced = new() { Content = "Line them up as they show in their own animations (right for characters that share animations)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
	private readonly CheckBox _accessories = new() { Content = "Bring the parts this object hasn't too (hats, hair, a scarf), shown only with their skin", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
	internal readonly TextBlock Problem = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Margin = new Thickness(0, 8, 0, 0) };
	private readonly List<string> _files = new();
	private readonly IReadOnlyCollection<Bone> _picked;

	// What the file picker is answered with (the tests answer it); null: the user's answer
	internal Func<Task<IReadOnlyList<string>>>? AnswerFiles { get; set; }

	public IReadOnlyList<string> Files => _files;
	public string Category => CategoryBox.Text?.Trim() ?? "";
	public bool Animations => _animations.IsChecked == true;
	public bool AsPlaced => _asPlaced.IsChecked == true;
	public bool Accessories => _accessories.IsChecked == true;

	public BatchBringInWindow(string targetName, IReadOnlyCollection<Bone> picked, IEnumerable<string> categories, string? folder)
	{
		_picked = picked;
		Title = $"Bring in from Animate projects, into {targetName}";
		Width = 640;
		SizeToContent = SizeToContent.Height;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		var panel = new StackPanel { Margin = new Thickness(14) };
		panel.Children.Add(new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Text = "Each project is imported aside (not added to this project). Its parts are matched to this object's by name, " +
				   "and the ones on the picked bones become a skin named after the file: a zombie's head, from the head bone. " +
				   (picked.Count == 0
					   ? "No bones are picked in this object: close this, pick the head bone in the rig panel, and come back."
					   : $"Picked: {string.Join(", ", picked.Select(b => b.Name))} (and the bones under them).")
		});

		panel.Children.Add(new TextBlock { Text = "Projects", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
		panel.Children.Add(FileList);
		var add = new Button { Content = "Add…", Margin = new Thickness(0, 0, 6, 0) };
		add.Click += async (_, _) =>
		{
			IReadOnlyList<string> chosen;
			if (AnswerFiles is { } answer) chosen = await answer();
			else
			{
				var start = folder != null && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
				var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
				{
					Title = "Animate projects (.fla, .xfl, the DOMDocument.xml in an XFL folder, a zipped XFL folder)",
					AllowMultiple = true,
					SuggestedStartLocation = start,
					FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Animate projects") { Patterns = new[] { "*.fla", "*.xfl", "DOMDocument.xml", "*.zip" } } }
				});
				chosen = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
			}
			AddFiles(chosen);
		};
		var remove = new Button { Content = "Remove" };
		remove.Click += (_, _) =>
		{
			foreach (var item in (FileList.SelectedItems?.Cast<object>() ?? Enumerable.Empty<object>()).OfType<string>().ToList()) _files.Remove(item);
			FileList.ItemsSource = _files.ToList();
		};
		panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { add, remove } });

		panel.Children.Add(new TextBlock { Text = "Skins", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
		var all = categories.Append("Head").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var pick = new Button { Content = "▾", Margin = new Thickness(2, 0, 0, 0) };
		pick.Click += (_, _) =>
		{
			var menu = new MenuFlyout();
			foreach (string c in all)
			{
				var item = new MenuItem { Header = c };
				item.Click += (_, _) => CategoryBox.Text = c;
				menu.Items.Add(item);
			}
			menu.ShowAt(pick);
		};
		panel.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Children = { new TextBlock { Text = "Category", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, CategoryBox, pick }
		});
		panel.Children.Add(_accessories);
		panel.Children.Add(_asPlaced);
		panel.Children.Add(_animations);
		panel.Children.Add(Problem);

		var ok = new Button { Content = "Bring them in", IsDefault = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } });
		Content = panel;
		UiSize.Fit(this);
	}

	internal void AddFiles(IEnumerable<string> files)
	{
		foreach (string f in files)
			if (!_files.Contains(f)) _files.Add(f);
		FileList.ItemsSource = _files.ToList();
	}

	internal void TryOk()
	{
		if (_files.Count == 0) Problem.Text = "Add the projects to bring in first.";
		else if (_picked.Count == 0) Problem.Text = "Pick the bones whose parts come in first (the head bone, for heads): close this and pick it in the rig panel.";
		else if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
