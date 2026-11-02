namespace Animator.Tests;

// Bringing one object into another (ObjectMerge): one zombie's head as a skin on another's body,
// and its animations onto the other's bones, matched by name past each one's prefix
public class ObjectMergeTests
{
	// A little zombie: root → body → head (and, for the source, a hat bone on the head). Parts:
	// body, head (with a hurt drawing), and for the source a hat; names start with the prefix.
	private static (Skeleton Skeleton, List<Animation> Animations) Zombie(string p, float headRest, bool hat)
	{
		var s = new Skeleton();
		var root = new Bone { Name = p + "root", Length = 10 };
		var body = new Bone { Name = p + "body", Length = 60, SetupPosition = new Vector2(0, 40), SetupRotation = MathF.PI / 2 };
		var head = new Bone { Name = p + "head", Length = 30, SetupPosition = new Vector2(60, 0), SetupRotation = headRest };
		s.Roots.Add(root);
		root.AddChild(body);
		body.AddChild(head);
		Asset Picture(string name)
		{
			var a = new Asset { Name = name, Width = 20, Height = 20 };
			a.Layers.Add(new AssetLayer { Name = "Base" });
			s.Assets[a.Id] = a;
			return a;
		}
		void Part(string name, Bone bone, params string[] drawings)
		{
			var slot = new Slot(p + name, bone) { SetupAttachment = p + drawings[0] };
			s.Slots.Add(slot);
			foreach (string d in drawings)
				s.DefaultSkin.Set(slot.Name, new Attachment { Name = p + d, AssetId = Picture(p + d).Id, Offset = new Vector2(5, 1) });
		}
		Part("body", body, "body");
		Part("head", head, "head", "head_hurt");
		if (hat)
		{
			var hatBone = new Bone { Name = p + "hat", Length = 10, SetupPosition = new Vector2(30, 0) };
			head.AddChild(hatBone);
			Part("hat", hatBone, "hat");
		}
		s.SetToSetupPose();
		s.UpdateWorld();

		var walk = new Animation { Name = "walk", Duration = 1, Loop = true };
		var turn = new RotateTimeline(p + "head");
		turn.Track.Add(0, 0.5f);
		walk.Timelines.Add(turn);
		var swap = new AttachmentTimeline(p + "head");
		swap.Add(0.5f, p + "head_hurt");
		walk.Timelines.Add(swap);
		walk.GetOrCreateEvents().Add(0.25f, new AnimationEvent("step"));
		var deform = new DeformTimeline(p + "head");
		deform.Track.Add(0, new DeformKey(p + "head", new[] { Vector2.Zero }));
		walk.Timelines.Add(deform);
		walk.GetOrCreateDrawOrder().Add(0.5f, new[] { p + "head", p + "body" });
		return (s, new List<Animation> { walk });
	}

	[Fact]
	public void Prefixes_are_guessed_and_names_matched_past_them()
	{
		Assert.Equal("zombie_egypt_", ObjectMerge.GuessPrefix(new[] { "zombie_egypt_jaw", "zombie_egypt_skull", "zombie_egypt_torso", "ink", "butter" }));
		Assert.Equal("", ObjectMerge.GuessPrefix(new[] { "head", "body" }));
		var m = ObjectMerge.Match(new[] { "zombie_egypt_jaw", "zombie_egypt_flag", "ink" },
			new[] { "zombie_pirate_basic_jaw", "ink" }, "zombie_egypt_", "zombie_pirate_basic_");
		Assert.Equal("zombie_pirate_basic_jaw", m["zombie_egypt_jaw"]);
		Assert.Equal("ink", m["ink"]);
		Assert.Null(m["zombie_egypt_flag"]);
		Assert.Equal("zombie_pirate_basic_flag", ObjectMerge.Renamed("zombie_egypt_flag", "zombie_egypt_", "zombie_pirate_basic_"));
	}

	[Fact]
	public void The_suggestion_matches_everything_it_can()
	{
		var (mummy, walks) = Zombie("egypt_", 0.2f, hat: true);
		var (pirate, _) = Zombie("pirate_", 0.1f, hat: false);
		var plan = ObjectMerge.Suggest(mummy, walks, pirate);
		Assert.Equal("egypt_", plan.SourcePrefix);
		Assert.Equal("pirate_", plan.TargetPrefix);
		Assert.Equal("pirate_head", plan.Bones["egypt_head"]);
		Assert.Null(plan.Bones["egypt_hat"]);
		Assert.Equal("pirate_head", plan.Parts["egypt_head"]);
		Assert.Null(plan.Parts["egypt_hat"]);
		Assert.Equal(new[] { "walk" }, plan.Animations);
	}

	[Fact]
	public void A_head_comes_in_as_a_skin_with_its_animations()
	{
		var (mummy, walks) = Zombie("egypt_", 0.2f, hat: true);
		var (pirate, pirateAnimations) = Zombie("pirate_", 0.1f, hat: false);
		var plan = ObjectMerge.Suggest(mummy, walks, pirate);
		plan.SkinName = "mummy";
		plan.SkinCategory = "Head";
		plan.SkinParts.Add("egypt_head");
		plan.SkinParts.Add("egypt_hat");
		plan.NewParts.Add("egypt_hat");
		plan.Suffix = "_mummy";

		var result = ObjectMerge.Merge(mummy, walks, pirate, pirateAnimations, plan);

		// The skin: the head's two drawings, under this object's names, with pictures of their own
		var skin = Assert.IsType<Skin>(result.Skin);
		Assert.Equal("Head", skin.Category);
		Assert.Equal(new[] { "pirate_head", "pirate_head_hurt" }, skin.AttachmentsFor("pirate_head").Select(a => a.Name).OrderBy(n => n));
		Assert.All(skin.AttachmentsFor("pirate_head"), a => Assert.True(pirate.Assets.ContainsKey(a.AssetId)));
		Assert.DoesNotContain(skin.AttachmentsFor("pirate_head"), a => mummy.Assets.ContainsKey(a.AssetId));
		Assert.Equal(new Vector2(5, 1), skin.AttachmentsFor("pirate_head").First().Offset);   // (as it sat on its bone)
		Assert.Equal(3, result.LayerSources.Count);   // head, head_hurt, hat (and nothing of the body)

		// The hat: a new part on the head (its own bone isn't here), shown only with the skin on
		var hat = Assert.Single(result.NewParts);
		Assert.Equal("pirate_hat", hat.Name);
		Assert.Equal("pirate_head", hat.Bone.Name);
		Assert.Equal(pirate.Slots.IndexOf(pirate.FindSlot("pirate_head")!) + 1, pirate.Slots.IndexOf(hat));
		Assert.Null(pirate.ResolveAttachment(hat));
		SkinOps.Show(pirate, skin);
		Assert.NotNull(pirate.ResolveAttachment(hat));

		// The animation: on this object's head, as it looked (0.2 + 0.5 at rest there, so 0.6 here)
		var walk = pirateAnimations.Single(a => a.Name == "walk_mummy");
		var turn = walk.Find<RotateTimeline>("pirate_head")!;
		Assert.Equal(0.6f, turn.Track.Keys[0].Value, 4);
		Assert.Equal("pirate_head_hurt", walk.Find<AttachmentTimeline>("pirate_head")!.Track.Keys[0].Value);
		Assert.Equal(new[] { "pirate_head", "pirate_body" }, walk.DrawOrder!.Track.Keys[0].Value);
		Assert.Single(walk.Events!.Track.Keys);
		Assert.Empty(walk.Timelines.OfType<DeformTimeline>());
		Assert.Contains(result.Notes, n => n.Contains("deform"));
		Assert.Equal(2, pirateAnimations.Count);   // its own walk is still there

		// It plays
		pirate.SetToSetupPose();
		walk.Apply(pirate, 0.6f);
		Assert.Equal(0.1f + 0.6f, pirate.FindBone("pirate_head")!.Rotation, 4);
		Assert.Equal("pirate_head_hurt", pirate.FindSlot("pirate_head")!.ActiveAttachment);
		Assert.Equal(0, pirate.ResolveAttachment(pirate.FindSlot("pirate_head")!)!.AssetId.CompareTo(skin.Get("pirate_head", "pirate_head_hurt")!.AssetId));
	}

	[Fact]
	public void As_offsets_the_keys_stay_as_they_are()
	{
		var (mummy, walks) = Zombie("egypt_", 0.2f, hat: false);
		var (pirate, pirateAnimations) = Zombie("pirate_", 0.1f, hat: false);
		var plan = ObjectMerge.Suggest(mummy, walks, pirate);
		plan.Absolute = false;
		plan.Suffix = "";
		var result = ObjectMerge.Merge(mummy, walks, pirate, pirateAnimations, plan);
		Assert.Null(result.Skin);
		var walk = pirateAnimations.Single(a => a.Name == "walk 2");   // (its own walk was there)
		Assert.Equal(0.5f, walk.Find<RotateTimeline>("pirate_head")!.Track.Keys[0].Value, 4);
	}

	[Fact]
	public void Imported_drawings_take_the_first_name_that_means_something()
	{
		Assert.True(Animator.Core.Import.XflEvaluator.SaysNothing("zombie_pirate_basic_28x33"));
		Assert.True(Animator.Core.Import.XflEvaluator.SaysNothing("zombie_egypt_basic_85x85_2"));
		Assert.True(Animator.Core.Import.XflEvaluator.SaysNothing("Symbol 12"));
		Assert.False(Animator.Core.Import.XflEvaluator.SaysNothing("zombie_pirate_basic_jaw"));
		Assert.False(Animator.Core.Import.XflEvaluator.SaysNothing("arm_2"));
	}
}
