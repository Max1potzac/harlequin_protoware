using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

public class PaintSession
{
	public AssetStore Store { get; }
	public UndoStack Undo { get; }
	public Skeleton Skeleton { get; set; }   // the edited object's rig: keeps pivots in place on canvas resize

	public Asset? Asset { get; private set; }
	public AssetLayer? Layer { get; set; }
	public PaintTool Tool { get; set; }
	public SKColor Color { get; private set; } = SKColors.Black;
	public SKColor SecondaryColor { get; private set; } = SKColors.White;
	public bool GradientToTransparent { get; set; }
	public int Size { get; set; } = 4;
	public int Tolerance { get; set; } = 24;

	// The selected pixels; null = nothing selected (then every tool works on the whole layer).
	// Set it through SetSelection to make the change undoable.
	public SelectionMask? Selection { get; set; }

	// Selection tools: how a new shape combines with the current selection
	// (Shift / Alt / Shift+Alt override this while you click)
	public SelectMode SelectMode { get; set; } = SelectMode.New;
	public SelectShape SelectShape { get; set; } = SelectShape.Rectangle;

	// Wand and Fill: only the area connected to the click (true), or that colour everywhere
	public bool Contiguous { get; set; } = true;

	// Mixer
	public float MixWet { get; set; } = 0.5f;
	public float MixLoad { get; set; } = 0.2f;

	// Fill, Mixer and Wand read from the merged image instead of the active layer
	public bool SampleAllLayers { get; set; }

	// Figure tool
	public FigureShape Figure { get; set; } = FigureShape.Line;
	public FigureStyle FigureStyle { get; set; } = FigureStyle.Outline;
	public bool FigureSmooth { get; set; } = true;
	public int PolygonSides { get; set; } = 5;

	// Input state, updated by the canvas before every tool call
	public bool ShiftHeld { get; set; }
	public bool CtrlHeld { get; set; }
	public bool AltHeld { get; set; }
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

	// preferredLayer: keep painting on the same layer number when switching between drawings
	// that share a layer setup (lines, colors…); -1 = the top layer
	public void Open(Asset asset, int preferredLayer = -1)
	{
		Asset = asset;
		Layer = preferredLayer >= 0 && preferredLayer < asset.Layers.Count
			? asset.Layers[preferredLayer]
			: asset.Layers.LastOrDefault();
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
		if (Selection is { } mask) canvas.ClipRegion(mask.Region);
	}

	public bool InSelection(int x, int y) => Selection is not { } mask || mask.Contains(x, y);

	// The mode a selection tool uses for this click: the modifier keys win over the mode box
	public SelectMode ModeForClick() => (ShiftHeld, AltHeld) switch
	{
		(true, true) => SelectMode.Intersect,
		(true, false) => SelectMode.Add,
		(false, true) => SelectMode.Subtract,
		_ => SelectMode
	};

	// Changes the selection as one undo step (like Photoshop, so a misclick can be taken back)
	public void SetSelection(SelectionMask? mask, string name)
	{
		if (Asset is not { } asset || ReferenceEquals(mask, Selection)) return;
		var before = Selection;
		Selection = mask;
		Undo.Push(new SnapshotCommand<SelectionMask?>(name, before, mask, m => RestoreSelection(asset, m)));
	}

	// Combines a new shape with the current selection, as one undo step
	public void ApplySelection(SelectionMask shape, SelectMode mode, string name) =>
		SetSelection(SelectionMask.Combine(Selection, shape, mode), name);

	private void RestoreSelection(Asset asset, SelectionMask? mask)
	{
		if (Asset != asset) return;   // another drawing is open: its selection isn't this one
		Selection = mask != null && mask.Width == asset.Width && mask.Height == asset.Height ? mask : null;
	}

	public void SelectAll()
	{
		if (Asset is { } asset) SetSelection(SelectionMask.All(asset.Width, asset.Height), "Select all");
	}

	public void Deselect() => SetSelection(null, "Deselect");

	public void InvertSelection()
	{
		if (Asset is not { } asset) return;
		var inverted = Selection == null ? SelectionMask.All(asset.Width, asset.Height) : Selection.Inverted();
		SetSelection(inverted, "Invert selection");
	}

	// Pixels and selection together, as one undo step (the Move tool carries both)
	public void CommitMove(PixelBuffer pixelsBefore, SelectionMask? selectionBefore)
	{
		if (Asset is not { } asset || LayerBitmap is not { } bmp) return;
		var before = (pixelsBefore, selectionBefore);
		var after = (PixelBuffer.Read(bmp), Selection);
		Undo.Push(new SnapshotCommand<(PixelBuffer Pixels, SelectionMask? Selection)>(
			$"Move on {asset.Name}", before, after, state =>
			{
				state.Pixels.WriteTo(bmp);
				asset.MarkEdited();
				RestoreSelection(asset, state.Selection);
			}));
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

	// A colour layer above the active layer: it changes the colours of the layers under it
	public void AddColorLayer(ColorAdjustKind kind)
	{
		if (Asset is not { } asset) return;
		int index = Layer == null ? asset.Layers.Count : asset.Layers.IndexOf(Layer) + 1;
		string name = ColorAdjust.KindName(kind);
		EditLayers($"Add a colour layer ({name})", asset, () =>
		{
			var layer = Store.AddLayer(asset, name, index: index);
			layer.Adjust = new ColorAdjust(kind);
			Layer = layer;
		});
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