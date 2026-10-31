using SkiaSharp;

namespace Animator.Viewer.Rendering;

// Owns all pixels. The core only knows asset and layer IDs.
public class AssetStore
{
	public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

	// Layer id -> pixels. Entries are never removed, so undo can bring deleted layers back.
	private readonly Dictionary<Guid, SKBitmap> _layers = new();

	// Asset id -> merged image, plus the asset version it was built from
	private readonly Dictionary<Guid, (int Version, SKBitmap? Image)> _composites = new();
	private readonly Dictionary<Guid, (int Version, SKBitmap? Image)> _plain = new();   // without colour layers

	public static SKImageInfo InfoFor(int width, int height) =>
		new(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);

	public AssetLayer AddLayer(Asset asset, string name, SKBitmap? pixels = null, int index = -1)
	{
		var bmp = new SKBitmap(InfoFor(asset.Width, asset.Height));
		bmp.Erase(SKColors.Transparent);
		if (pixels != null)
		{
			using var canvas = new SKCanvas(bmp);
			canvas.DrawBitmap(pixels, 0, 0, Nearest, null);
		}

		var layer = new AssetLayer { Name = name };
		_layers[layer.Id] = bmp;

		if (index < 0 || index > asset.Layers.Count) asset.Layers.Add(layer);
		else asset.Layers.Insert(index, layer);

		asset.MarkEdited();
		return layer;
	}
	public void ReplaceLayer(AssetLayer layer, SKBitmap bitmap) => _layers[layer.Id] = bitmap;

	// For copied objects: the new layer gets its own copy of the old layer's pixels
	public void CopyPixels(Guid fromLayer, Guid toLayer)
	{
		if (!_layers.TryGetValue(fromLayer, out var source)) return;
		var copy = new SKBitmap(source.Info);
		copy.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(copy))
			canvas.DrawBitmap(source, 0, 0, Nearest, null);   // over transparent = an exact copy
		_layers[toLayer] = copy;
	}

	// Single-image convenience: replaces all layers with one "Base" layer
	public void Set(Asset asset, SKBitmap bitmap)
	{
		asset.Width = bitmap.Width;
		asset.Height = bitmap.Height;
		asset.Layers.Clear();
		AddLayer(asset, "Base", bitmap);
	}

	public void LoadFromFile(Asset asset, string path)
	{
		using var bmp = SKBitmap.Decode(path);
		if (bmp != null) Set(asset, bmp);
	}
	// For loading: gives an existing layer description its pixels (null = empty layer)
	public void AttachLayer(Asset asset, AssetLayer layer, SKBitmap? pixels)
	{
		var bmp = new SKBitmap(InfoFor(asset.Width, asset.Height));
		bmp.Erase(SKColors.Transparent);
		if (pixels != null)
		{
			using var canvas = new SKCanvas(bmp);
			canvas.DrawBitmap(pixels, 0, 0, Nearest, null);   // also converts to our pixel format
		}
		_layers[layer.Id] = bmp;
		asset.MarkEdited();
	}

	// Takes over another store's pixels (used when opening a file)
	public void ReplaceWith(AssetStore other)
	{
		_layers.Clear();
		foreach (var (id, bmp) in other._layers) _layers[id] = bmp;
		_composites.Clear();
		_plain.Clear();
	}

	public void Clear()
	{
		_layers.Clear();
		_composites.Clear();
		_plain.Clear();
	}
	public SKBitmap? GetLayer(AssetLayer layer) =>
		_layers.TryGetValue(layer.Id, out var b) ? b : null;

	// The merged image the rig draws. Rebuilt only when the asset's version changed. Colour layers
	// change the layers under them; colorLayers false: the picture without them (an export that
	// doesn't bake them in).
	public SKBitmap? Get(Asset asset, bool colorLayers = true)
	{
		if (!colorLayers && asset.Layers.Any(l => l.IsColorLayer && l.Visible))
			return Merge(asset, _plain, withColor: false);
		return Merge(asset, _composites, withColor: true);
	}

	private SKBitmap? Merge(Asset asset, Dictionary<Guid, (int Version, SKBitmap? Image)> cache, bool withColor)
	{
		if (asset.Width <= 0 || asset.Height <= 0) return null;

		cache.TryGetValue(asset.Id, out var cached);
		if (cached.Image != null && cached.Version == asset.Version) return cached.Image;

		var image = cached.Image != null && cached.Image.Width == asset.Width && cached.Image.Height == asset.Height
			? cached.Image
			: new SKBitmap(InfoFor(asset.Width, asset.Height));

		using (var clear = new SKCanvas(image)) clear.Clear(SKColors.Transparent);
		using var paint = new SKPaint();
		foreach (var layer in asset.Layers)   // bottom to top
		{
			if (!layer.Visible || layer.Opacity <= 0) continue;
			if (layer.Adjust is { } adjust)
			{
				if (withColor) ColorFilters.ApplyTo(image, adjust, Math.Clamp(layer.Opacity, 0f, 1f));
				continue;
			}
			if (GetLayer(layer) is not { } bmp) continue;
			paint.Color = SKColors.White.WithAlpha((byte)(Math.Clamp(layer.Opacity, 0f, 1f) * 255));
			using var canvas = new SKCanvas(image);
			canvas.DrawBitmap(bmp, 0, 0, Nearest, paint);
		}

		cache[asset.Id] = (asset.Version, image);
		return image;
	}
}