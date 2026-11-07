namespace Animator.Viewer.Painting;

// The paint editor's tools, one of each, by the name its tool buttons go by (Select, Wand, Move,
// Pencil, Brush, Mixer, Eraser, Picker, Fill, Gradient, Figure), and which options each one uses
public sealed class PaintToolbox
{
	public ShapeSelectTool Select { get; } = new();
	public MagicWandTool Wand { get; } = new();
	public MoveTool Move { get; } = new();
	public StrokeTool Pencil { get; } = new(StrokeTool.Kind.Pencil);
	public StrokeTool Brush { get; } = new(StrokeTool.Kind.Brush);
	public MixerTool Mixer { get; } = new();
	public StrokeTool Eraser { get; } = new(StrokeTool.Kind.Eraser);
	public EyedropperTool Picker { get; } = new();
	public FillTool Fill { get; } = new();
	public GradientTool Gradient { get; } = new();
	public FigureTool Figure { get; } = new();

	public static readonly IReadOnlyList<string> Names =
		new[] { "Select", "Wand", "Move", "Pencil", "Brush", "Mixer", "Eraser", "Picker", "Fill", "Gradient", "Figure" };

	// The tool of that name (null: there's none)
	public PaintTool? Named(string name) => name switch
	{
		"Select" => Select,
		"Wand" => Wand,
		"Move" => Move,
		"Pencil" => Pencil,
		"Brush" => Brush,
		"Mixer" => Mixer,
		"Eraser" => Eraser,
		"Picker" => Picker,
		"Fill" => Fill,
		"Gradient" => Gradient,
		"Figure" => Figure,
		_ => null
	};

	public string? NameOf(PaintTool tool) => Names.FirstOrDefault(n => Named(n) == tool);

	// Which options a tool uses (the panel shows only those)
	public bool DrawsStrokes(PaintTool t) => t == Pencil || t == Brush || t == Eraser || t == Mixer;
	public bool BySimilarColour(PaintTool t) => t == Fill || t == Wand;
	public bool PicksShapes(PaintTool t) => t == Select || t == Wand;

	// A half-made figure or selection outline being placed point by point
	public bool PlacingPoints => Figure.InProgress || Select.PlacingPoints;
}
