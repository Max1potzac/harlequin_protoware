using System.IO;
using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Import;

// One part of an Animate import: a track of the played timeline (a symbol, picture or run of
// shapes on a layer, at some depth inside other symbols), which may change drawings over time
public sealed class XflPart
{
	public int Track { get; init; }                    // index in the plan's data
	public string Name { get; set; } = "";
	public string Where { get; init; } = "";           // its layers, for showing ("legs / thigh")
	public bool Include { get; set; } = true;          // brought in at all
	public bool InRig { get; set; } = true;            // used to work out the rig (skins, extras: leave out)
	public RigRole Role { get; set; } = RigRole.Bone;  // in a suggested rig
	public int Target { get; set; } = -1;              // the part it hangs from / rides on (-1 = worked out)
}

public sealed record XflTimelineChoice(string Key, string Label, bool Scene, int Frames)
{
	public override string ToString() => Label;
}

// What the Animate import dialog decided: which timeline, which parts come in, and how (a drawn
// object with its parts moving, or a rigged one with bones worked out from the animation)
public sealed class XflImportPlan : IDisposable
{
	public XflDocument Document { get; }
	public string Name { get; set; }
	public List<XflTimelineChoice> Timelines { get; } = new();
	public XflTimelineChoice Timeline { get; private set; } = null!;
	public XflAnimationData Data { get; private set; } = null!;
	public XflPainter Painter { get; private set; } = null!;
	public List<XflPart> Parts { get; } = new();
	public bool SuggestRig { get; set; }
	public bool SplitAtLabels { get; set; } = true;
	public float Scale { get; set; } = 1f;
	public int SetupFrame { get; set; }                // the rest pose: this frame's placement
	public int Root { get; set; } = -1;                // the part whose bone everything hangs from (-1 = worked out)
	public Vector2 Origin { get; private set; }        // in the timeline's space: where the object's origin goes

	// Animations (by name, see Animations) the rig isn't worked out from: ones where parts come
	// apart (a head falling off) would hide the joints the others show
	public HashSet<string> NotForRig { get; } = new();

	private RigMasks? _masks;
	private Matrix3x2 _objectToCanvas;
	private MotionHints? _motion;

	private XflImportPlan(XflDocument document, string name)
	{
		Document = document;
		Name = name;
	}

	public void Dispose() => Painter?.Dispose();

	public static XflImportPlan Load(string path, string name)
	{
		var plan = new XflImportPlan(XflReader.Read(path), name);
		var doc = plan.Document;
		foreach (var scene in doc.Scenes)
			plan.Timelines.Add(new XflTimelineChoice(XflEvaluator.SceneKey(scene.Name), $"Scene: {scene.Name}", true, scene.Length));
		var probe = new XflEvaluator(doc);
		foreach (var symbol in doc.Symbols.Values.Where(s => !probe.IsStatic(s.Name)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
			plan.Timelines.Add(new XflTimelineChoice(symbol.Name, $"Symbol: {symbol.Name}", false, symbol.Timeline.Length));
		if (plan.Timelines.Count == 0)
			throw new InvalidDataException("There's no timeline in this project to bring in.");
		plan.Choose(plan.Timelines[0]);
		return plan;
	}

	public IReadOnlyList<string> Notes
	{
		get
		{
			var notes = Document.Notes.Concat(Data.Notes).Concat(Painter.Notes).ToList();
			if (WorstSkew > 1f)
				notes.Add($"Some parts are skewed (up to {WorstSkew:0}°): skew isn't supported yet, so they'll be turned and scaled the nearest way instead.");
			return notes.Distinct().ToList();
		}
	}

	// The most any part is skewed on stage (degrees): skew can't be brought in as it is
	public float WorstSkew
	{
		get
		{
			float worst = 0;
			foreach (var part in Parts.Where(p => p.Include))
				foreach (var p in Data.Tracks[part.Track].Frames)
					if (p is { } placed)
					{
						var (_, _, ax, ay) = XflMath.Decompose(placed.Matrix);
						worst = MathF.Max(worst, MathF.Abs(XflMath.Mod(ay - ax + MathF.PI, 2 * MathF.PI) - MathF.PI));
					}
			return worst * 180 / MathF.PI;
		}
	}

	// Plays the timeline and makes its parts
	public void Choose(XflTimelineChoice choice)
	{
		Timeline = choice;
		var timeline = choice.Scene ? Document.Scenes.First(s => XflEvaluator.SceneKey(s.Name) == choice.Key) : Document.Symbol(choice.Key)!.Timeline;
		Data = XflAnimationData.Evaluate(Document, timeline, choice.Key);
		Painter?.Dispose();
		Painter = new XflPainter(Document, Data.Evaluator);
		Parts.Clear();
		var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int t = 0; t < Data.Tracks.Count; t++)
		{
			var track = Data.Tracks[t];
			string name = track.AutoNamed || track.LayerName.Trim().Length == 0
				? Data.Evaluator.DrawingName(track.MostShown())
				: track.LayerName.Trim();
			string unique = name;
			for (int i = 2; !used.Add(unique); i++) unique = $"{name} {i}";
			track.Name = unique;
			Parts.Add(new XflPart { Track = t, Name = unique, Where = track.Id, Include = !track.Hidden });
		}
		SetupFrame = BusiestFrame();
		Root = -1;
		Origin = FindOrigin();
		NotForRig.Clear();
		Forget();
	}

	// A scene's stage centre, or its corner (0, 0) when the drawing sits mostly off the stage
	// around that corner, the way PvZ2 animations stand their characters on it; a symbol's own origin
	private Vector2 FindOrigin()
	{
		var middle = new Vector2(Document.Width / 2, Document.Height / 2);
		if (!Timeline.Scene) return Vector2.Zero;
		var content = SKRect.Empty;
		var bounds = new Dictionary<XflDrawingKey, SKRect>();
		foreach (var track in Data.Tracks)
			foreach (var p in track.Frames)
			{
				if (p is not { } placed) continue;
				if (!bounds.TryGetValue(placed.Drawing, out var box)) bounds[placed.Drawing] = box = Painter.Bounds(placed.Drawing);
				if (box.IsEmpty) continue;
				var onStage = SkeletonRenderer.ToSk(placed.Matrix).MapRect(box);
				content = content.IsEmpty ? onStage : SKRect.Union(content, onStage);
			}
		if (content.IsEmpty) return middle;
		float w = MathF.Max(0, MathF.Min(content.Right, Document.Width) - MathF.Max(content.Left, 0));
		float h = MathF.Max(0, MathF.Min(content.Bottom, Document.Height) - MathF.Max(content.Top, 0));
		return w * h >= 0.5f * content.Width * content.Height ? middle : Vector2.Zero;
	}

	// The frame showing the most parts (the earliest of those): a good rest pose
	private int BusiestFrame()
	{
		int best = 0, most = -1;
		for (int f = 0; f < Data.FrameCount; f++)
		{
			int shown = Parts.Count(p => p.Include && Data.Tracks[p.Track].Frames[f] != null);
			if (shown > most)
			{
				most = shown;
				best = f;
			}
		}
		return best;
	}

	// The rig's inputs change: work them out again when next asked
	public void Forget()
	{
		_masks = null;
		_motion = null;
	}

	// ======================= spaces =======================

	// The played timeline's space (pixels, y down) → the object's (y up), Origin at its origin
	public Matrix3x2 TimelineToObject => Matrix3x2.CreateTranslation(-Origin) * Matrix3x2.CreateScale(Scale, -Scale);

	// A part's own space → the object, at a frame (null = not shown then)
	public Matrix3x2? PartToObject(int part, int frame) =>
		Data.Tracks[Parts[part].Track].Frames[frame] is { } p ? p.Matrix * TimelineToObject : null;

	public XflPlacement? Placement(int part, int frame) => Data.Tracks[Parts[part].Track].Frames[frame];

	// ======================= animations =======================

	// The animations it becomes: one per frame label (to the next label), or the whole timeline
	public List<(string Name, int Start, int End)> Animations()
	{
		var list = new List<(string, int, int)>();
		var labels = Data.Labels.Where(l => l.Frame < Data.FrameCount).ToList();
		if (SplitAtLabels && labels.Count > 0)
		{
			if (labels[0].Frame > 0) list.Add(("start", 0, labels[0].Frame));
			for (int i = 0; i < labels.Count; i++)
			{
				int end = i + 1 < labels.Count ? labels[i + 1].Frame : Data.FrameCount;
				if (end > labels[i].Frame) list.Add((labels[i].Name, labels[i].Frame, end));
			}
		}
		else list.Add((Timeline.Scene ? "animation" : Plain(Timeline.Key), 0, Data.FrameCount));
		return list;
	}

	private static string Plain(string name) => name[(name.Replace('\\', '/').LastIndexOf('/') + 1)..];

	// ======================= the rig =======================

	private bool UsedForRig(int part) => Parts[part].Include && Parts[part].InRig && Placement(part, SetupFrame) != null;

	// The parts' shapes at the rest pose, on a canvas (y down, at most 2048 pixels across)
	public RigMasks Masks
	{
		get
		{
			if (_masks != null) return _masks;
			var boxes = new List<SKRect>();
			for (int i = 0; i < Parts.Count; i++)
			{
				var box = SKRect.Empty;
				if (PartToObject(i, SetupFrame) is { } m && Placement(i, SetupFrame) is { } p)
					box = SkeletonRenderer.ToSk(m).MapRect(Painter.Bounds(p.Drawing));
				boxes.Add(box);
			}
			var all = boxes.Where(b => !b.IsEmpty).ToList();
			float minX = all.Count > 0 ? all.Min(b => b.Left) : 0, maxX = all.Count > 0 ? all.Max(b => b.Right) : 1;
			float minY = all.Count > 0 ? all.Min(b => b.Top) : 0, maxY = all.Count > 0 ? all.Max(b => b.Bottom) : 1;
			float span = MathF.Max(1, MathF.Max(maxX - minX, maxY - minY));
			float s = span > 2048 ? 2048 / span : 1f;
			_objectToCanvas = Matrix3x2.CreateTranslation(-minX, -maxY) * Matrix3x2.CreateScale(s, -s);
			int width = (int)MathF.Ceiling((maxX - minX) * s) + 1, height = (int)MathF.Ceiling((maxY - minY) * s) + 1;

			var rects = new List<PixelRect>();
			var alphas = new List<byte[]>();
			for (int i = 0; i < Parts.Count; i++)
			{
				if (boxes[i].IsEmpty || PartToObject(i, SetupFrame) is not { } m || Placement(i, SetupFrame) is not { } p)
				{
					rects.Add(new PixelRect(0, 0, 0, 0));
					alphas.Add(Array.Empty<byte>());
					continue;
				}
				var onCanvas = SkeletonRenderer.ToSk(_objectToCanvas).MapRect(boxes[i]);
				int x0 = (int)MathF.Floor(onCanvas.Left), y0 = (int)MathF.Floor(onCanvas.Top);
				var rect = new PixelRect(x0, y0, Math.Max(1, (int)MathF.Ceiling(onCanvas.Right) - x0), Math.Max(1, (int)MathF.Ceiling(onCanvas.Bottom) - y0));
				using var drawn = new SKBitmap(AssetStore.InfoFor(rect.W, rect.H));
				drawn.Erase(SKColors.Transparent);
				using (var canvas = new SKCanvas(drawn))
				{
					var place = SkeletonRenderer.ToSk(m * _objectToCanvas * Matrix3x2.CreateTranslation(-x0, -y0));
					canvas.Concat(in place);
					Painter.Draw(canvas, p.Drawing);
				}
				rects.Add(rect);
				alphas.Add(SheetImporter.Alpha(drawn));
			}
			_masks = RigMasks.Build(width, height, rects, alphas);
			return _masks;
		}
	}

	public Vector2 CanvasToObject(Vector2 canvas)
	{
		_ = Masks;
		return Matrix3x2.Invert(_objectToCanvas, out var back) ? Vector2.Transform(canvas, back) : canvas;
	}

	public Vector2 ObjectToCanvas(Vector2 point)
	{
		_ = Masks;
		return Vector2.Transform(point, _objectToCanvas);
	}

	// How the parts move against each other over the timeline (but the animations left out of
	// the rig, apart from the rest pose)
	public MotionHints Motion
	{
		get
		{
			if (_motion != null) return _motion;
			var used = new bool[Data.FrameCount];
			foreach (var (name, start, end) in Animations())
				if (!NotForRig.Contains(name))
					for (int f = start; f < end; f++) used[f] = true;
			used[SetupFrame] = true;
			var placements = new List<Matrix3x2?[]>();
			var bounds = new List<(Vector2, Vector2)>();
			var toObject = TimelineToObject;
			for (int i = 0; i < Parts.Count; i++)
			{
				var track = Data.Tracks[Parts[i].Track];
				placements.Add(track.Frames.Select((f, frame) => used[frame] && f is { } p ? p.Matrix * toObject : (Matrix3x2?)null).ToArray());
				var box = Painter.Bounds(track.MostShown());
				bounds.Add(box.IsEmpty ? (Vector2.Zero, Vector2.One) : (new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Bottom)));
			}
			_motion = MotionHints.Fit(placements, bounds, SetupFrame);
			return _motion;
		}
	}

	private sealed class MotionAdapter : IRigMotion
	{
		private readonly XflImportPlan _plan;
		public MotionAdapter(XflImportPlan plan) => _plan = plan;
		public double? Strength(int a, int b) => _plan.Motion.Quality(a, b);
		public Vector2? Joint(int a, int b) =>
			_plan.Motion.Quality(a, b) >= RigSuggester.ClearJoint && _plan.Motion.Joint(a, b) is { } j ? _plan.ObjectToCanvas(j) : null;
		public bool MovesAgainst(int a, int b) => _plan.Motion.MovesAgainst(a, b);
		public bool MovesAsOne(int a, int b) => _plan.Motion.AnythingMoves && _plan.Motion.Rigid(a, b);
	}

	// Roles as the shapes and the motion suggest, choices cleared
	public void SuggestRoles()
	{
		var roles = RigSuggester.SuggestRoles(Masks, Enumerable.Range(0, Parts.Count).Select(UsedForRig).ToList(), new MotionAdapter(this));
		for (int i = 0; i < Parts.Count; i++)
		{
			if (UsedForRig(i)) Parts[i].Role = roles[i];
			Parts[i].Target = -1;
		}
		Root = -1;
	}

	// The rig as the parts' roles say now (parts left out of it, or not shown at the rest pose,
	// are put on the bone they move with when it's made)
	public RigSuggestion Suggest() => RigSuggester.Suggest(Masks,
		Enumerable.Range(0, Parts.Count).Select(i => UsedForRig(i) ? Parts[i].Role : RigRole.LeaveOut).ToList(),
		Parts.Select(p => p.Target).ToList(), Root, new MotionAdapter(this));
}

// Makes the object a plan describes, with its animations
public static class XflImporter
{
	private const float Turn = 2 * MathF.PI;

	// pictures: if given, each drawing made, with what it shows and its pixels → that drawing's
	// own space (for checking the result)
	public static SceneObject IntoScene(Scene scene, AssetStore store, XflImportPlan plan, Vector2 position,
		IDictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>? pictures = null)
	{
		var data = plan.Data;
		int frames = data.FrameCount;
		var parts = Enumerable.Range(0, plan.Parts.Count).Where(i => plan.Parts[i].Include && data.Tracks[plan.Parts[i].Track].Shown.Any()).ToList();
		var obj = plan.SuggestRig ? SceneOps.AddRigged(scene, plan.Name, position) : SceneOps.AddDrawn(scene, plan.Name, position);
		var skeleton = obj.Skeleton;
		var handle = skeleton.Roots[0];
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();

		// ----- bones: the suggested ones, each moving with its part -----
		var bones = new List<(Bone Bone, int Part)>();
		var rig = plan.SuggestRig ? plan.Suggest() : null;
		if (rig != null)
		{
			var made = RigOps.AddSuggestedBones(skeleton, handle, rig, plan.CanvasToObject, plan.Parts.Select(p => p.Name).ToList());
			for (int b = 0; b < made.Length; b++) bones.Add((made[b], rig.Bones[b].Layer));
		}
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();

		// Each bone's world over time, held while its part is hidden: it turns as its part turns,
		// and its joint goes where the part takes that point. Bones only move and turn: a part's
		// scale and skew stay on the part (on a bone that scaled unevenly, a part turning on it
		// would be skewed by it), so they're rebuilt the way the editor will from the keys made.
		var target = new Dictionary<Bone, Matrix3x2[]>();
		foreach (var (bone, part) in bones)
		{
			var atSetup = plan.PartToObject(part, plan.SetupFrame)!.Value;
			Matrix3x2.Invert(atSetup, out var fromPart);
			var joint = Vector2.Transform(bone.World.Translation, fromPart);   // in the part's own space
			float turned = bone.WorldRotation - AngleOf(atSetup);
			target[bone] = Held(Enumerable.Range(0, frames).Select(f => plan.PartToObject(part, f) is { } m
				? Matrix3x2.CreateRotation(AngleOf(m) + turned) * Matrix3x2.CreateTranslation(Vector2.Transform(joint, m))
				: (Matrix3x2?)null).ToArray(), bone.World);
		}
		var world = new Dictionary<Bone, Matrix3x2[]> { [handle] = Enumerable.Repeat(handle.World, frames).ToArray() };
		var locals = new Dictionary<Bone, (Vector2 Position, float Rotation, Vector2 Scale)[]>();
		foreach (var bone in skeleton.AllBones().Where(b => target.ContainsKey(b)))   // parents first
		{
			var parentWorld = world[bone.Parent!];
			var rebuilt = new Matrix3x2[frames];
			var local = new (Vector2, float, Vector2)[frames];
			float previous = bone.SetupRotation;
			for (int f = 0; f < frames; f++)
			{
				Matrix3x2.Invert(parentWorld[f], out var toParent);
				var (at, turn, size) = Local(target[bone][f] * toParent);
				turn = previous + XflMath.Mod(turn - previous + MathF.PI, Turn) - MathF.PI;   // continuous
				previous = turn;
				local[f] = (at, turn, size);
				rebuilt[f] = Matrix3x2.CreateScale(size) * Matrix3x2.CreateRotation(turn) * Matrix3x2.CreateTranslation(at) * parentWorld[f];
			}
			world[bone] = rebuilt;
			locals[bone] = local;
		}

		// ----- parts: each on its bone (one left out of the rig on the bone it moves with) -----
		var bonesOf = new Dictionary<int, Bone>();
		foreach (int part in parts)
		{
			Bone? bone = null;
			if (rig != null && rig.BoneOf[part] >= 0) bone = bones[rig.BoneOf[part]].Bone;
			bone ??= MovesWith(plan, part, bones.Select(b => b.Bone).ToList(), world, frames) ?? handle;
			bonesOf[part] = bone;
		}

		var slots = new Dictionary<int, (Slot Slot, Dictionary<XflDrawingKey, (Attachment Drawing, XflPicture Picture)> Drawings, int RestFrame)>();
		var rendered = new Dictionary<XflDrawingKey, XflPicture?>();
		var shownAt = ShownScales(plan, parts);
		try
		{
			foreach (int part in parts)
			{
				var track = data.Tracks[plan.Parts[part].Track];
				var bone = bonesOf[part];
				int rest = track.Frames[plan.SetupFrame] != null ? plan.SetupFrame : track.Shown.First();
				Slot? slot = null;
				var drawings = new Dictionary<XflDrawingKey, (Attachment, XflPicture)>();
				foreach (var key in track.Frames.Where(f => f != null).Select(f => f!.Value.Drawing).Distinct())
				{
					if (!rendered.TryGetValue(key, out var picture))
						rendered[key] = picture = MakePicture(plan, key, shownAt.GetValueOrDefault(key, 1f));
					if (picture == null) continue;
					var pivot = track.Frames[rest]?.Pivot ?? Vector2.Zero;
					var drawing = NewDrawing(skeleton, store, ref slot, bone, plan.Parts[part].Name, plan.Painter.Notes, key, data.Evaluator.DrawingName(key), picture, pivot);
					var (offset, rotation, scale) = Placement(PictureToBone(plan, part, rest, picture, drawing,
						world.TryGetValue(bone, out var exact) ? exact[rest] : bone.World));
					drawing.Offset = offset;
					drawing.Rotation = rotation;
					drawing.Scale = scale;
					drawings[key] = (drawing, picture);
					if (pictures != null) pictures[drawing] = (key, picture.PixelToLocal);
				}
				if (slot == null) continue;
				var setup = track.Frames[plan.SetupFrame];
				slot.SetupAttachment = setup is { } s && drawings.TryGetValue(s.Drawing, out var shown) ? shown.Item1.Name : null;
				var atRest = (track.Frames[plan.SetupFrame] ?? track.Frames[rest])!.Value;
				var color = atRest.Color;
				slot.Blend = atRest.Blend;
				if (track.Frames.Any(f => f is { } shownThen && shownThen.Blend != atRest.Blend))
					plan.Painter.Notes.Add("Some parts change their blend mode during the animation: they keep the one they have at the rest pose.");
				slot.SetupColor = new Vector3(color.Multiply.X, color.Multiply.Y, color.Multiply.Z);
				slot.SetupOpacity = Math.Clamp(color.Multiply.W, 0, 1);
				slot.ResetToSetup();
				slots[part] = (slot, drawings, rest);
			}
		}
		finally
		{
			foreach (var p in rendered.Values) p?.Dispose();
		}

		// ----- animations -----
		obj.Animations.Clear();
		float fps = data.FrameRate;

		// How far a part reaches from its pivot (object units), so a small turn or scale of it can
		// be told in pixels
		float Reach(int part) => slots[part].Drawings
			.Select(d => new Vector2(d.Value.Picture.Width, d.Value.Picture.Height).Length() / d.Value.Picture.Scale * shownAt.GetValueOrDefault(d.Key, 1f) * plan.Scale)
			.DefaultIfEmpty(1f).Max();

		foreach (var (name, start, end) in plan.Animations())
		{
			var animation = new Animation { Name = UniqueName(obj.Animations, name), Duration = Math.Max(1, end - start) / fps, Loop = true };
			obj.Animations.Add(animation);

			// The bones' motion: keys wherever it isn't a straight line between them (finely enough
			// that a part fixed on its bone needs no keys of its own)
			foreach (var (bone, _) in bones)
			{
				var local = locals[bone];
				AddKeys(animation, start, end, fps, i => local[i].Rotation - bone.SetupRotation, 0.0001f,
					() => animation.GetOrCreate(bone.Name, n => new RotateTimeline(n)).Track);
				AddKeys(animation, start, end, fps, i => local[i].Position - bone.SetupPosition, 0.01f,
					() => animation.GetOrCreate(bone.Name, n => new TranslateTimeline(n)).Track);
				AddKeys(animation, start, end, fps, i => new Vector2(local[i].Scale.X / NonZero(bone.SetupScale.X), local[i].Scale.Y / NonZero(bone.SetupScale.Y)), 0.0001f,
					() => animation.GetOrCreate(bone.Name, n => new ScaleTimeline(n)).Track, Vector2.One);
			}

			// The bones as those keys pose them (a hair off the motion where keys were left out):
			// the parts are placed against that, so they land exactly where Animate draws them
			var posed = new Dictionary<Bone, Matrix3x2[]>();
			if (bones.Count > 0)
			{
				var all = skeleton.AllBones().ToList();
				foreach (var bone in all) posed[bone] = new Matrix3x2[end - start];
				for (int f = start; f < end; f++)
				{
					skeleton.SetToSetupPose();
					animation.Apply(skeleton, (f - start) / fps);
					skeleton.UpdateWorld();
					foreach (var bone in all) posed[bone][f - start] = bone.World;
				}
				skeleton.SetToSetupPose();
				skeleton.UpdateWorld();
			}
			Matrix3x2 BoneAt(Bone bone, int f) =>
				posed.TryGetValue(bone, out var keyed) ? keyed[f - start] : world.TryGetValue(bone, out var exact) ? exact[f] : bone.World;

			foreach (var (part, (slot, drawings, rest)) in slots)
			{
				var track = data.Tracks[plan.Parts[part].Track];
				var bone = bonesOf[part];

				// Which drawing shows: a key wherever that changes (and at the start, if it isn't the rest one)
				string? showing = slot.SetupAttachment;
				for (int f = start; f < end; f++)
				{
					string? now = track.Frames[f] is { } p && drawings.TryGetValue(p.Drawing, out var d) ? d.Drawing.Name : null;
					if (now == showing && f > start) continue;
					if (f == start && now == slot.SetupAttachment) continue;
					animation.GetOrCreate(slot.Name, n => new AttachmentTimeline(n)).Add((f - start) / fps, now);
					showing = now;
				}

				// Its placement on its bone, where it isn't fixed to it; held while it's hidden
				var placements = new (Vector2 Offset, float Rotation, Vector2 Scale)?[end - start];
				float previous = 0;
				for (int f = start; f < end; f++)
				{
					if (track.Frames[f] is not { } p || !drawings.TryGetValue(p.Drawing, out var d)) continue;
					var (o, r, s) = Placement(PictureToBone(plan, part, f, d.Picture, d.Drawing, BoneAt(bone, f)));
					float turn = r - d.Drawing.Rotation;
					turn = previous + XflMath.Mod(turn - previous + MathF.PI, Turn) - MathF.PI;
					previous = turn;
					placements[f - start] = (o - d.Drawing.Offset, turn,
						new Vector2(s.X / NonZero(d.Drawing.Scale.X), s.Y / NonZero(d.Drawing.Scale.Y)));
				}
				var held = Held(placements);
				if (held != null)
				{
					float fine = MathF.Max(1e-5f, 0.02f / Reach(part));   // a turn or scale moving its farthest pixel 1/50 pixel
					AddKeys(animation, start, end, fps, i => held[i - start].Offset, 0.02f,
						() => animation.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track);
					AddKeys(animation, start, end, fps, i => held[i - start].Rotation, fine,
						() => animation.GetOrCreate(slot.Name, n => new PartRotateTimeline(n)).Track);
					AddKeys(animation, start, end, fps, i => held[i - start].Scale, fine,
						() => animation.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track, Vector2.One);
				}

				// Its colour and opacity, where they change
				var colors = Held(Enumerable.Range(start, end - start).Select(f => track.Frames[f]?.Color).ToArray());
				if (colors != null)
				{
					AddKeys(animation, start, end, fps, i => new Vector3(colors[i - start].Multiply.X, colors[i - start].Multiply.Y, colors[i - start].Multiply.Z),
						0.004f, () => animation.GetOrCreate(slot.Name, n => new PartColorTimeline(n)).Track, slot.SetupColor);
					AddKeys(animation, start, end, fps, i => Math.Clamp(colors[i - start].Multiply.W, 0, 1), 0.004f,
						() => animation.GetOrCreate(slot.Name, n => new PartOpacityTimeline(n)).Track, slot.SetupOpacity);
					if (colors.Any(c => c.Offset.X != 0 || c.Offset.Y != 0 || c.Offset.Z != 0 || c.Offset.W != 0))
						plan.Painter.Notes.Add("Colour effects that brighten or tint (offsets) aren't brought in: only multiplied colours and alpha.");
				}
			}
		}
		obj.PreviewAnimation = obj.Animations.FirstOrDefault()?.Name;   // (the skew dropped is the parts' own: the plan's notes say how much)

		skeleton.SetToSetupPose();
		skeleton.UpdatePose();
		return obj;
	}

	// ----- placing drawings -----

	// Which way a transform's x axis points
	private static float AngleOf(Matrix3x2 m) => MathF.Atan2(m.M12, m.M11);

	// A transform as a bone's local values (no skew): position, rotation, scale
	private static (Vector2 Position, float Rotation, Vector2 Scale) Local(Matrix3x2 m)
	{
		var (sx, sy, ax, _) = XflMath.Decompose(m);
		return (m.Translation, ax, new Vector2(sx, sy));
	}

	// A drawing's picture (y down) → its bone, at a frame, as the scaled / turned / moved part of
	// Attachment.LocalMatrix (its pivot and the y flip taken off first)
	private static Matrix3x2 PictureToBone(XflImportPlan plan, int part, int frame, XflPicture picture, Attachment drawing, Matrix3x2 boneWorld)
	{
		var toObject = plan.PartToObject(part, frame)!.Value;
		Matrix3x2.Invert(boneWorld, out var toBone);
		var pivot = new Vector2(drawing.Pivot.X * picture.Width, drawing.Pivot.Y * picture.Height);
		return Matrix3x2.CreateScale(1, -1) * Matrix3x2.CreateTranslation(pivot) * picture.PixelToLocal * toObject * toBone;
	}

	private static (Vector2 Offset, float Rotation, Vector2 Scale) Placement(Matrix3x2 m) => Local(m);

	// How big each drawing is ever shown (the larger of its axes' scales)
	private static Dictionary<XflDrawingKey, float> ShownScales(XflImportPlan plan, List<int> parts)
	{
		var shown = new Dictionary<XflDrawingKey, float>();
		foreach (int part in parts)
			foreach (var p in plan.Data.Tracks[plan.Parts[part].Track].Frames)
				if (p is { } placed)
				{
					float scale = MathF.Max(new Vector2(placed.Matrix.M11, placed.Matrix.M12).Length(), new Vector2(placed.Matrix.M21, placed.Matrix.M22).Length());
					shown[placed.Drawing] = MathF.Max(shown.GetValueOrDefault(placed.Drawing), scale);
				}
		return shown;
	}

	private static XflPicture? MakePicture(XflImportPlan plan, XflDrawingKey key, float shown)
	{
		// As sharp as it's ever shown (and its pictures' own detail), within reason
		float scale = Math.Clamp(MathF.Max(shown * plan.Scale, plan.Painter.PictureDensity(key) * plan.Scale), 0.05f, 4f);
		var bounds = plan.Painter.Bounds(key);
		if (bounds.IsEmpty) return null;
		float largest = MathF.Max(bounds.Width, bounds.Height) * scale;
		if (largest > 4096) scale *= 4096 / largest;
		return plan.Painter.Render(key, scale) is { } made ? new XflPicture(made.Image, made.Origin, scale) : null;
	}

	private static Attachment NewDrawing(Skeleton skeleton, AssetStore store, ref Slot? slot, Bone bone, string partName,
		HashSet<string> notes, XflDrawingKey key, string drawingName, XflPicture picture, Vector2 pivot)
	{
		var asset = new Asset { Name = drawingName, Width = picture.Width, Height = picture.Height };
		store.AddLayer(asset, "Base", picture.Image);
		Attachment drawing;
		if (slot == null)
		{
			slot = PartOps.CreatePart(skeleton, bone, asset, partName, Vector2.Zero, 0f);
			drawing = skeleton.DefaultSkin.Get(slot.Name, slot.Name)!;
			if (drawingName != slot.Name && skeleton.DefaultSkin.Get(slot.Name, drawingName) == null)
			{
				skeleton.DefaultSkin.RenameAttachment(slot.Name, slot.Name, drawingName);
				slot.SetupAttachment = slot.ActiveAttachment = drawingName;
				asset.Name = drawingName;
			}
		}
		else
		{
			string name = drawingName;
			for (int i = 2; skeleton.DefaultSkin.Get(slot.Name, name) != null; i++) name = $"{drawingName} {i}";
			asset.Name = name;
			skeleton.Assets[asset.Id] = asset;
			drawing = new Attachment { Name = name, AssetId = asset.Id };
			skeleton.DefaultSkin.Set(slot.Name, drawing);
		}
		// It turns around the animator's transformation point
		var local = (pivot - picture.Origin) * picture.Scale;
		drawing.Pivot = new Vector2(local.X / picture.Width, local.Y / picture.Height);
		return drawing;
	}

	// The bone a part moves with best: the one it stays most still against (null: nothing to go by)
	private static Bone? MovesWith(XflImportPlan plan, int part, List<Bone> bones, Dictionary<Bone, Matrix3x2[]> world, int frames)
	{
		Bone? best = null;
		float bestDrift = float.MaxValue;
		var box = plan.Painter.Bounds(plan.Data.Tracks[plan.Parts[part].Track].MostShown());
		var middle = box.IsEmpty ? Vector2.Zero : new Vector2(box.MidX, box.MidY);
		foreach (var bone in bones)
		{
			var points = new List<Vector2>();
			float minTurn = float.MaxValue, maxTurn = float.MinValue;
			for (int f = 0; f < frames; f++)
			{
				if (plan.PartToObject(part, f) is not { } m || !Matrix3x2.Invert(world[bone][f], out var toBone)) continue;
				var onBone = m * toBone;
				points.Add(Vector2.Transform(middle, onBone));
				float turn = MathF.Atan2(onBone.M12, onBone.M11);
				minTurn = MathF.Min(minTurn, turn);
				maxTurn = MathF.Max(maxTurn, turn);
			}
			if (points.Count == 0) continue;
			var mean = points.Aggregate(Vector2.Zero, (sum, point) => sum + point) / points.Count;
			float drift = points.Max(point => Vector2.Distance(point, mean)) + (maxTurn - minTurn) * 20;
			if (drift >= bestDrift) continue;
			bestDrift = drift;
			best = bone;
		}
		return best;
	}

	// ----- keys -----

	// Values over a stretch (nulls = hidden then): the gaps held from the value before (or after)
	private static T[] Held<T>(T?[] values, T fallback) where T : struct
	{
		var held = new T[values.Length];
		T? last = values.FirstOrDefault(v => v != null);
		for (int i = 0; i < values.Length; i++)
		{
			if (values[i] is { } v) last = v;
			held[i] = last ?? fallback;
		}
		return held;
	}

	private static T[]? Held<T>(T?[] values) where T : struct =>
		values.Any(v => v != null) ? Held(values, default) : null;

	// Keys at the frames needed to follow `value` within `tolerance`, straight lines between
	// (Douglas-Peucker): none at all if it never leaves `rest`
	private static void AddKeys<T>(Animation animation, int start, int end, float fps, Func<int, T> value, float tolerance,
		Func<Track<T>> track, T rest = default) where T : struct
	{
		int n = end - start;
		var values = new float[n][];
		for (int i = 0; i < n; i++) values[i] = Components(value(start + i));
		var restValues = Components(rest);
		if (values.All(v => Distance(v, restValues) <= tolerance)) return;
		var keep = new bool[n];
		keep[0] = keep[n - 1] = true;
		Simplify(values, 0, n - 1, tolerance, keep);
		var keys = track();
		for (int i = 0; i < n; i++)
			if (keep[i])
				keys.Add(i / fps, value(start + i), CurveType.Linear);
	}

	private static void Simplify(float[][] values, int a, int b, float tolerance, bool[] keep)
	{
		if (b - a < 2) return;
		int worst = -1;
		float most = tolerance;
		for (int i = a + 1; i < b; i++)
		{
			float t = (i - a) / (float)(b - a);
			float d = 0;
			for (int c = 0; c < values[i].Length; c++)
				d = MathF.Max(d, MathF.Abs(values[i][c] - (values[a][c] + (values[b][c] - values[a][c]) * t)));
			if (d <= most) continue;
			most = d;
			worst = i;
		}
		if (worst < 0) return;
		keep[worst] = true;
		Simplify(values, a, worst, tolerance, keep);
		Simplify(values, worst, b, tolerance, keep);
	}

	private static float[] Components<T>(T value) => value switch
	{
		float f => new[] { f },
		Vector2 v => new[] { v.X, v.Y },
		Vector3 v => new[] { v.X, v.Y, v.Z },
		_ => Array.Empty<float>()
	};

	private static float Distance(float[] a, float[] b)
	{
		float d = 0;
		for (int i = 0; i < a.Length && i < b.Length; i++) d = MathF.Max(d, MathF.Abs(a[i] - b[i]));
		return d;
	}

	private static float NonZero(float v) => MathF.Abs(v) < 1e-6f ? 1e-6f : v;

	private static string UniqueName(List<Animation> animations, string name)
	{
		name = string.IsNullOrWhiteSpace(name) ? "animation" : name.Trim();
		string unique = name;
		for (int i = 2; animations.Any(a => a.Name == unique); i++) unique = $"{name}{i}";
		return unique;
	}
}

// A drawing made into a picture: `Scale` pixels per unit of its own space, its top-left pixel
// corner at Origin. Its size stays known after the pixels are let go (they're copied into assets).
public sealed class XflPicture : IDisposable
{
	public SKBitmap Image { get; }
	public int Width { get; }
	public int Height { get; }
	public Vector2 Origin { get; }
	public float Scale { get; }

	public XflPicture(SKBitmap image, Vector2 origin, float scale)
	{
		Image = image;
		Width = image.Width;
		Height = image.Height;
		Origin = origin;
		Scale = scale;
	}

	// The picture's pixels (y down) → the drawing's own space
	public Matrix3x2 PixelToLocal => Matrix3x2.CreateScale(1 / Scale) * Matrix3x2.CreateTranslation(Origin);

	public void Dispose() => Image.Dispose();
}
