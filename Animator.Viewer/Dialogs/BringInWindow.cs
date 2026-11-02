using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer.Dialogs;

// Edit → Bring in from another object…: another object's animations onto this one's bones, and
// its drawings (a head, an outfit) as a skin here. Bones, parts and drawings are matched by
// name, past each object's prefix ("zombie_egypt_jaw" ↔ "zombie_pirate_basic_jaw"); every match
// can be changed. Built in code (no XAML). OK leaves the source and the plan in Source and Plan.
public sealed class BringInWindow : Window
{
	private const string None = "(none)";
	private const string LeaveOut = "(leave it out)";
	private const string AddNew = "(add it as a new part)";

	private readonly Skeleton _target;
	private readonly Func<IReadOnlyCollection<Bone>> _pickedBones;
	private readonly ComboBox _from = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Name" };
	private readonly TextBox _sourcePrefix = new() { Width = 170 };
	private readonly TextBox _targetPrefix = new() { Width = 170 };
	private readonly Grid _bones = new() { Margin = new Thickness(0, 4, 0, 0) };
	private readonly Grid _parts = new() { Margin = new Thickness(0, 4, 0, 0) };
	private readonly Expander _bonesBox = new() { Margin = new Thickness(0, 8, 0, 0) };
	private readonly CheckBox _makeSkin = new() { Content = "Its ticked parts' drawings become a skin here", IsChecked = true, Margin = new Thickness(0, 6, 0, 4) };
	private readonly TextBox _skinName = new() { Width = 170 };
	private readonly ComboBox _category = new() { Width = 170, IsEditable = true };
	private readonly CheckBox _overlay = new() { Content = "Overlay (shows over the other skins)", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
	private readonly StackPanel _animations = new();
	private readonly TextBox _suffix = new() { Width = 170 };
	private readonly RadioButton _absolute = new() { Content = "Poses as they look (worked out again from this object's rest pose)", IsChecked = true, GroupName = "how" };
	private readonly RadioButton _offsets = new() { Content = "Keys as they are (changes from the rest pose)", GroupName = "how" };
	private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

	private readonly List<(string Source, ComboBox To)> _boneRows = new();
	private readonly List<(string Source, CheckBox Skin, ComboBox To)> _partRows = new();
	private readonly List<(string Name, CheckBox Box)> _animationRows = new();
	private bool _filling;

	public SceneObject? Source => _from.SelectedItem as SceneObject;
	public MergePlan Plan { get; private set; } = new();

	public BringInWindow(string targetName, Skeleton target, IReadOnlyList<SceneObject> sources, IEnumerable<string> categories,
		Func<IReadOnlyCollection<Bone>> pickedBones)
	{
		_target = target;
		_pickedBones = pickedBones;
		Title = $"Bring in from another object, into {targetName}";
		Width = 760;
		Height = 820;
		MinWidth = 560;
		MinHeight = 420;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		SetResourceReference(BackgroundProperty, Themes.Key("Window"));
		SetResourceReference(ForegroundProperty, Themes.Key("Text"));

		var panel = new StackPanel { Margin = new Thickness(14) };
		panel.Children.Add(Note("Another object's animations come onto this object's bones, and its drawings (a head, an outfit) can become a skin here. " +
			"Bones, parts and drawings are matched by name, setting aside what each object's names start with. Change any match below. " +
			"Import the other object first (File → Import…), into this project."));

		panel.Children.Add(Heading("From"));
		_from.ItemsSource = sources;
		_from.SelectionChanged += (_, _) => Suggest();
		panel.Children.Add(_from);

		panel.Children.Add(Heading("Names"));
		var names = new WrapPanel();
		names.Children.Add(Label("Its names start with"));
		names.Children.Add(_sourcePrefix);
		names.Children.Add(Label("   this object's with"));
		names.Children.Add(_targetPrefix);
		var again = new Button { Content = "Match again", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0) };
		again.Click += (_, _) => Suggest(keepPrefixes: true);
		names.Children.Add(again);
		panel.Children.Add(names);

		_bonesBox.Content = _bones;
		panel.Children.Add(_bonesBox);

		panel.Children.Add(Heading("Parts"));
		panel.Children.Add(Note("Ticked: its drawings go into the skin. A part with no match here can be added as a new part, shown only while the skin is on " +
			"(or always, if it isn't ticked)."));
		var ticks = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
		Button Tick(string text, string tip, Func<string, bool> which)
		{
			var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
			b.Click += (_, _) =>
			{
				foreach (var row in _partRows) row.Skin.IsChecked = which(row.Source);
			};
			ticks.Children.Add(b);
			return b;
		}
		Tick("Tick all", "Every part's drawings go into the skin", _ => true);
		Tick("Tick none", "", _ => false);
		Tick("Tick those on the picked bones", "The parts that would go on the bones picked in this object (and the bones under them): pick the head bone first for a head",
			source => OnPickedBones(source));
		panel.Children.Add(ticks);
		panel.Children.Add(_parts);

		panel.Children.Add(Heading("Skin"));
		panel.Children.Add(_makeSkin);
		var skin = new WrapPanel();
		skin.Children.Add(Label("Name"));
		skin.Children.Add(_skinName);
		skin.Children.Add(Label("   Category"));
		_category.ItemsSource = categories.Append("Head").Append("Body").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		_category.Text = "Head";
		skin.Children.Add(_category);
		skin.Children.Add(_overlay);
		panel.Children.Add(skin);

		panel.Children.Add(Heading("Animations"));
		panel.Children.Add(_animations);
		var suffix = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
		suffix.Children.Add(Label("Their names here end with"));
		suffix.Children.Add(_suffix);
		panel.Children.Add(suffix);
		panel.Children.Add(_absolute);
		panel.Children.Add(_offsets);
		panel.Children.Add(_summary);

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
		var ok = new Button { Content = "Bring in", IsDefault = true, Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) => Finish();
		var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 2, 14, 2) };
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		panel.Children.Add(buttons);

		Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
		if (sources.Count > 0) _from.SelectedIndex = 0;
	}

	private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) };

	private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

	private static TextBlock Note(string text)
	{
		var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
		t.SetResourceReference(TextBlock.ForegroundProperty, Themes.Key("TextMuted"));
		return t;
	}

	// ======================= filling it in =======================

	private void Suggest(bool keepPrefixes = false)
	{
		if (Source is not { } source) return;
		_filling = true;
		var plan = ObjectMerge.Suggest(source.Skeleton, source.Animations, _target);
		if (keepPrefixes)
		{
			plan.SourcePrefix = _sourcePrefix.Text;
			plan.TargetPrefix = _targetPrefix.Text;
			plan.Bones.Clear();
			plan.Parts.Clear();
			foreach (var (s, t) in ObjectMerge.Match(source.Skeleton.AllBones().Select(b => b.Name), _target.AllBones().Select(b => b.Name), plan.SourcePrefix, plan.TargetPrefix))
				plan.Bones[s] = t;
			foreach (var (s, t) in ObjectMerge.Match(source.Skeleton.Slots.Select(x => x.Name), _target.Slots.Select(x => x.Name), plan.SourcePrefix, plan.TargetPrefix))
				plan.Parts[s] = t;
		}
		_sourcePrefix.Text = plan.SourcePrefix;
		_targetPrefix.Text = plan.TargetPrefix;

		// Bones
		_bones.Children.Clear();
		_bones.RowDefinitions.Clear();
		_bones.ColumnDefinitions.Clear();
		_bones.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		_bones.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		_boneRows.Clear();
		var targetBones = new List<string> { None };
		targetBones.AddRange(_target.AllBones().Select(b => b.Name));
		foreach (var bone in source.Skeleton.AllBones())
		{
			var to = new ComboBox { ItemsSource = targetBones, SelectedItem = plan.Bones.GetValueOrDefault(bone.Name) ?? None, Margin = new Thickness(0, 0, 0, 2) };
			Row(_bones, bone.Name, null, to);
			_boneRows.Add((bone.Name, to));
		}
		int matchedBones = plan.Bones.Values.Count(v => v != null);
		_bonesBox.Header = $"Bones: {matchedBones} of {plan.Bones.Count} matched (open to change)";
		_bonesBox.IsExpanded = matchedBones < plan.Bones.Count / 2;

		// Parts, front first (as the Order view lists them)
		_parts.Children.Clear();
		_parts.RowDefinitions.Clear();
		_parts.ColumnDefinitions.Clear();
		_parts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		_parts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		_parts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		_partRows.Clear();
		var targetParts = new List<string> { LeaveOut, AddNew };
		targetParts.AddRange(_target.Slots.Select(s => s.Name));
		foreach (var slot in Enumerable.Reverse(source.Skeleton.Slots))
		{
			var match = plan.Parts.GetValueOrDefault(slot.Name);
			var skin = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 2), ToolTip = "Its drawings go into the skin" };
			var to = new ComboBox { ItemsSource = targetParts, SelectedItem = match ?? AddNew, Margin = new Thickness(0, 0, 0, 2) };
			Row(_parts, slot.Name, skin, to);
			_partRows.Add((slot.Name, skin, to));
		}

		// Animations
		_animations.Children.Clear();
		_animationRows.Clear();
		foreach (var animation in source.Animations)
		{
			var box = new CheckBox { Content = animation.Name, IsChecked = true, Margin = new Thickness(0, 0, 0, 2) };
			_animations.Children.Add(box);
			_animationRows.Add((animation.Name, box));
		}
		string word = ObjectMerge.Core(source.Name, "zombie_").Replace(".pam", "");
		_suffix.Text = "_" + word;
		_skinName.Text = word;
		_summary.Text = $"{plan.Parts.Values.Count(v => v != null)} of {plan.Parts.Count} parts and {matchedBones} of {plan.Bones.Count} bones match by name.";
		_filling = false;
	}

	private static void Row(Grid grid, string name, CheckBox? tick, ComboBox to)
	{
		int row = grid.RowDefinitions.Count;
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
		var row = _partRows.First(r => r.Source == sourcePart);
		Bone? bone = row.To.SelectedItem is string name && _target.FindSlot(name) is { } slot ? slot.Bone : null;
		if (bone == null && source.Skeleton.FindSlot(sourcePart) is { } theirs)
			for (var b = theirs.Bone; b != null && bone == null; b = b.Parent)
				if (_boneRows.FirstOrDefault(r => r.Source == b.Name).To?.SelectedItem is string mapped && mapped != None)
					bone = _target.FindBone(mapped);
		return bone != null && picked.Any(p => bone == p || bone.IsDescendantOf(p));
	}

	private void Finish()
	{
		if (Source == null)
		{
			_summary.Text = "There's no other object to bring in from: import it first (File → Import…).";
			return;
		}
		var plan = new MergePlan
		{
			SourcePrefix = _sourcePrefix.Text,
			TargetPrefix = _targetPrefix.Text,
			Suffix = _suffix.Text,
			Absolute = _absolute.IsChecked == true,
			SkinName = _makeSkin.IsChecked == true ? (_skinName.Text.Trim().Length > 0 ? _skinName.Text.Trim() : "Skin") : null,
			SkinCategory = _category.Text ?? "",
			SkinOverlay = _overlay.IsChecked == true
		};
		foreach (var (source, to) in _boneRows)
			plan.Bones[source] = to.SelectedItem is string name && name != None ? name : null;
		foreach (var (source, skin, to) in _partRows)
		{
			string? choice = to.SelectedItem as string;
			plan.Parts[source] = choice is null or LeaveOut or AddNew ? null : choice;
			if (choice == AddNew) plan.NewParts.Add(source);
			if (skin.IsChecked == true && choice != LeaveOut) plan.SkinParts.Add(source);
		}
		foreach (var (name, box) in _animationRows)
			if (box.IsChecked == true) plan.Animations.Add(name);
		if (plan.Animations.Count == 0 && (plan.SkinName == null || plan.SkinParts.Count == 0))
		{
			_summary.Text = "Nothing to bring in: tick some animations, or parts for a skin.";
			return;
		}
		Plan = plan;
		DialogResult = true;
	}
}
