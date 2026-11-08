using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Animator.Desktop;

// A theme, as the Windows editor's theme files have it: a colour for each of the named colours the
// interface is drawn with (Themes.Tokens). A theme file only needs the ones it changes: the rest
// come from its base (or Dark).
public sealed class Theme
{
	public string Name { get; set; } = "";
	public string? Base { get; set; }
	public Dictionary<string, string> Colors { get; set; } = new();
	public string? File { get; set; }   // (a user's theme: where it came from)
}

// Themes: the built-in ones (Dark, Light, High contrast) and the user's, JSON files in the themes
// folder (Settings > Appearance opens it, with an example to copy), the same files the Windows
// editor reads. Dark and Light are Fluent's own looks; the others colour Fluent's palette from
// their tokens. Either way the viewport, timeline and graph take theirs (CanvasTheme).
public static class Themes
{
	// Every named colour, and what it's for. (A theme file lists any of these.)
	public static readonly (string Name, string What)[] Tokens =
	{
		("Window", "Window and dialog backgrounds"),
		("Panel", "Panels: the rig panel, the timeline bar, the paint editor"),
		("PanelAlt", "Lists and trees inside panels"),
		("Control", "Buttons, boxes, menus at rest"),
		("ControlHover", "Buttons and list rows under the mouse"),
		("ControlPressed", "Buttons pressed"),
		("Input", "Text boxes and number fields"),
		("Border", "Edges of controls and dividers"),
		("Text", "Text"),
		("TextMuted", "Secondary text: hints, labels, captions"),
		("TextDisabled", "Text of controls that are off"),
		("Accent", "Chosen tabs and toggles, focus, the slider's track"),
		("AccentText", "Text on the accent colour"),
		("Selection", "Selected rows"),
		("Hint", "Hints and notes after an action"),
		("Warning", "Warnings"),
		("Error", "Errors"),
		("Success", "Things that went well; events flashing"),
		("BuildFrame", "The frame round the viewport while building"),
		("AnimateFrame", "The frame round the viewport while animating"),
		("SkinEditing", "The skin being edited"),
		("Viewport", "The viewport's background"),
		("Canvas", "The timeline's and the graph's background"),
		("CanvasText", "Text drawn on the timeline and the graph"),
		("CanvasLine", "Ticks and lines on the timeline and the graph"),
		("Playhead", "The playhead"),
	};

	public static readonly Theme Dark = new()
	{
		Name = "Dark",
		Colors = new()
		{
			["Window"] = "#1B1D22", ["Panel"] = "#24272E", ["PanelAlt"] = "#1E2026", ["Control"] = "#2F333B",
			["ControlHover"] = "#3A3F4A", ["ControlPressed"] = "#465061", ["Input"] = "#1A1C21", ["Border"] = "#3A3F4A",
			["Text"] = "#F2F4F7", ["TextMuted"] = "#9AA4B2", ["TextDisabled"] = "#6A7280", ["Accent"] = "#3A8BE0",
			["AccentText"] = "#FFFFFF", ["Selection"] = "#2F4A6E", ["Hint"] = "#E6C17A", ["Warning"] = "#F0A54A",
			["Error"] = "#FF6B6B", ["Success"] = "#8CE6A0", ["BuildFrame"] = "#B03A8BE0", ["AnimateFrame"] = "#C0E0503C",
			["SkinEditing"] = "#C87A2A", ["Viewport"] = "#282C34", ["Canvas"] = "#1E2127", ["CanvasText"] = "#C8C8C8",
			["CanvasLine"] = "#5AFFFFFF", ["Playhead"] = "#FF5A5A",
		}
	};

	public static readonly Theme Light = new()
	{
		Name = "Light",
		Colors = new()
		{
			["Window"] = "#EEF0F3", ["Panel"] = "#F7F8FA", ["PanelAlt"] = "#FFFFFF", ["Control"] = "#E4E7EC",
			["ControlHover"] = "#D7DCE4", ["ControlPressed"] = "#C5CCD8", ["Input"] = "#FFFFFF", ["Border"] = "#C3C9D3",
			["Text"] = "#1C1F24", ["TextMuted"] = "#5E6875", ["TextDisabled"] = "#A0A7B2", ["Accent"] = "#2F6FC0",
			["AccentText"] = "#FFFFFF", ["Selection"] = "#CFE0F7", ["Hint"] = "#8A5A00", ["Warning"] = "#B36200",
			["Error"] = "#C0392B", ["Success"] = "#2E8B4A", ["BuildFrame"] = "#B02F6FC0", ["AnimateFrame"] = "#C0D04030",
			["SkinEditing"] = "#C06A1A", ["Viewport"] = "#D9DDE3", ["Canvas"] = "#F4F5F7", ["CanvasText"] = "#3A3F47",
			["CanvasLine"] = "#50000000", ["Playhead"] = "#E03030",
		}
	};

	public static readonly Theme HighContrast = new()
	{
		Name = "High contrast",
		Colors = new()
		{
			["Window"] = "#000000", ["Panel"] = "#000000", ["PanelAlt"] = "#0A0A0A", ["Control"] = "#1A1A1A",
			["ControlHover"] = "#333333", ["ControlPressed"] = "#004C99", ["Input"] = "#000000", ["Border"] = "#FFFFFF",
			["Text"] = "#FFFFFF", ["TextMuted"] = "#E0E0E0", ["TextDisabled"] = "#8A8A8A", ["Accent"] = "#FFD800",
			["AccentText"] = "#000000", ["Selection"] = "#0050A0", ["Hint"] = "#FFD800", ["Warning"] = "#FFA000",
			["Error"] = "#FF4040", ["Success"] = "#40FF80", ["BuildFrame"] = "#FF00A0FF", ["AnimateFrame"] = "#FFFF3030",
			["SkinEditing"] = "#FF8C00", ["Viewport"] = "#101010", ["Canvas"] = "#000000", ["CanvasText"] = "#FFFFFF",
			["CanvasLine"] = "#B0FFFFFF", ["Playhead"] = "#FF3030",
		}
	};

	public static IReadOnlyList<Theme> BuiltIn { get; } = new[] { Dark, Light, HighContrast };

	public static Theme Current { get; private set; } = Dark;
	private static Dictionary<string, Color> _colors = Resolve(Dark);

	// After a theme is applied (what keeps colours of its own asks again)
	public static event Action? Changed;

	// (the tests keep theirs elsewhere)
	public static string UserFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Harlequin", "themes");

	// The built-in themes and the user's (a file that can't be read is left out, and said why)
	public static List<Theme> All(List<string>? problems = null)
	{
		var all = BuiltIn.ToList();
		if (!Directory.Exists(UserFolder)) return all;
		foreach (var file in Directory.GetFiles(UserFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			if (Path.GetFileName(file).Equals("example.json", StringComparison.OrdinalIgnoreCase)) continue;
			try
			{
				var theme = Read(System.IO.File.ReadAllText(file));
				theme.File = file;
				if (theme.Name.Trim().Length == 0) theme.Name = Path.GetFileNameWithoutExtension(file);
				if (all.Any(t => t.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase))) theme.Name += $" ({Path.GetFileNameWithoutExtension(file)})";
				all.Add(theme);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				problems?.Add($"{Path.GetFileName(file)}: {ex.Message}");
			}
		}
		return all;
	}

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	// A theme file: { "name": "Midnight", "base": "Dark", "colors": { "Accent": "#FF8800", … } }
	public static Theme Read(string json)
	{
		Theme theme;
		try { theme = JsonSerializer.Deserialize<Theme>(json, Options) ?? throw new InvalidDataException("It's empty."); }
		catch (JsonException ex) { throw new InvalidDataException(ex.Message, ex); }
		foreach (var (name, value) in theme.Colors)
		{
			if (!Tokens.Any(t => t.Name == name)) throw new InvalidDataException($"\"{name}\" isn't one of the theme's colours.");
			if (Parse(value) == null) throw new InvalidDataException($"\"{value}\" ({name}) isn't a colour like #RRGGBB or #AARRGGBB.");
		}
		return theme;
	}

	public static Theme Find(string? name) =>
		All().FirstOrDefault(t => t.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase)) ?? Dark;

	private static Color? Parse(string text) => Color.TryParse(text.Trim(), out var c) ? c : null;

	// What a theme is built on: itself if built in, else its base (Dark when it names none)
	private static Theme BaseOf(Theme theme) => BuiltIn.Contains(theme) ? theme
		: BuiltIn.FirstOrDefault(t => t.Name.Equals(theme.Base ?? "", StringComparison.OrdinalIgnoreCase)) ?? Dark;

	// Every colour of a theme: its own, else its base's, else Dark's
	private static Dictionary<string, Color> Resolve(Theme theme)
	{
		var baseTheme = BaseOf(theme);
		var colors = new Dictionary<string, Color>();
		foreach (var (name, _) in Tokens)
		{
			string value = theme.Colors.GetValueOrDefault(name) ?? baseTheme.Colors.GetValueOrDefault(name) ?? Dark.Colors[name];
			colors[name] = Parse(value) ?? Parse(Dark.Colors[name])!.Value;
		}
		return colors;
	}

	public static Color ColorOf(string name) => _colors.TryGetValue(name, out var c) ? c : Colors.Magenta;

	public static SKColor Sk(string name)
	{
		var c = ColorOf(name);
		return new SKColor(c.R, c.G, c.B, c.A);
	}

	// Light (Fluent's light look underneath) or dark
	public static bool IsLight(Theme theme) => BaseOf(theme) == Light;

	// The theme on the whole application
	public static void Apply(Theme theme)
	{
		Current = theme;
		_colors = Resolve(theme);
		CanvasTheme.Colour = Sk;
		if (Application.Current is { } app)
		{
			var variant = IsLight(theme) ? ThemeVariant.Light : ThemeVariant.Dark;
			if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
			{
				fluent.Palettes.Remove(ThemeVariant.Dark);
				fluent.Palettes.Remove(ThemeVariant.Light);
				if (theme != Dark && theme != Light) fluent.Palettes[variant] = Palette();
			}
			// (the variant set again, so a palette changed under the same one shows)
			app.RequestedThemeVariant = variant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
			app.RequestedThemeVariant = variant;
		}
		Changed?.Invoke();
	}

	// Fluent's palette from the theme's colours
	private static ColorPaletteResources Palette()
	{
		Color C(string name) => ColorOf(name);
		return new ColorPaletteResources
		{
			Accent = C("Accent"),
			RegionColor = C("Window"),
			ErrorText = C("Error"),
			BaseHigh = C("Text"),
			BaseMediumHigh = C("Text"),
			BaseMedium = C("TextMuted"),
			BaseMediumLow = C("TextDisabled"),
			BaseLow = C("Border"),
			AltHigh = C("Window"),
			AltMediumHigh = C("Panel"),
			AltMedium = C("Control"),
			AltMediumLow = C("Control"),
			AltLow = C("Input"),
			ChromeAltLow = C("Text"),
			ChromeBlackHigh = C("Window"),
			ChromeBlackMedium = C("Window"),
			ChromeBlackMediumLow = C("Window"),
			ChromeBlackLow = C("Window"),
			ChromeDisabledHigh = C("Border"),
			ChromeDisabledLow = C("TextDisabled"),
			ChromeGray = C("TextMuted"),
			ChromeHigh = C("Border"),
			ChromeLow = C("PanelAlt"),
			ChromeMedium = C("Control"),
			ChromeMediumLow = C("Panel"),
			ChromeWhite = C("Text"),
			ListLow = C("ControlHover"),
			ListMedium = C("ControlPressed")
		};
	}

	// The themes folder, with an example theme in it to copy (every colour, from Dark)
	public static string PrepareFolder()
	{
		Directory.CreateDirectory(UserFolder);
		string example = Path.Combine(UserFolder, "example.json");
		var sample = new Theme { Name = "My theme", Base = "Dark", Colors = new Dictionary<string, string>(Dark.Colors) };
		string notes = "// A Harlequin theme. Copy this file (any name, .json), change its name and the colours you want;\n" +
			"// leave out the rest (they come from \"base\": Dark, Light or High contrast). Colours: #RRGGBB or #AARRGGBB.\n" +
			"// What each one colours:\n" + string.Join("\n", Tokens.Select(t => $"//   {t.Name}: {t.What}")) + "\n";
		System.IO.File.WriteAllText(example, notes + JsonSerializer.Serialize(new { sample.Name, sample.Base, sample.Colors }, Options));
		return UserFolder;
	}
}
