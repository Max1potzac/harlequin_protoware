namespace Animator.Viewer;

// Colour layers (EditorSession's side of the Colour view and the paint editor's colour layers):
// the open object's added, deleted, moved, renamed, shown, leaving parts out, their settings
// changed; their numbers (and a drawing's colour layers' numbers) set at rest or keyed at the
// playhead. Each edit is one undo step; dragging a slider makes one too (they merge).
public sealed partial class EditorSession
{
	// One undo step for the colour layers themselves. Change: whether it changed anything.
	public bool EditColourLayers(string name, Func<bool> change)
	{
		var before = ColorLayersSnapshot.Capture(Rig);
		if (!change()) return false;
		State.Undo.Push(new SnapshotCommand<ColorLayersSnapshot>(name, before, ColorLayersSnapshot.Capture(Rig), s => s.Restore()));
		return true;
	}

	// ...and with their keys in every animation (renaming, deleting)
	public bool EditColourAndKeys(string name, Func<bool> change)
	{
		var layers = ColorLayersSnapshot.Capture(Rig);
		var rest = State.CaptureStructure();
		if (!change()) return false;
		State.Undo.Push(new SnapshotCommand<(StructureSnapshot, ColorLayersSnapshot)>(name, (rest, layers),
			(State.CaptureStructure(), ColorLayersSnapshot.Capture(Rig)),
			s =>
			{
				s.Item1.Restore();
				s.Item2.Restore();
			}));
		return true;
	}

	// A new colour layer of this kind on the whole object, last (applied after the others)
	public ColorLayer AddColourLayer(ColorAdjustKind kind)
	{
		var layer = new ColorLayer { Name = ColorLayerOps.UniqueName(Rig, ColorAdjust.KindName(kind)), Adjust = new ColorAdjust(kind) };
		EditColourLayers($"Add colour layer {layer.Name}", () =>
		{
			Rig.ColorLayers.Add(layer);
			return true;
		});
		return layer;
	}

	// Gone, with its keys (and out of any skin)
	public bool DeleteColourLayer(ColorLayer layer) =>
		EditColourAndKeys($"Delete colour layer {layer.Name}", () =>
		{
			if (!Rig.ColorLayers.Remove(layer)) return false;
			SkinOps.Forget(Rig, layer);
			foreach (var animation in State.Animations)
				animation.Timelines.RemoveAll(t => t is ColorLayerTimeline && t.Target == layer.Name);
			return true;
		});

	// Earlier (applied before the others, -1) or later (+1)
	public bool MoveColourLayer(ColorLayer layer, int by)
	{
		var list = Rig.ColorLayers;
		int i = list.IndexOf(layer), j = i + by;
		if (i < 0 || j < 0 || j >= list.Count) return false;
		return EditColourLayers($"Move colour layer {layer.Name}", () =>
		{
			list.RemoveAt(i);
			list.Insert(j, layer);
			return true;
		});
	}

	// False: empty, or another colour layer's name. Its keys follow.
	public bool RenameColourLayer(ColorLayer layer, string name)
	{
		name = name.Trim();
		return name != layer.Name && EditColourAndKeys($"Rename colour layer to {name}", () => ColorLayerOps.Rename(Rig, State.Animations, layer, name));
	}

	public bool ShowColourLayer(ColorLayer layer, bool visible) =>
		layer.Visible != visible && EditColourLayers($"{(visible ? "Show" : "Hide")} colour layer {layer.Name}", () =>
		{
			layer.Visible = visible;
			return true;
		});

	// A part it doesn't change (true), or does again
	public bool LeaveOut(ColorLayer layer, Slot part, bool leaveOut) =>
		EditColourLayers($"{layer.Name}: {(leaveOut ? "leave out" : "take in")} {part.Name}", () =>
			leaveOut ? layer.LeftOut.Add(part) : layer.LeftOut.Remove(part));

	// Its numbers: at rest (Setup), or a key at the playhead (Animate; auto key off, held until
	// keyed). One undo step per setting changed (a slider drag merges into one).
	public void SetColourValues(ColorLayer layer, AdjustValues values, string what)
	{
		var adjust = layer.Adjust;
		if (State.AnimateMode)
		{
			State.Playing = false;
			State.SetFrame(State.Frame);
			if (!State.AutoKey)
			{
				State.Hold.HoldColor(layer, values, Rig, State.Animation, State.Frame);
				return;
			}
			var animation = State.Animation;
			var before = AnimationSnapshot.Capture(animation);
			animation.GetOrCreate(layer.Name, n => new ColorLayerTimeline(n)).Track.Add(State.Time, values);
			adjust.Now = values;
			State.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"Key {layer.Name} {what.ToLowerInvariant()} @ frame {State.Frame}",
				before, AnimationSnapshot.Capture(animation), s => s.Restore(), mergeKey: $"colour key {layer.Name} {what} {State.Frame}"));
			return;
		}
		var rest = ColorLayersSnapshot.Capture(Rig);
		adjust.Setup = values;
		adjust.Now = values;
		State.Undo.Push(new SnapshotCommand<ColorLayersSnapshot>($"{layer.Name}: {what.ToLowerInvariant()}",
			rest, ColorLayersSnapshot.Capture(Rig), s => s.Restore(), mergeKey: $"colour {layer.Name} {what}"));
	}

	// ======================= a drawing's colour layers =======================

	// A drawing's colour layer's numbers: at rest, or keyed (as SetColourValues)
	public void SetDrawingColourValues(Asset asset, AssetLayer layer, AdjustValues values, string what)
	{
		if (layer.Adjust is not { } adjust) return;
		if (State.AnimateMode)
		{
			State.Playing = false;
			State.SetFrame(State.Frame);
			if (!State.AutoKey)
			{
				State.Hold.HoldColor(asset, layer, values, Rig, State.Animation, State.Frame);
				return;
			}
			var animation = State.Animation;
			var before = AnimationSnapshot.Capture(animation);
			animation.GetOrCreate(LayerAdjustTimeline.TargetOf(layer), n => new LayerAdjustTimeline(n) { Caption = $"{asset.Name}: {layer.Name}" })
				.Track.Add(State.Time, values);
			adjust.Now = values;
			asset.MarkEdited();
			State.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"Key {layer.Name} {what.ToLowerInvariant()} @ frame {State.Frame}",
				before, AnimationSnapshot.Capture(animation), s => s.Restore(), mergeKey: $"layer colour key {layer.Id} {what} {State.Frame}"));
			return;
		}
		var rest = LayerAdjustSnapshot.Capture(asset, layer);
		adjust.Setup = values;
		adjust.Now = values;
		asset.MarkEdited();
		State.Undo.Push(new SnapshotCommand<LayerAdjustSnapshot>($"{layer.Name}: {what.ToLowerInvariant()}", rest,
			LayerAdjustSnapshot.Capture(asset, layer), s => s.Restore(), mergeKey: $"layer colour {layer.Id} {what}"));
	}

	// A drawing's colour layer's settings changed (its kind's options), as one undo step
	public void EditDrawingColour(Asset asset, AssetLayer layer, string what, Action<ColorAdjust> change)
	{
		if (layer.Adjust is not { } adjust) return;
		var before = LayerAdjustSnapshot.Capture(asset, layer);
		change(adjust);
		asset.MarkEdited();
		State.Undo.Push(new SnapshotCommand<LayerAdjustSnapshot>($"{layer.Name}: {what}", before,
			LayerAdjustSnapshot.Capture(asset, layer), s => s.Restore()));
	}
}
