using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

public class PaintSession
{
	public AssetStore Store { get; }
	public UndoStack Undo { get; }
	public Skeleton Skeleton { get; }   // for keeping pivots in place on canvas resize

	public Asset? Asset { get; private set; }
	public AssetLayer? Layer { get; set; }
	public PaintTool Tool { get; set; }
	public SKColor Color { get; private set; } = SKColors.Black;
	public SKColor SecondaryColor { get; private set; } = SKColors.White;
	public bool GradientToTransparent { get; set; }
	public int Size { get; set; } = 4;
	public int Tolerance { get; set; } = 24;

	// Rectangular selection in image pixels; null = everything
	public SKRectI? Selection { get; set; }

	// Mixer
	public float MixWet { get; set; } = 0.5f;
	public float MixLoad { get; set; } = 0.2f;

	// Fill and Mixer read from the merged image instead of the active layer
	public bool SampleAllLayers { get; set; }

	// Figure tool
	public FigureShape Figure { get; set; } = FigureShape.Line;
	public FigureStyle FigureStyle { get; set; } = FigureStyle.Outline;
	public bool FigureSmooth { get; set; } = true;
	public int PolygonSides { get; set; } = 5;

	// Input state, updated by the canvas before every tool call
	public bool ShiftHeld { get; set; }
	public bool CtrlHeld { get; set; }
	public int ClickCount { get; set; }
	public float ViewZoom { get; set; } = 1f;

	// Where the last brush stroke ended (Shift+click draws a line from here)
	public Vector2? LastPoint { get; set; }

	public event Action? Changed;

	public PaintSession(AssetStore store, UndoStack undo, Skeleton skeleton, PaintTool initialTool)
	{
		Store = store;
		Undo = undo;
		Skeleton = skeleton;
		Tool = initialTool;
	}

	public SKBitmap? LayerBitmap => Layer == null ? null : Store.GetLayer(Layer);
	public SKBitmap? Composite => Asset == null ? null : Store.Get(Asset);

	public void Open(Asset asset)
	{
		Asset = asset;
		Layer = asset.Layers.LastOrDefault();
		Selection = null;
		LastPoint = null;
		Changed?.Invoke();
	}

	public void Close()
	{
		Asset = null;
		Layer = null;
		Selection = null;
		LastPoint = null;
		Changed?.Invoke();
	}

	public void SetColor(SKColor color)
	{
		Color = color;
		Changed?.Invoke();
	}
	public void SetSecondaryColor(SKColor color)
	{
		SecondaryColor = color;
		Changed?.Invoke();
	}

	public void SwapColors()
	{
		(Color, SecondaryColor) = (SecondaryColor, Color);   // tuple swap: no temp variable needed
		Changed?.Invoke();
	}

	public void CommitPixels(string name, PixelBuffer before)
	{
		if (Asset is not { } asset || LayerBitmap is not { } bmp) return;
		var after = PixelBuffer.Read(bmp);
		Undo.Push(new SnapshotCommand<PixelBuffer>($"{name} on {asset.Name}", before, after, pixels =>
		{
			pixels.WriteTo(bmp);
			asset.MarkEdited();
		}));
	}

	// ---------- selection ----------

	public void ClipToSelection(SKCanvas canvas)
	{
		if (Selection is { } r)
			canvas.ClipRect(SKRect.Create(r.Left, r.Top, r.Width, r.Height));
	}

	public bool InSelection(int x, int y) => Selection is not { } r || r.Contains(x, y);

	public void SelectAll()
	{
		if (Asset != null) Selection = new SKRectI(0, 0, Asset.Width, Asset.Height);
	}

	public void DeleteSelectedPixels()
	{
		if (Selection == null || LayerBitmap is not { } bmp || Asset is not { } asset) return;
		var before = PixelBuffer.Read(bmp);
		using (var c = new SKCanvas(bmp))
		{
			ClipToSelection(c);
			c.Clear(SKColors.Transparent);
		}
		asset.MarkEdited();
		CommitPixels("Delete pixels", before);
	}

	// ---------- layers (undoable) ----------

	public void AddLayer()
	{
		if (Asset is not { } asset) return;
		int index = Layer == null ? asset.Layers.Count : asset.Layers.IndexOf(Layer) + 1;
		EditLayers("Add layer", asset, () =>
			Layer = Store.AddLayer(asset, $"Layer {asset.Layers.Count + 1}", index: index));
	}

	public void DeleteLayer()
	{
		if (Asset is not { } asset || Layer == null || asset.Layers.Count <= 1) return;
		int i = asset.Layers.IndexOf(Layer);
		EditLayers($"Delete layer {Layer.Name}", asset, () =>
		{
			asset.Layers.RemoveAt(i);
			Layer = asset.Layers[Math.Max(0, i - 1)];
		});
	}

	public void MoveLayer(int direction)
	{
		if (Asset is not { } asset || Layer == null) return;
		int i = asset.Layers.IndexOf(Layer);
		int j = i + direction;
		if (j < 0 || j >= asset.Layers.Count) return;

		var layer = Layer;
		EditLayers($"Move layer {layer.Name}", asset, () =>
		{
			asset.Layers.RemoveAt(i);
			asset.Layers.Insert(j, layer);
		});
	}

	private void EditLayers(string name, Asset asset, Action change)
	{
		var before = asset.Layers.ToList();
		change();
		var after = asset.Layers.ToList();
		asset.MarkEdited();

		Undo.Push(new SnapshotCommand<List<AssetLayer>>(name, before, after, list =>
		{
			asset.Layers.Clear();
			asset.Layers.AddRange(list);
			asset.MarkEdited();
			if (Asset == asset && (Layer == null || !asset.Layers.Contains(Layer)))
				Layer = asset.Layers.LastOrDefault();
			Changed?.Invoke();
		}));
		Changed?.Invoke();
	}
	public void SetLayerVisible(AssetLayer layer, bool visible)
	{
		if (Asset is not { } asset || layer.Visible == visible) return;
		bool before = layer.Visible;
		Apply(visible);
		Undo.Push(new SnapshotCommand<bool>($"{(visible ? "Show" : "Hide")} {layer.Name}", before, visible, Apply));

		void Apply(bool v)   // a "local function": a small helper that lives inside this method
		{
			layer.Visible = v;
			asset.MarkEdited();
		}
	}

	public void SetLayerOpacity(AssetLayer layer, float opacity)
	{
		if (Asset is not { } asset || layer.Opacity == opacity) return;
		float before = layer.Opacity;
		Apply(opacity);
		Undo.Push(new SnapshotCommand<float>($"Opacity of {layer.Name}", before, opacity, Apply,
			mergeKey: $"opacity {layer.Id}"));   // a whole slider drag = one undo step

		void Apply(float v)
		{
			layer.Opacity = v;
			asset.MarkEdited();
		}
	}

	public void RenameLayer(AssetLayer layer, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == layer.Name) return;
		string before = layer.Name;
		layer.Name = name;
		Undo.Push(new SnapshotCommand<string>($"Rename layer to {name}", before, name, v => layer.Name = v));
	}

	// ---------- canvas size (undoable) ----------

	// anchorX/anchorY: 0 = left/top, 1 = center, 2 = right/bottom
	public void ResizeCanvas(int width, int height, int anchorX, int anchorY)
	{
		if (Asset is not { } asset || width < 1 || height < 1) return;
		if (width == asset.Width && height == asset.Height) return;
		Tool.Deactivate(this);

		int oldW = asset.Width, oldH = asset.Height;
		int ox = (width - oldW) * anchorX / 2;    // where the old image's top-left lands
		int oy = (height - oldH) * anchorY / 2;

		var before = CaptureCanvas(asset);

		var bitmaps = new Dictionary<AssetLayer, SKBitmap>();
		foreach (var layer in asset.Layers)
		{
			var bmp = new SKBitmap(AssetStore.InfoFor(width, height));
			bmp.Erase(SKColors.Transparent);
			if (Store.GetLayer(layer) is { } old)
			{
				using var c = new SKCanvas(bmp);
				c.DrawBitmap(old, ox, oy, AssetStore.Nearest, null);
			}
			bitmaps[layer] = bmp;
		}

		// Re-aim each pivot at the same pixel, so the part doesn't jump on the rig.
		// This is why pivots are stored normalized (Milestone 2).
		var pivots = new Dictionary<Attachment, Vector2>();
		foreach (var att in Skeleton.AllAttachments().Where(a => a.AssetId == asset.Id))
			pivots[att] = new Vector2((att.Pivot.X * oldW + ox) / width, (att.Pivot.Y * oldH + oy) / height);

		var after = new CanvasState(asset, width, height, bitmaps, pivots);
		RestoreCanvas(after);
		Undo.Push(new SnapshotCommand<CanvasState>(
			$"Resize {asset.Name} to {width}×{height}", before, after, RestoreCanvas));
	}

	private sealed record CanvasState(
		Asset Asset, int Width, int Height,
		Dictionary<AssetLayer, SKBitmap> Bitmaps,
		Dictionary<Attachment, Vector2> Pivots);

	private CanvasState CaptureCanvas(Asset asset)
	{
		var bitmaps = new Dictionary<AssetLayer, SKBitmap>();
		foreach (var layer in asset.Layers)
			if (Store.GetLayer(layer) is { } bmp) bitmaps[layer] = bmp;

		var pivots = Skeleton.AllAttachments()
			.Where(a => a.AssetId == asset.Id)
			.ToDictionary(a => a, a => a.Pivot);

		return new CanvasState(asset, asset.Width, asset.Height, bitmaps, pivots);
	}

	private void RestoreCanvas(CanvasState state)
	{
		state.Asset.Width = state.Width;
		state.Asset.Height = state.Height;
		foreach (var (layer, bmp) in state.Bitmaps) Store.ReplaceLayer(layer, bmp);
		foreach (var (att, pivot) in state.Pivots) att.Pivot = pivot;
		state.Asset.MarkEdited();
		Selection = null;
		LastPoint = null;
		Changed?.Invoke();
	}
}