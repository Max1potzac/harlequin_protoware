using System.Globalization;

namespace Animator.Cli;

// The commands: each opens the project (or starts one), does its work through Animator.Engine,
// and saves when it changed something. What they print is meant to be read (and grepped).
public static class Commands
{
	// ======================= info =======================

	public static int Info(Args a)
	{
		string path = a.At(0, "project");
		string? only = a.Get("object");
		bool tree = a.Has("tree");
		a.NothingElse();
		var p = Project.Open(path);
		var scene = p.Scene;
		Console.WriteLine($"{Path.GetFileName(path)}: {scene.Kind.ToString().ToLowerInvariant()} project, {scene.Fps} fps, {scene.Objects.Count} object(s)");
		foreach (var obj in scene.Objects)
		{
			if (only != null && obj.Name != only) continue;
			var sk = obj.Skeleton;
			Console.WriteLine();
			Console.WriteLine($"{obj.Name} ({obj.Kind.ToString().ToLowerInvariant()}): {sk.AllBones().Count()} bone(s), {sk.Slots.Count} part(s)" +
							  (obj.Visible ? "" : ", hidden") + (obj.Export ? "" : ", not exported"));
			Console.WriteLine($"  animations ({obj.Animations.Count}):");
			foreach (var anim in obj.Animations)
				Console.WriteLine($"    {anim.Name}  {anim.Duration.ToString("0.###", CultureInfo.InvariantCulture)} s, {Math.Round(anim.Duration * scene.Fps)} frames" +
								  (anim.Loop ? ", loops" : "") + (anim.Name == ScenePlayer.Preview(obj)?.Name ? "  (preview)" : ""));
			if (sk.Skins.Count > 0)
			{
				Console.WriteLine($"  skins ({sk.Skins.Count}):");
				foreach (var group in sk.Skins.GroupBy(s => s.Overlay ? "overlays" : s.Category.Length == 0 ? "outfits" : s.Category))
					Console.WriteLine($"    {group.Key}: " + string.Join(", ", group.Select(s =>
						s.Name + (sk.ShownSkins.Contains(s) ? " (on)" : "") + $" [{SkinOps.PartsOf(sk, s).Count}]")));
			}
			if (tree)
			{
				Console.WriteLine("  bones (and their parts):");
				foreach (var root in sk.Roots) PrintBone(sk, root, 2);
			}
		}
		if (only != null && scene.Find(only) == null) throw new UsageException($"There's no object \"{only}\".");
		return 0;
	}

	private static void PrintBone(Skeleton sk, Bone bone, int depth)
	{
		var parts = sk.Slots.Where(s => s.Bone == bone).Select(s => s.Name).ToList();
		Console.WriteLine(new string(' ', depth * 2) + bone.Name + (parts.Count > 0 ? "  : " + string.Join(", ", parts) : ""));
		foreach (var child in bone.Children) PrintBone(sk, child, depth + 1);
	}

	// ======================= import =======================

	public static int Import(Args a)
	{
		string path = a.At(0, "project");
		bool rig = !a.Has("no-rig");
		bool split = !a.Has("no-split");
		float scale = a.Float("scale", 1f);
		a.NothingElse();
		if (a.Positional.Count < 2) throw new UsageException("Name the files to import after the project.");
		var p = Project.OpenOrNew(path);
		bool first = p.Scene.Objects.Count == 0;
		foreach (string file in a.Positional.Skip(1))
		{
			SceneObject made;
			if (Path.GetExtension(file).Equals(".psd", StringComparison.OrdinalIgnoreCase))
			{
				using var plan = PsdImportPlan.FromDocument(PsdReader.Read(file), Path.GetFileNameWithoutExtension(file));
				if (plan.Parts.Count == 0) throw new InvalidDataException($"{file}: every layer is empty.");
				plan.SuggestRig = rig;
				plan.Scale = scale;
				if (rig) _ = plan.Masks;
				made = PsdImporter.IntoScene(p.Scene, p.Assets, plan, Vector2.Zero);
			}
			else
			{
				using var plan = XflImportPlan.Load(AnimateBringIn.AnimateFile(file), AnimateBringIn.ProjectName(file));
				plan.SuggestRig = rig;
				plan.SplitAtLabels = split;
				plan.Scale = scale;
				if (first) p.Scene.Fps = Math.Max(1, (int)MathF.Round(plan.Data.FrameRate));
				made = XflImporter.IntoScene(p.Scene, p.Assets, plan, Vector2.Zero);
				foreach (string note in plan.Notes) Console.WriteLine("  note: " + note);
			}
			first = false;
			Console.WriteLine($"{made.Name}: {made.Skeleton.AllBones().Count()} bone(s), {made.Skeleton.Slots.Count} part(s), {made.Animations.Count} animation(s)");
		}
		p.Save();
		return 0;
	}

	// ======================= bring-in =======================

	public static int BringIn(Args a)
	{
		string path = a.At(0, "project");
		var p = Project.Open(path);
		var target = p.Object(a.Need("object"));
		var bones = new List<Bone>();
		foreach (string name in a.List("bones"))
			bones.Add(target.Skeleton.FindBone(name) ?? throw new UsageException($"{target.Name} has no bone \"{name}\" (harlequin info --tree shows them)."));
		var include = a.List("include");
		if (bones.Count == 0 && include.Count == 0) throw new UsageException("--bones is needed: the bones whose parts come in (head, for heads), or --include.");
		var options = new BringInOptions(bones, a.Get("category") ?? "Head", a.Has("animations"), !a.Has("loose"), !a.Has("no-accessories"), a.List("except"), Pairs(a.List("map")), include);
		a.NothingElse();
		if (a.Positional.Count < 2) throw new UsageException("Name the Animate projects to bring in after the project.");
		int brought = 0;
		var notes = new HashSet<string>();
		foreach (string file in a.Positional.Skip(1))
		{
			try
			{
				var b = AnimateBringIn.FromAnimate(file, target.Skeleton, target.Animations, p.Assets, options);
				Console.WriteLine(AnimateBringIn.Describe(b));
				if (b.Result == null) continue;
				brought++;
				foreach (string note in b.Result.Notes) notes.Add(note);
			}
			catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
			{
				Console.WriteLine($"{AnimateBringIn.ProjectName(file)}: left out: {ex.Message}");
			}
		}
		foreach (string note in notes) Console.WriteLine("  note: " + note);
		if (brought > 0) p.Save();
		Console.WriteLine($"{brought} of {a.Positional.Count - 1} brought in.");
		return brought > 0 ? 0 : 1;
	}

	// ======================= show, rename, remove =======================

	public static int Show(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		var obj = p.Object(a.Need("object"));
		var sk = obj.Skeleton;
		if (a.Has("none")) sk.ShownSkins.Clear();
		foreach (string name in a.List("hide")) sk.ShownSkins.Remove(p.Skin(obj, name));
		foreach (string name in a.List("skins")) SkinOps.Show(sk, p.Skin(obj, name));
		a.NothingElse();
		Console.WriteLine($"{obj.Name}: on: " + (sk.ShownSkins.Count == 0 ? "(none)" : string.Join(", ", sk.Skins.Where(sk.ShownSkins.Contains).Select(s => s.Name))));
		p.Save();
		return 0;
	}

	// A new skin from parts' own drawings: those parts then show only while it's on
	public static int Skin(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		var obj = p.Object(a.Need("object"));
		string name = a.Need("name");
		var parts = a.List("parts").Select(n => obj.Skeleton.FindSlot(n) ?? throw new UsageException($"{obj.Name} has no part \"{n}\".")).ToList();
		string category = a.Get("category") ?? "";
		bool overlay = a.Has("overlay");
		bool on = a.Has("on");
		a.NothingElse();
		if (parts.Count == 0) throw new UsageException("--parts is needed: the parts whose drawings make the skin.");
		var skin = SkinOps.MakeSkin(obj.Skeleton, name, parts, category, overlay);
		if (!on) SkinOps.Show(obj.Skeleton, skin, false);
		Console.WriteLine($"{skin.Name}: {parts.Count} part(s)" + (category.Length > 0 ? $", in {category}" : "") + (overlay ? ", an overlay" : "") + (on ? ", on" : ""));
		p.Save();
		return 0;
	}

	public static int Rename(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		var obj = p.Object(a.Need("object"));
		string to = a.Need("to").Trim();
		var skin = a.Get("skin") is { } skinName ? p.Skin(obj, skinName) : null;
		a.NothingElse();
		if (to.Length == 0) throw new UsageException("--to is empty.");
		if (skin != null)
		{
			string old = skin.Name;
			if (!SkinOps.Rename(obj.Skeleton, skin, to)) throw new UsageException($"{obj.Name} has a skin called \"{to}\" already.");
			p.Save();
			Console.WriteLine($"The skin {old} is {to} now.");
			return 0;
		}
		if (!SceneOps.RenameObject(p.Scene, obj, to)) throw new UsageException($"There's an object called \"{to}\" already.");
		p.Save();
		Console.WriteLine($"Renamed to {to}.");
		return 0;
	}

	public static int Remove(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		var obj = p.Object(a.Need("object"));
		var animations = a.List("animations").Select(n => obj.FindAnimation(n) ?? throw new UsageException($"{obj.Name} has no animation \"{n}\".")).ToList();
		var skins = a.List("skins").Select(n => p.Skin(obj, n)).ToList();
		var parts = a.List("parts").Select(n => obj.Skeleton.FindSlot(n) ?? throw new UsageException($"{obj.Name} has no part \"{n}\".")).ToList();
		a.NothingElse();
		if (animations.Count == 0 && skins.Count == 0 && parts.Count == 0)
		{
			if (!SceneOps.DeleteObject(p.Scene, obj)) throw new UsageException("A project keeps at least one object.");
			Console.WriteLine($"Removed {obj.Name}.");
		}
		foreach (var anim in animations)
		{
			obj.Animations.Remove(anim);
			obj.Clips.RemoveAll(c => c.Animation == anim);
			if (obj.PreviewAnimation == anim.Name) obj.PreviewAnimation = null;
			Console.WriteLine($"Removed the animation {anim.Name}.");
		}
		foreach (var part in parts)
		{
			PartOps.DeletePart(obj.Skeleton, obj.Animations, part);
			Console.WriteLine($"Removed the part {part.Name}.");
		}
		foreach (var skin in skins)
		{
			SkinOps.Delete(obj.Skeleton, obj.Animations, skin);
			Console.WriteLine($"Removed the skin {skin.Name}.");
		}
		p.Save();
		return 0;
	}

	// ======================= render =======================

	public static int Render(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		string output = a.Need("out");
		var obj = a.Get("object") is { } name ? p.Object(name)
			: p.Scene.Objects.FirstOrDefault() ?? throw new UsageException("The project has no objects.");
		var anim = a.Get("animation") is { } an ? obj.FindAnimation(an) ?? throw new UsageException($"{obj.Name} has no animation \"{an}\".") : null;
		float time = a.Get("time") != null ? a.Float("time", 0f) : a.Int("frame", 0) / (float)p.Scene.Fps;
		float scale = a.Float("scale", 1f);
		var background = Color(a.Get("background"));
		foreach (string s in a.List("skins")) SkinOps.Show(obj.Skeleton, p.Skin(obj, s));
		string? sheet = a.Get("sheet");
		int columns = a.Int("columns", 0);
		a.NothingElse();
		if (scale <= 0) throw new UsageException("--scale has to be more than 0.");

		SKBitmap? picture;
		if (sheet != null)
		{
			var skins = obj.Skeleton.Skins.Where(s => sheet.Equals("all", StringComparison.OrdinalIgnoreCase) || SkinOps.SameCategory(s.Category, sheet)).ToList();
			if (skins.Count == 0) throw new UsageException($"{obj.Name} has no skins in \"{sheet}\".");
			using var typeface = Fonts.Find();
			picture = Snapshots.SkinSheet(obj, p.Assets, skins, anim, time, scale, columns, typeface, background);
			Console.WriteLine($"{skins.Count} skin(s): {string.Join(", ", skins.Select(s => s.Name))}" + (typeface == null ? " (no font found: unlabelled)" : ""));
		}
		else picture = Snapshots.Frame(obj, p.Assets, anim, time, scale, background: background);
		if (picture == null) throw new InvalidOperationException("Nothing shows: there's nothing to draw.");
		using (picture)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
			using var data = picture.Encode(SKEncodedImageFormat.Png, 100);
			using var stream = File.Create(output);
			data.SaveTo(stream);
			Console.WriteLine($"{output}: {picture.Width} × {picture.Height}");
		}
		return 0;
	}

	// ======================= export =======================

	public static int Export(Args a)
	{
		var p = Project.Open(a.At(0, "project"));
		var settings = new ExportSettings
		{
			Path = Path.GetFullPath(a.Need("out")),
			Format = a.Need("format").ToLowerInvariant() switch
			{
				"engine" => ExportFormat.Engine,
				"png" or "pngs" => ExportFormat.PngSequence,
				"gif" => ExportFormat.Gif,
				"sheet" or "spritesheet" => ExportFormat.SpriteSheet,
				var f => throw new UsageException($"--format: \"{f}\"? (engine, png, gif or sheet)")
			},
			Scale = a.Float("scale", 1f),
			BakeColorLayers = a.Has("bake"),
			PixelArt = a.Has("pixel-art"),
			WriteEvents = !a.Has("no-events"),
			Background = Color(a.Get("background"))
		};
		if (a.Get("object") is { } name)
		{
			var obj = p.Object(name);
			settings.Scene = false;
			settings.Object = obj;
			var wanted = a.List("animations");
			settings.Animations = wanted.Count == 0 ? obj.Animations.ToList()
				: wanted.Select(n => obj.FindAnimation(n) ?? throw new UsageException($"{obj.Name} has no animation \"{n}\".")).ToList();
		}
		else if (settings.Format == ExportFormat.Engine) throw new UsageException("--format engine exports one object: name it with --object.");
		a.NothingElse();
		Directory.CreateDirectory(Path.GetDirectoryName(settings.Path)!);
		string? last = null;
		foreach (var step in new Exporter(p.Scene, p.Assets, p.Scene.Fps, settings).Run())
			if (step.Step != last)
			{
				Console.WriteLine($"{step.Step} ({step.Done}/{step.Total})");
				last = step.Step;
			}
		Console.WriteLine($"Exported to {settings.Path}.");
		return 0;
	}

	// "a=b" pairs (--map head_105x74=zombie_skull)
	private static Dictionary<string, string> Pairs(List<string> items)
	{
		var pairs = new Dictionary<string, string>();
		foreach (string item in items)
		{
			int eq = item.IndexOf('=');
			if (eq <= 0 || eq == item.Length - 1) throw new UsageException($"\"{item}\": pairs are written theirs=ours.");
			pairs[item[..eq].Trim()] = item[(eq + 1)..].Trim();
		}
		return pairs;
	}

	private static SKColor? Color(string? text)
	{
		if (text == null) return null;
		return SKColor.TryParse(text.StartsWith('#') ? text : "#" + text, out var c) ? c
			: throw new UsageException($"\"{text}\" isn't a colour (RRGGBB, like ffffff).");
	}
}
