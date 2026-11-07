using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer;

// The open object's hitboxes and points, and its parts' meshes, edited (EditorSession's side of
// the Boxes view and the mesh controls): added, deleted, renamed, fitted, put on bones, switched
// on and off; meshes made, removed and weighted. Each edit is one undo step and says whether it
// changed anything. Nothing here at scene level.
public sealed partial class EditorSession
{
	// ======================= hitboxes and points =======================

	public bool EditBoxes(string name, Func<bool> change) => !SceneLevel && State.EditStructure(() => change() ? name : null);

	// Where a new hitbox or point goes: the selected bone, the selected part's, or the first root
	public Bone? BoxBone() => State.Selected ?? State.SelectedSlot?.Bone ?? Rig.Roots.FirstOrDefault();

	// A hitbox on a bone (round the selected part, when it's on that bone). Null: no bone, or scene level.
	public Hitbox? AddHitbox(HitboxShape shape, string kind)
	{
		if (SceneLevel || BoxBone() is not { } bone) return null;
		var part = State.SelectedSlot;
		Hitbox? made = null;
		EditBoxes($"Add a {kind} {(shape == HitboxShape.Box ? "box" : "circle")} on {bone.Name}", () =>
		{
			made = HitboxOps.AddHitbox(Rig, bone, shape, kind, part != null ? $"{part.Name} {kind}" : null);
			if (part != null && part.Bone == bone) FitBox(made, part);
			return true;
		});
		return made;
	}

	// A point at the tip of the bone (null: no bone, or scene level)
	public RigPoint? AddPoint()
	{
		if (SceneLevel || BoxBone() is not { } bone) return null;
		RigPoint? made = null;
		EditBoxes($"Add a point on {bone.Name}", () => { made = HitboxOps.AddPoint(Rig, bone); return true; });
		return made;
	}

	// A hitbox (its keys too) or a point gone
	public bool DeleteBoxItem(object item) => item switch
	{
		Hitbox hitbox => EditBoxes($"Delete the hitbox {hitbox.Name}", () => { HitboxOps.DeleteHitbox(Rig, State.Animations, hitbox); return true; }),
		RigPoint point => EditBoxes($"Delete the point {point.Name}", () => Rig.Points.Remove(point)),
		_ => false
	};

	// Round a part's visible pixels as it rests (false: it shows nothing there)
	public bool FitHitbox(Hitbox hitbox, Slot part)
	{
		bool fitted = EditBoxes($"Fit {hitbox.Name} round {part.Name}", () =>
		{
			Rig.SetToSetupPose();
			Rig.UpdateWorld();
			return FitBox(hitbox, part);
		});
		State.SetFrame(State.Frame);   // (back to how the animation poses it)
		return fitted;
	}

	private bool FitBox(Hitbox hitbox, Slot part)
	{
		(float, float, float, float)? visible = null;
		if (part.SetupAttachment is { } name && Rig.FindAttachment(part.Name, name) is { } drawing
			&& Rig.Assets.TryGetValue(drawing.AssetId, out var asset) && Assets.Get(asset, false) is { } bitmap)
			visible = VisibleRect(bitmap);
		return HitboxOps.FitToPart(Rig, hitbox, part, visible);
	}

	// The rectangle of a picture's pixels that show (null: none do)
	public static (float X, float Y, float W, float H)? VisibleRect(SKBitmap bitmap)
	{
		int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
		for (int y = 0; y < bitmap.Height; y++)
			for (int x = 0; x < bitmap.Width; x++)
				if (bitmap.GetPixel(x, y).Alpha > 8)
				{
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
		return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
	}

	// Animate mode: on or off from the playhead on
	public bool KeyHitbox(Hitbox hitbox, bool on)
	{
		if (!State.AnimateMode) return false;
		int frame = State.Frame;
		bool changed = EditBoxes($"{hitbox.Name} {(on ? "on" : "off")} @ frame {frame}", () => HitboxOps.KeyActive(State.Animation, hitbox, frame, State.Fps, on));
		State.SetFrame(frame);
		return changed;
	}

	// False: empty, unchanged, or another hitbox's (or point's) name
	public bool RenameBoxItem(object item, string name)
	{
		name = name.Trim();
		return item switch
		{
			Hitbox h when name.Length > 0 && name != h.Name && Rig.FindHitbox(name) == null =>
				EditBoxes($"Rename the hitbox to {name}", () => HitboxOps.RenameHitbox(Rig, State.Animations, h, name)),
			RigPoint p when name.Length > 0 && name != p.Name && Rig.FindPoint(name) == null =>
				EditBoxes($"Rename the point to {name}", () => HitboxOps.RenamePoint(Rig, p, name)),
			_ => false
		};
	}

	// What a game takes it for (hurt, attack, ...)
	public bool SetHitboxKind(Hitbox hitbox, string kind)
	{
		kind = kind.Trim();
		return kind.Length > 0 && kind != hitbox.Kind && EditBoxes($"Make {hitbox.Name} a {kind} box", () => { hitbox.Kind = kind; return true; });
	}

	// A box or a circle (a circle as wide as the box was on average)
	public bool SetHitboxShape(Hitbox hitbox, HitboxShape shape) =>
		shape != hitbox.Shape && EditBoxes($"Make {hitbox.Name} a {(shape == HitboxShape.Box ? "box" : "circle")}", () =>
		{
			hitbox.Shape = shape;
			if (shape == HitboxShape.Circle) hitbox.Size = new Vector2((hitbox.Size.X + hitbox.Size.Y) / 2f);
			return true;
		});

	public bool SetBoxBone(object item, Bone bone) => item switch
	{
		Hitbox h => EditBoxes($"Put {h.Name} on {bone.Name}", () => HitboxOps.SetBone(h, bone)),
		RigPoint p => EditBoxes($"Put {p.Name} on {bone.Name}", () => HitboxOps.SetBone(p, bone)),
		_ => false
	};

	// On or off at rest
	public bool SetHitboxActive(Hitbox hitbox, bool on)
	{
		if (hitbox.SetupActive == on) return false;
		bool changed = EditBoxes($"{hitbox.Name} {(on ? "on" : "off")} at rest", () => { hitbox.SetupActive = on; return true; });
		State.SetFrame(State.Frame);
		return changed;
	}

	// Its numbers: where its middle is on its bone, its size (a circle: both the diameter), its
	// angle (radians); the values given change
	public bool PlaceBoxItem(object item, Vector2? offset = null, Vector2? size = null, float? rotation = null) => item switch
	{
		Hitbox h => EditBoxes(size != null ? $"Resize {h.Name}" : rotation != null ? $"Turn {h.Name}" : $"Move {h.Name}", () =>
		{
			var before = (h.Offset, h.Size, h.Rotation);
			if (offset is { } o) h.Offset = o;
			if (size is { } s) h.Size = h.Shape == HitboxShape.Circle ? new Vector2(MathF.Max(1f, s.X)) : Vector2.Max(Vector2.One, s);
			if (rotation is { } r) h.Rotation = r;
			return (h.Offset, h.Size, h.Rotation) != before;
		}),
		RigPoint p => EditBoxes(rotation != null ? $"Turn {p.Name}" : $"Move {p.Name}", () =>
		{
			var before = (p.Offset, p.Rotation);
			if (offset is { } o) p.Offset = o;
			if (rotation is { } r) p.Rotation = r;
			return (p.Offset, p.Rotation) != before;
		}),
		_ => false
	};

	// What's under a world position: the picked hitbox's handles first (within `reach`, in world
	// units), then points, then the smallest box it's in. Resize: a handle of the picked one.
	public (object Item, bool Resize)? BoxItemAt(Vector2 world, float reach, object? picked)
	{
		if (picked is Hitbox box)
			foreach (var handle in HitboxRenderer.Handles(box))
				if (Vector2.Distance(handle, world) <= reach) return (box, true);
		foreach (var point in Rig.Points.AsEnumerable().Reverse())
			if (Vector2.Distance(point.WorldPosition, world) <= reach) return (point, false);
		var inside = Rig.Hitboxes.Where(h => h.Contains(world)).OrderBy(h => h.Size.X * h.Size.Y).FirstOrDefault();
		return inside != null ? (inside, false) : null;
	}

	// A drag: a hitbox's handle to a world position (round its middle: the corner or edge follows),
	// or a hitbox or point moved by how far the mouse went from where it started
	public static void ResizeHitboxTo(Hitbox box, Vector2 world)
	{
		if (!Matrix3x2.Invert(box.World, out var toBox)) return;
		var local = Vector2.Transform(world, toBox);
		box.Size = box.Shape == HitboxShape.Box
			? new Vector2(MathF.Max(1f, 2f * MathF.Abs(local.X)), MathF.Max(1f, 2f * MathF.Abs(local.Y)))
			: new Vector2(MathF.Max(1f, 2f * local.Length()));
	}

	public static void MoveBoxItem(object item, Vector2 startOffset, Vector2 start, Vector2 world)
	{
		var bone = item is Hitbox hb ? hb.Bone : ((RigPoint)item).Bone;
		if (!Matrix3x2.Invert(bone.World, out var toBone)) return;
		var offset = startOffset + Vector2.TransformNormal(world - start, toBone);
		if (item is Hitbox moved) moved.Offset = offset;
		else ((RigPoint)item).Offset = offset;
	}

	// ======================= meshes =======================

	// A part's drawing that shows now, and its mesh (null: no drawing, or scene level)
	public (Slot Slot, Attachment Drawing, Mesh? Mesh)? MeshOf(Slot? slot) =>
		!SceneLevel && slot != null && Rig.ResolveAttachment(slot) is { } drawing ? (slot, drawing, drawing.Mesh) : null;

	// A change to the part's mesh as one undo step (editing a skin: the skin's own copy of the drawing)
	public bool EditMesh(Slot slot, string name, Func<Slot, Attachment, bool> change)
	{
		if (MeshOf(slot) is not { } p) return false;
		return State.EditStructure(() =>
		{
			var drawing = State.EditableDrawing(p.Slot) ?? p.Drawing;
			return change(p.Slot, drawing) ? name : null;
		});
	}

	// A grid of points over the picture's visible pixels, about `across` cells wide (1: just the corners)
	public bool MakeMesh(Slot slot, int across)
	{
		if (MeshOf(slot) is not { } part || !Rig.Assets.TryGetValue(part.Drawing.AssetId, out var asset)
			|| Assets.Get(asset, false) is not { } bitmap) return false;
		var visible = VisibleRect(bitmap) ?? (0, 0, bitmap.Width, bitmap.Height);
		var area = ((int)visible.X, (int)visible.Y, (int)visible.W, (int)visible.H);
		int step = across <= 1 ? Math.Max(area.Item3, area.Item4) + 1 : Math.Max(2, (int)MathF.Ceiling(Math.Max(area.Item3, area.Item4) / (float)across));
		bool Shows(int x0, int y0, int x1, int y1)
		{
			for (int y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y1); y++)
				for (int x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x1); x++)
					if (bitmap.GetPixel(x, y).Alpha > 8) return true;
			return false;
		}
		return EditMesh(slot, $"Make a mesh on {slot.Name}", (_, drawing) =>
		{
			drawing.Mesh = MeshOps.Grid(bitmap.Width, bitmap.Height, area, step, Shows);
			return true;
		});
	}

	public bool RemoveMesh(Slot slot) => EditMesh(slot, "Remove the mesh", (_, drawing) =>
	{
		if (drawing.Mesh == null) return false;
		drawing.Mesh = null;
		return true;
	});

	// How bones bend a mesh: from the nearest bones (of `among`, if given), all on one bone, or none
	public enum Weights { Nearest, AllOn, None }

	public bool WeighMesh(Slot slot, Weights how, string name, Bone? bone = null, IReadOnlyList<Bone>? among = null) =>
		EditMesh(slot, name, (s, drawing) =>
		{
			if (drawing.Mesh is not { } mesh) return false;
			Rig.SetToSetupPose();   // (weights are given at rest)
			Rig.UpdateWorld();
			drawing.Mesh = how switch
			{
				Weights.Nearest => MeshOps.AutoWeights(Rig, s, drawing, mesh, among is { Count: > 0 } ? among : null),
				Weights.AllOn when bone != null => MeshOps.AllOn(Rig, s, drawing, mesh, bone),
				Weights.None => mesh.Rigid(),
				_ => mesh
			};
			return true;
		});
}
