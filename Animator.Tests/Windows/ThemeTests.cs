using System.IO;
using Animator.Viewer.Theming;

namespace Animator.Tests;

// Themes: the built-in ones are complete; a user's file names only what it changes, from a base,
// and anything wrong with it is said
[Collection("Themes")]
public class ThemeTests
{
	[Fact]
	public void The_built_in_themes_give_every_colour()
	{
		foreach (var theme in Themes.BuiltIn)
			foreach (var (name, _) in Themes.Tokens)
				Assert.True(theme.Colors.ContainsKey(name), $"{theme.Name} has no {name}");
		Assert.Equal(Themes.Tokens.Length, Themes.Tokens.Select(t => t.Name).Distinct().Count());
	}

	[Fact]
	public void A_theme_file_changes_some_colours_and_takes_the_rest_from_its_base()
	{
		var theme = Themes.Read("""
			{
			  // a test theme
			  "name": "Ember",
			  "base": "Light",
			  "colors": { "Accent": "#FF8800", "Playhead": "#80FF0000", },
			}
			""");
		Assert.Equal(("Ember", "Light"), (theme.Name, theme.Base));
		var before = Themes.Current;
		try
		{
			Themes.Apply(theme);
			Assert.Equal(new SkiaSharp.SKColor(0xFF, 0x88, 0x00), Themes.Sk("Accent"));
			Assert.Equal(new SkiaSharp.SKColor(0xFF, 0x00, 0x00, 0x80), Themes.Sk("Playhead"));
			Assert.Equal(Themes.Sk("Window"), SkiaSharp.SKColor.Parse(Themes.Light.Colors["Window"]));   // from Light
			Assert.Equal(40, Themes.SkAlpha("Text", 40).Alpha);
		}
		finally
		{
			Themes.Apply(before);
		}

		Assert.Throws<InvalidDataException>(() => Themes.Read("""{ "name": "x", "colors": { "Nope": "#000000" } }"""));
		Assert.Throws<InvalidDataException>(() => Themes.Read("""{ "name": "x", "colors": { "Text": "very red" } }"""));
		Assert.Throws<InvalidDataException>(() => Themes.Read("{ not json"));
	}
}
