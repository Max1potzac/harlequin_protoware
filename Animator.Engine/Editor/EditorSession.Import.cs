using System.Numerics;

namespace Animator.Viewer;

// What the files of a bring-in from Animate projects came to: a line for each that came in, and
// for each left out (and why), and what's worth knowing about them all
public sealed record BatchBroughtIn(List<string> Brought, List<string> Problems, List<string> Notes);

// Bringing things into the project (EditorSession's side of File → Import and Edit → Bring in):
// files read (a Photoshop file, an Animate project, a sprite sheet's JSON), then put into the scene
// as new objects or into the open object as parts; and another object's animations and skins, or
// Animate projects' parts as skins, merged into the open one. Reading says what's wrong with a file
// rather than throwing; each import is one undo step.
public sealed partial class EditorSession
{
	// ======================= reading files =======================

	// A Photoshop file's layers, ready for the import dialog (null: problem says why it can't be read)
	public static PsdImportPlan? ReadPhotoshop(string path, out string? problem)
	{
		problem = null;
		try
		{
			var plan = PsdImportPlan.FromDocument(PsdReader.Read(path), Path.GetFileNameWithoutExtension(path));
			_ = plan.Masks;   // the layers' shapes, for suggesting a rig: worked out now, while the reader waits
			return plan;
		}
		catch (Exception ex)
		{
			problem = ex is NotSupportedException or InvalidDataException or IOException
				? ex.Message
				: $"Something in it couldn't be read ({ex.GetType().Name}: {ex.Message}).";
			return null;
		}
	}

	// An Animate project (.fla, .xfl, DOMDocument.xml or a zipped XFL folder), ready for the import
	// dialog (null: problem says why it can't be read)
	public static XflImportPlan? ReadAnimate(string path, out string? problem)
	{
		problem = null;
		try
		{
			return XflImportPlan.Load(path, AnimateBringIn.ProjectName(path));
		}
		catch (Exception ex)
		{
			problem = ex is NotSupportedException or InvalidDataException or IOException or System.Xml.XmlException
				? ex.Message
				: $"Something in it couldn't be read ({ex.GetType().Name}: {ex.Message}).";
			return null;
		}
	}

	// The JSON a sheet program (or Harlequin's own export) wrote next to a sheet, if there's one
	public static SpriteSheetJson? ReadSheetJson(string sheetPath)
	{
		string path = Path.ChangeExtension(sheetPath, ".json");
		if (!File.Exists(path)) return null;
		try
		{
			var json = SpriteSheetJson.Read(File.ReadAllText(path));
			return json.Frames.Count > 0 ? json : null;
		}
		catch (Exception)
		{
			return null;   // not a sheet's JSON: the sheet gets cut some other way
		}
	}

	// ======================= importing =======================

	// A Photoshop file's layers as a new object at this place in the scene
	public SceneObject? ImportPhotoshop(PsdImportPlan plan, Vector2 at, string name)
	{
		SceneObject? made = null;
		EditScene(() =>
		{
			made = PsdImporter.IntoScene(Scene, Assets, plan, at);
			return $"Import {name}";
		});
		return made;
	}

	// An Animate project's parts and animations as a new object at this place in the scene
	public SceneObject? ImportAnimate(XflImportPlan plan, Vector2 at, string name)
	{
		SceneObject? made = null;
		EditScene(() =>
		{
			made = XflImporter.IntoScene(Scene, Assets, plan, at);
			return $"Import {name}";
		});
		return made;
	}

	// The bone a sprite sheet's pieces would go on as parts of the open object: a drawn object's
	// own, else the selected bone (else the first). Null: no object open, or it has no bones.
	public Bone? BoneForSheet() => !Editing ? null
		: Active!.Kind == ObjectKind.Drawn ? Rig.Roots.FirstOrDefault()
		: State.Selected ?? Rig.Roots.FirstOrDefault();

	// A sprite sheet's pieces as parts of the open object, on this bone; the first one selected
	public List<Slot> ImportSheetIntoObject(SheetImportPlan plan, Bone bone, string name)
	{
		var made = new List<Slot>();
		if (!Editing) return made;
		var target = Active!;
		State.EditStructure(() =>
		{
			made = SheetImporter.IntoObject(target, bone, Assets, plan, State.Fps);
			return made.Count > 0 ? name : null;
		});
		if (made.Count == 0) return made;
		State.Playing = false;
		State.SelectedSlot = made[0];
		State.Selected = made[0].Bone;
		return made;
	}

	// A sprite sheet's pieces as drawn objects around this place in the scene
	public List<SceneObject> ImportSheetIntoScene(SheetImportPlan plan, Vector2 around, string name)
	{
		var made = new List<SceneObject>();
		EditScene(() =>
		{
			made = SheetImporter.IntoScene(Scene, Assets, plan, around, State.Fps);
			return made.Count > 0 ? name : null;
		});
		return made;
	}

	// ======================= bringing in =======================

	// Another object's animations, drawings and parts into the open one, as the plan says (see
	// ObjectMerge). A skin it makes is put on. Null: nothing came in.
	public MergeResult? BringIn(SceneObject source, MergePlan plan)
	{
		if (!Editing) return null;
		MergeResult? result = null;
		bool done = State.EditStructure(() =>
		{
			result = ObjectMerge.Merge(source.Skeleton, source.Animations, Rig, State.Animations, plan);
			foreach (var (newLayer, oldLayer) in result.LayerSources) Assets.CopyPixels(oldLayer, newLayer);
			return result.Animations == 0 && result.Skin == null && result.NewParts.Count == 0 ? null : $"Bring in from {source.Name}";
		});
		if (!done || result == null) return null;
		if (result.Skin is { } skin) SkinOps.Show(Rig, skin);
		Rig.SetToSetupPose();
		return result;
	}

	// Animate projects' parts (on the options' bones) into the open object, a skin each, as one
	// undo step. A file that can't be read is left out, saying why.
	public BatchBroughtIn BringInFromAnimate(IEnumerable<string> files, BringInOptions options)
	{
		var brought = new List<string>();
		var problems = new List<string>();
		var notes = new List<string>();
		if (!Editing) return new BatchBroughtIn(brought, problems, notes);
		State.EditStructure(() =>
		{
			foreach (string file in files)
			{
				try
				{
					var b = AnimateBringIn.FromAnimate(file, Rig, State.Animations, Assets, options);
					if (b.Problem != null)
					{
						problems.Add(AnimateBringIn.Describe(b));
						continue;
					}
					brought.Add(AnimateBringIn.Describe(b));
					foreach (string note in b.Result!.Notes)
						if (!note.StartsWith("Drawings lined up", StringComparison.Ordinal) && !notes.Contains(note)) notes.Add(note);
				}
				catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
				{
					problems.Add($"{AnimateBringIn.ProjectName(file)}: {ex.Message}");
				}
			}
			return brought.Count > 0 ? $"Bring in {brought.Count} skin(s)" : null;
		});
		Rig.SetToSetupPose();
		return new BatchBroughtIn(brought, problems, notes);
	}
}
