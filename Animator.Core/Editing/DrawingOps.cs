namespace Animator.Core.Editing;

// What a hold change did (F5 / Shift+F5)
public enum HoldChange
{
	Done,
	NothingAfter,   // the drawing at the playhead is the part's last one: it already lasts until the end
	NoRoom          // the last drawing would be pushed off the end of the animation
}

// Frame-by-frame parts ("drawing sequences"). A part (slot) can own several drawings:
// attachments with their own pixels, placed on the bone the same way. The part's drawing
// row (its attachment timeline) picks which one shows, from each key until the next key.
// Like RigOps and PartOps, these change the model directly; the editor adds undo.
public static class DrawingOps
{
	// Every drawing the part has, in a natural order (hat, hat_2, hat_3 … hat_10)
	public static List<string> DrawingNames(Skeleton skeleton, Slot slot) =>
		skeleton.AllSkins()
				.SelectMany(s => s.AttachmentsFor(slot.Name))
				.Select(a => a.Name)
				.Distinct()
				.OrderBy(n => n, NaturalOrder.Instance)
				.ToList();

	// The drawing with this name, as the rig would draw it (the active skin wins)
	public static Attachment? Find(Skeleton skeleton, Slot slot, string? name) =>
		name == null ? null
		: skeleton.ActiveSkin?.Get(slot.Name, name) ?? skeleton.DefaultSkin.Get(slot.Name, name);

	// The drawing the animation shows at this time (null = the part is hidden).
	// Before the row's first key, that's the part's rest drawing.
	public static string? DrawingAt(Animation animation, Slot slot, float time)
	{
		var row = animation.Find<AttachmentTimeline>(slot.Name);
		if (row == null || row.Track.Keys.Count == 0 || time < row.Track.Keys[0].Time - KeyHelpers.Epsilon)
			return slot.SetupAttachment;
		return row.Track.TrySample(time, out var name) ? name : slot.SetupAttachment;
	}

	public static string UniqueDrawingName(Skeleton skeleton, Slot slot)
	{
		var used = DrawingNames(skeleton, slot).ToHashSet();
		for (int i = 2; ; i++)
		{
			string candidate = $"{slot.Name}_{i}";
			if (!used.Contains(candidate)) return candidate;
		}
	}

	// Adds an (already filled) asset to the part as a new drawing, placed exactly like
	// `template`, the drawing it's made from. It goes into the same skin as the template.
	public static Attachment AddDrawing(Skeleton skeleton, Slot slot, Attachment? template, Asset asset)
	{
		string name = UniqueDrawingName(skeleton, slot);
		asset.Name = name;
		skeleton.Assets[asset.Id] = asset;

		var drawing = new Attachment { Name = name, AssetId = asset.Id };
		if (template != null)
		{
			drawing.Offset = template.Offset;
			drawing.Rotation = template.Rotation;
			drawing.Scale = template.Scale;
			drawing.Pivot = template.Pivot;
		}

		var skin = template != null && skeleton.ActiveSkin is { } active
				   && active.Get(slot.Name, template.Name) == template
			? active
			: skeleton.DefaultSkin;
		skin.Set(slot.Name, drawing);
		return drawing;
	}

	// key = true (Animate mode): shows the drawing from `frame` on, until the row's next key.
	// key = false (Setup mode): makes it the part's rest drawing.
	// null = hide the part. Returns false if that's already the case.
	public static bool Show(Animation animation, Slot slot, string? name, int frame, int fps, bool key)
	{
		if (!key)
		{
			if (slot.SetupAttachment == name) return false;
			slot.SetupAttachment = name;
			return true;
		}

		float time = frame / (float)fps;
		var row = animation.Find<AttachmentTimeline>(slot.Name);
		int here = row?.Track.Keys.FindIndex(k => MathF.Abs(k.Time - time) < KeyHelpers.Epsilon) ?? -1;
		string? current = here >= 0 ? row!.Track.Keys[here].Value : DrawingAt(animation, slot, time);
		if (current == name) return false;

		animation.GetOrCreate(slot.Name, n => new AttachmentTimeline(n)).Add(time, name);
		return true;
	}

	// Removes a drawing from the part everywhere: its skin entries, and its keys in every
	// animation (the drawing before each one simply lasts longer). Pixels nothing uses any
	// more are dropped from the project. A part keeps at least one drawing.
	public static bool DeleteDrawing(Skeleton skeleton, IEnumerable<Animation> animations, Slot slot, string name)
	{
		var names = DrawingNames(skeleton, slot);
		if (!names.Contains(name) || names.Count <= 1) return false;

		foreach (var skin in skeleton.AllSkins()) skin.Remove(slot.Name, name);
		foreach (var row in Rows(animations, slot))
			row.Track.Keys.RemoveAll(k => k.Value == name);
		if (slot.SetupAttachment == name)
			slot.SetupAttachment = names.First(n => n != name);

		PartOps.RemoveUnusedAssets(skeleton);
		return true;
	}

	public static bool RenameDrawing(Skeleton skeleton, IEnumerable<Animation> animations, Slot slot,
									 string oldName, string newName)
	{
		newName = newName.Trim();
		var names = DrawingNames(skeleton, slot);
		if (newName.Length == 0 || newName == oldName || !names.Contains(oldName) || names.Contains(newName))
			return false;

		foreach (var skin in skeleton.AllSkins()) skin.RenameAttachment(slot.Name, oldName, newName);
		foreach (var row in Rows(animations, slot))
		{
			var keys = row.Track.Keys;
			for (int i = 0; i < keys.Count; i++)
				if (keys[i].Value == oldName) keys[i] = keys[i] with { Value = newName };
		}
		if (slot.SetupAttachment == oldName) slot.SetupAttachment = newName;

		// The pixels take the new name too, unless another drawing shares them
		foreach (var skin in skeleton.AllSkins())
			if (skin.Get(slot.Name, newName) is { } drawing
				&& skeleton.Assets.TryGetValue(drawing.AssetId, out var asset)
				&& skeleton.AllAttachments().Count(a => a.AssetId == asset.Id) == 1)
				asset.Name = newName;
		return true;
	}

	// F5: the drawing at `frame` stays one frame longer, so every later key of the part's
	// drawing row moves one frame later. The animation's length doesn't change, so this
	// is refused if the last drawing would be pushed to the end frame or past it.
	public static HoldChange InsertFrame(Animation animation, Slot slot, int frame, int fps, int endFrame)
	{
		var row = animation.Find<AttachmentTimeline>(slot.Name);
		float time = frame / (float)fps;
		if (row == null || !row.Track.Keys.Any(k => k.Time > time + KeyHelpers.Epsilon))
			return HoldChange.NothingAfter;
		if (FrameOf(row.Track.Keys[^1].Time, fps) + 1 >= endFrame) return HoldChange.NoRoom;

		Shift(row, time, +1, fps);
		return HoldChange.Done;
	}

	// Shift+F5: the drawing at `frame` lasts one frame less, so every later key moves one
	// frame earlier. A drawing that was only on screen for this one frame disappears.
	public static HoldChange RemoveFrame(Animation animation, Slot slot, int frame, int fps)
	{
		var row = animation.Find<AttachmentTimeline>(slot.Name);
		float time = frame / (float)fps;
		if (row == null || !row.Track.Keys.Any(k => k.Time > time + KeyHelpers.Epsilon))
			return HoldChange.NothingAfter;

		var keys = row.Track.Keys;
		int here = keys.FindIndex(k => MathF.Abs(k.Time - time) < KeyHelpers.Epsilon);
		if (here >= 0 && here + 1 < keys.Count && FrameOf(keys[here + 1].Time, fps) == frame + 1)
			keys.RemoveAt(here);   // the next drawing takes over this frame

		Shift(row, time, -1, fps);
		return HoldChange.Done;
	}

	// ---------- helpers ----------

	private static IEnumerable<AttachmentTimeline> Rows(IEnumerable<Animation> animations, Slot slot) =>
		animations.SelectMany(a => a.Timelines).OfType<AttachmentTimeline>().Where(t => t.Target == slot.Name);

	private static int FrameOf(float time, int fps) => (int)MathF.Round(time * fps);

	// Moves every key after `after` by whole frames (keys stay sorted: they all move together)
	private static void Shift(AttachmentTimeline row, float after, int frames, int fps)
	{
		var keys = row.Track.Keys;
		for (int i = 0; i < keys.Count; i++)
			if (keys[i].Time > after + KeyHelpers.Epsilon)
				keys[i] = keys[i] with { Time = (FrameOf(keys[i].Time, fps) + frames) / (float)fps };
	}

	// Sorts "hat_2" before "hat_10": runs of digits compare as numbers, letters ignore case
	private sealed class NaturalOrder : IComparer<string>
	{
		public static readonly NaturalOrder Instance = new();

		public int Compare(string? a, string? b)
		{
			a ??= "";
			b ??= "";
			int i = 0, j = 0;
			while (i < a.Length && j < b.Length)
			{
				if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
				{
					int startA = i, startB = j;
					while (i < a.Length && char.IsDigit(a[i])) i++;
					while (j < b.Length && char.IsDigit(b[j])) j++;
					string numberA = a[startA..i].TrimStart('0'), numberB = b[startB..j].TrimStart('0');
					int byNumber = numberA.Length != numberB.Length
						? numberA.Length.CompareTo(numberB.Length)
						: string.CompareOrdinal(numberA, numberB);
					if (byNumber != 0) return byNumber;
				}
				else
				{
					int byLetter = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
					if (byLetter != 0) return byLetter;
					i++;
					j++;
				}
			}
			int byLength = (a.Length - i).CompareTo(b.Length - j);
			return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
		}
	}
}
