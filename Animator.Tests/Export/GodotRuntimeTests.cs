using System.IO;
using System.Text.Json;
using Animator.Core.Export;
using Animator.Core.Serialization;
using Animator.Core.Utilities;

namespace Animator.Tests;

// The Godot runtime (godot/addons/harlequin) against Harlequin's own code: godot/tests/expected.json
// holds poses of the sample rig as the Godot runtime works them out (its tests write it: see
// godot/tests/run_tests.gd); here the same steps are worked out with the editor's classes, and
// every bone, part, drawing, draw order, hitbox and mask has to come out the same.
public class GodotRuntimeTests
{
	private static string GodotFolder()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			string folder = Path.Combine(dir.FullName, "godot");
			if (File.Exists(Path.Combine(folder, "tests", "expected.json"))) return folder;
		}
		throw new DirectoryNotFoundException("The godot folder (with tests/expected.json) isn't above " + AppContext.BaseDirectory);
	}

	[Fact]
	public void The_sample_rig_reads()
	{
		var file = EngineFile.FromJson(File.ReadAllText(Path.Combine(GodotFolder(), "demo", "sample.hqrig.json")));
		var (skeleton, animations) = ProjectMapper.FromDto(file.Rig);
		Assert.Equal(17, skeleton.AllBones().Count());
		Assert.Equal(new[] { "walk", "idle" }, animations.Select(a => a.Name));
		Assert.True(skeleton.FindSlot("eye")!.IsMask);
		Assert.Single(skeleton.ShownSkins);
	}

	[Fact]
	public void The_Godot_runtime_poses_the_sample_as_Harlequin_does()
	{
		string godot = GodotFolder();
		var file = EngineFile.FromJson(File.ReadAllText(Path.Combine(godot, "demo", "sample.hqrig.json")));
		var (skeleton, animations) = ProjectMapper.FromDto(file.Rig);
		Animation Anim(JsonElement step, string field) => animations.Single(a => a.Name == step.GetProperty(field).GetString());

		using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(godot, "tests", "expected.json")));
		var samples = expected.RootElement.GetProperty("samples").EnumerateArray().ToList();
		var problems = new List<string>();
		int next = 0;
		void Check()
		{
			if (next >= samples.Count) problems.Add("more poses than expected.json has");
			else Compare(skeleton, file.Atlas, samples[next], problems);
			next++;
		}

		foreach (var step in expected.RootElement.GetProperty("steps").EnumerateArray())
		{
			skeleton.ShownSkins.Clear();
			foreach (var name in step.GetProperty("skins").EnumerateArray())
				SkinOps.Show(skeleton, skeleton.FindSkin(name.GetString()!)!);
			skeleton.ResetPhysics();
			switch (step.GetProperty("do").GetString())
			{
				case "rest":
					skeleton.SetToSetupPose();
					skeleton.UpdatePose();
					Check();
					break;
				case "play":
					foreach (var time in step.GetProperty("times").EnumerateArray())
					{
						float t = time.GetSingle();
						skeleton.SetToSetupPose();
						Anim(step, "anim").Apply(skeleton, t);
						skeleton.UpdatePose();
						skeleton.StepPhysics(t);
						Check();
					}
					break;
				case "crossfade":
					skeleton.SetToSetupPose();
					Mixing.Crossfade(skeleton, Anim(step, "from"), step.GetProperty("from_time").GetSingle(),
						Anim(step, "to"), step.GetProperty("to_time").GetSingle(), step.GetProperty("progress").GetSingle());
					skeleton.UpdatePose();
					skeleton.StepPhysics(0f);
					Check();
					break;
				case "layer":
					skeleton.SetToSetupPose();
					Anim(step, "from").Apply(skeleton, step.GetProperty("from_time").GetSingle());
					Mixing.Layer(skeleton, Anim(step, "to"), step.GetProperty("to_time").GetSingle(), step.GetProperty("progress").GetSingle());
					skeleton.UpdatePose();
					skeleton.StepPhysics(0f);
					Check();
					break;
			}
		}
		Assert.Equal(samples.Count, next);
		Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
	}

	private static void Compare(Skeleton skeleton, EngineAtlas atlas, JsonElement want, List<string> problems)
	{
		string label = want.GetProperty("label").GetString()!;
		static float F(JsonElement e) => e.GetSingle();

		foreach (var b in want.GetProperty("bones").EnumerateObject())
		{
			var bone = skeleton.FindBone(b.Name)!;
			var v = b.Value.EnumerateArray().Select(F).ToArray();
			var at = bone.World.Translation;
			if (MathF.Abs(at.X - v[0]) > 0.02f || MathF.Abs(at.Y - v[1]) > 0.02f || MathF.Abs(MathUtil.ShortestAngle(bone.WorldRotation - v[2])) > 2e-3f)
				problems.Add($"{label}: bone {b.Name} is at ({at.X:0.###}, {at.Y:0.###}, {bone.WorldRotation:0.####}) here, ({v[0]:0.###}, {v[1]:0.###}, {v[2]:0.####}) in Godot");
		}

		foreach (var p in want.GetProperty("parts").EnumerateObject())
		{
			var slot = skeleton.FindSlot(p.Name)!;
			var drawing = skeleton.ResolveAttachment(slot);
			float[]? mine = null;
			if (drawing != null && atlas.Regions.TryGetValue(drawing.AssetId, out var region))
			{
				Vector2[] points;
				if (drawing.Mesh is { } mesh && mesh.Width == region.Width && mesh.Height == region.Height && mesh.Triangles.Length > 0)
					points = MeshOps.Posed(slot, drawing, mesh);
				else
				{
					var m = slot.ImageToBone(drawing, region.Width, region.Height) * slot.Bone.World;
					points = new[] { Vector2.Zero, new Vector2(region.Width, 0), new Vector2(region.Width, region.Height), new Vector2(0, region.Height) }
						.Select(c => Vector2.Transform(c, m)).ToArray();
				}
				mine = points.SelectMany(c => new[] { c.X, c.Y }).ToArray();
			}
			float[]? theirs = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.EnumerateArray().Select(F).ToArray();
			bool same = (mine == null) == (theirs == null)
				&& (mine == null || (mine.Length == theirs!.Length && mine.Zip(theirs).All(z => MathF.Abs(z.First - z.Second) < 0.05f)));
			if (!same)
				problems.Add($"{label}: part {p.Name} is at [{(mine == null ? "none" : string.Join(", ", mine.Select(x => x.ToString("0.##"))))}] here, " +
					$"[{(theirs == null ? "none" : string.Join(", ", theirs.Select(x => x.ToString("0.##"))))}] in Godot");

			string? shows = drawing != null ? $"{drawing.Name} {drawing.AssetId}" : null;
			string? godotShows = want.GetProperty("drawings").GetProperty(p.Name).GetString();
			if (shows != godotShows) problems.Add($"{label}: {p.Name} shows {shows ?? "nothing"} here, {godotShows ?? "nothing"} in Godot");

			var colour = want.GetProperty("colors").GetProperty(p.Name).EnumerateArray().Select(F).ToArray();
			if (MathF.Abs(slot.Color.X - colour[0]) > 1e-3f || MathF.Abs(slot.Color.Y - colour[1]) > 1e-3f || MathF.Abs(slot.Color.Z - colour[2]) > 1e-3f
				|| MathF.Abs(slot.Opacity - colour[3]) > 1e-3f)
				problems.Add($"{label}: {p.Name}'s colour is {slot.Color} × {slot.Opacity} here, [{string.Join(", ", colour)}] in Godot");
		}

		void Same(string what, IEnumerable<string> mine)
		{
			var theirs = want.GetProperty(what).EnumerateArray().Select(e => e.GetString()!).ToList();
			if (!mine.SequenceEqual(theirs))
				problems.Add($"{label}: {what} is [{string.Join(", ", mine)}] here, [{string.Join(", ", theirs)}] in Godot");
		}
		Same("order", skeleton.DrawOrder.Select(s => s.Name));
		Same("hitboxes", skeleton.Hitboxes.Where(h => h.Active).Select(h => h.Name));
		Same("clipped", MaskOps.Clipped(skeleton, skeleton.FindSlot("eye")!).Select(s => s.Name));

		foreach (var l in want.GetProperty("layers").EnumerateObject())
		{
			var now = skeleton.FindColorLayer(l.Name)!.Adjust.Now.ToArray();
			var theirs = l.Value.EnumerateArray().Select(F).ToArray();
			for (int i = 0; i < theirs.Length; i++)
				if (MathF.Abs(now[i] - theirs[i]) > 1e-3f)
				{
					problems.Add($"{label}: colour layer {l.Name}'s number {i} is {now[i]} here, {theirs[i]} in Godot");
					break;
				}
		}
	}
}
