using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Animator.Tests;

// Writes small Animate (XFL) projects for the tests, laid out the way Animate (or a PvZ2
// converter) lays them out: DOMDocument.xml, each symbol in LIBRARY/<its name>.xml, pictures as
// PNG files in LIBRARY. Saved as a folder, or zipped up as a .fla.
internal sealed class XflTestProject
{
	public float Width { get; init; } = 550;
	public float Height { get; init; } = 400;
	public float FrameRate { get; init; } = 24;

	private readonly Dictionary<string, byte[]> _files = new();   // path inside the project → contents
	private readonly List<string> _media = new();
	private readonly List<string> _includes = new();
	private readonly List<string> _scenes = new();

	private const string Namespace = "http://ns.adobe.com/xfl/2008/";

	// A picture of one colour, w × h pixels (an ellipse filling it when `round`), as LIBRARY/<name>.png
	public void Picture(string name, int width, int height, SKColor colour, bool round = false)
	{
		using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
		bitmap.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(bitmap))
		using (var paint = new SKPaint { Color = colour, IsAntialias = false })
		{
			if (round) canvas.DrawOval(new SKRect(0, 0, width, height), paint);
			else canvas.DrawRect(new SKRect(0, 0, width, height), paint);
		}
		using var image = SKImage.FromBitmap(bitmap);
		using var png = image.Encode(SKEncodedImageFormat.Png, 100);
		_files[$"LIBRARY/{name}.png"] = png.ToArray();
		_media.Add($"<DOMBitmapItem name=\"{name}\" href=\"{name}.png\" frameRight=\"{width * 20}\" frameBottom=\"{height * 20}\"/>");
	}

	// A symbol, its layers (top layer first) as XML
	public void Symbol(string name, string layers, string type = "graphic")
	{
		_files[$"LIBRARY/{name}.xml"] = Encoding.UTF8.GetBytes(
			$"<DOMSymbolItem xmlns=\"{Namespace}\" name=\"{name}\" symbolType=\"{type}\">" +
			$"<timeline><DOMTimeline name=\"{name}\"><layers>{layers}</layers></DOMTimeline></timeline></DOMSymbolItem>");
		_includes.Add($"<Include href=\"{name}.xml\"/>");
	}

	public void Scene(string name, string layers) =>
		_scenes.Add($"<DOMTimeline name=\"{name}\"><layers>{layers}</layers></DOMTimeline>");

	private byte[] Document() => Encoding.UTF8.GetBytes(
		$"<DOMDocument xmlns=\"{Namespace}\" width=\"{F(Width)}\" height=\"{F(Height)}\" frameRate=\"{F(FrameRate)}\" xflVersion=\"2.97\">" +
		$"<media>{string.Concat(_media)}</media><symbols>{string.Concat(_includes)}</symbols>" +
		$"<timelines>{string.Concat(_scenes)}</timelines></DOMDocument>");

	// Saved in a new folder; returns its DOMDocument.xml
	public string SaveFolder(string folder)
	{
		Directory.CreateDirectory(folder);
		File.WriteAllBytes(Path.Combine(folder, "DOMDocument.xml"), Document());
		File.WriteAllText(Path.Combine(folder, Path.GetFileName(folder) + ".xfl"), "PROXY-CS5");
		foreach (var (path, bytes) in _files)
		{
			string full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllBytes(full, bytes);
		}
		return Path.Combine(folder, "DOMDocument.xml");
	}

	// Zipped up like a .fla
	public string SaveFla(string path)
	{
		using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			void Add(string name, byte[] bytes)
			{
				using var stream = zip.CreateEntry(name).Open();
				stream.Write(bytes, 0, bytes.Length);
			}
			Add("DOMDocument.xml", Document());
			foreach (var (name, bytes) in _files) Add(name, bytes);
		}
		return path;
	}

	// ----- pieces of XML -----

	public static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

	public static string Layer(string name, params string[] frames) => LayerWith("", name, frames);

	// A layer with attributes ("layerType=\"guide\"", "visible=\"false\"")
	public static string LayerWith(string attributes, string name, params string[] frames) =>
		$"<DOMLayer name=\"{name}\" {attributes}><frames>{string.Concat(frames)}</frames></DOMLayer>";

	public static string Frame(int index, int duration, string elements = "", string attributes = "") =>
		$"<DOMFrame index=\"{index}\" duration=\"{duration}\" {attributes}><elements>{elements}</elements></DOMFrame>";

	public static string MatrixXml(Matrix3x2 m) =>
		$"<matrix><Matrix a=\"{F(m.M11)}\" b=\"{F(m.M12)}\" c=\"{F(m.M21)}\" d=\"{F(m.M22)}\" tx=\"{F(m.M31)}\" ty=\"{F(m.M32)}\"/></matrix>";

	// A symbol placed on a frame: a graphic symbol playing by `loop` ("loop", "play once",
	// "single frame"), or with type "movie clip" one playing on its own
	public static string Instance(string symbol, Matrix3x2 m, Vector2? pivot = null, string loop = "loop", string type = "graphic",
		string attributes = "", float alpha = 1f)
	{
		string point = pivot is { } p ? $"<transformationPoint><Point x=\"{F(p.X)}\" y=\"{F(p.Y)}\"/></transformationPoint>" : "";
		string colour = alpha < 1f ? $"<color><Color alphaMultiplier=\"{F(alpha)}\"/></color>" : "";
		string playing = type == "graphic" ? $"loop=\"{loop}\"" : "";
		return $"<DOMSymbolInstance libraryItemName=\"{symbol}\" symbolType=\"{type}\" {playing} {attributes}>{MatrixXml(m)}{point}{colour}</DOMSymbolInstance>";
	}

	public static string Bitmap(string name, Matrix3x2 m) =>
		$"<DOMBitmapInstance libraryItemName=\"{name}\">{MatrixXml(m)}</DOMBitmapInstance>";

	// A vector shape filled with one colour, its outline in Animate's edge format (twips)
	public static string Shape(string edges, string colour = "#3366CC") =>
		$"<DOMShape><fills><FillStyle index=\"1\"><SolidColor color=\"{colour}\"/></FillStyle></fills>" +
		$"<edges><Edge fillStyle1=\"1\" edges=\"{edges}\"/></edges></DOMShape>";
}
