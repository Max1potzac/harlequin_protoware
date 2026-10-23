using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animator.Core.Serialization;

public static class ProjectMapper
{
	// camelCase names, readable enums ("Smooth" instead of 2), nulls left out.
	// JSON always uses "." for decimals, whatever the Windows region setting.
	public static readonly JsonSerializerOptions Json = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() }
	};

	// ======================= model -> file =======================

	public static ProjectDto ToDto(Skeleton skeleton, IEnumerable<Animation> animations, int fps)
	{
		var dto = new ProjectDto { Fps = fps };

		foreach (var b in skeleton.AllBones())   // parents always come before their children
		{
			dto.Bones.Add(new BoneDto
			{
				Name = b.Name,
				Parent = b.Parent?.Name,
				Length = b.Length,
				Position = Vec.From(b.SetupPosition),
				Rotation = b.SetupRotation,
				Scale = Vec.From(b.SetupScale),
				InheritRotation = b.InheritRotation,
				InheritScale = b.InheritScale,
				MinOffset = b.MinOffset is { } min ? Vec.From(min) : null,
				MaxOffset = b.MaxOffset is { } max ? Vec.From(max) : null,
				Turn = b.SetupTurn,
				Tilt = b.SetupTilt,
			});
		}

		foreach (var s in skeleton.Slots)
			dto.Slots.Add(new SlotDto { Name = s.Name, Bone = s.Bone.Name, Attachment = s.SetupAttachment });

		foreach (var a in skeleton.Assets.Values)
		{
			dto.Assets.Add(new AssetDto
			{
				Id = a.Id,
				Name = a.Name,
				Width = a.Width,
				Height = a.Height,
				Layers = a.Layers.Select(l => new LayerDto
				{
					Id = l.Id,
					Name = l.Name,
					Visible = l.Visible,
					Opacity = l.Opacity
				}).ToList()
			});
		}

		dto.Skins.Add(SkinToDto(skeleton.DefaultSkin));
		if (skeleton.ActiveSkin != null && skeleton.ActiveSkin != skeleton.DefaultSkin)
			dto.Skins.Add(SkinToDto(skeleton.ActiveSkin));
		dto.ActiveSkin = skeleton.ActiveSkin?.Name;

		foreach (var c in skeleton.LookConstraints)
		{
			dto.LookConstraints.Add(new LookDto
			{
				Name = c.Name,
				Bone = c.Bone.Name,
				Target = c.Target.Name,
				Shape = c.Shape,
				Radius = Vec.From(c.Radius),
				Follow = c.Follow,
				Mix = c.SetupMix
			});
		}

		foreach (var anim in animations)
		{
			dto.Animations.Add(new AnimationDto
			{
				Name = anim.Name,
				Duration = anim.Duration,
				Loop = anim.Loop,
				Timelines = anim.Timelines.Select(TimelineToDto).ToList()
			});
		}

		return dto;
	}

	private static SkinDto SkinToDto(Skin skin) => new()
	{
		Name = skin.Name,
		Entries = skin.Entries.Select(e => new SkinEntryDto
		{
			Slot = e.Slot,
			Name = e.Attachment.Name,
			Asset = e.Attachment.AssetId,
			Offset = Vec.From(e.Attachment.Offset),
			Rotation = e.Attachment.Rotation,
			Scale = Vec.From(e.Attachment.Scale),
			Pivot = Vec.From(e.Attachment.Pivot)
		}).ToList()
	};

	private static TimelineDto TimelineToDto(Timeline timeline) => timeline switch
	{
		RotateTimeline r => new TimelineDto
		{
			Type = "rotate",
			Target = r.Target,
			Keys = r.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value }).ToList()
		},
		TranslateTimeline t => new TimelineDto
		{
			Type = "translate",
			Target = t.Target,
			Keys = t.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		ScaleTimeline sc => new TimelineDto
		{
			Type = "scale",
			Target = sc.Target,
			Keys = sc.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		IkMixTimeline ik => new TimelineDto
		{
			Type = "ikMix",
			Target = ik.Target,
			Keys = ik.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value }).ToList()
		},
		AttachmentTimeline at => new TimelineDto
		{
			Type = "attachment",
			Target = at.Target,
			Keys = at.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, Name = k.Value }).ToList()
		},
		DepthTimeline d => new TimelineDto
		{
			Type = "depth",
			Target = d.Target,
			Keys = d.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		_ => throw new NotSupportedException($"Saving '{timeline.GetType().Name}' is not supported yet.")
	};

	// ======================= file -> model =======================

	public static (Skeleton Skeleton, List<Animation> Animations) FromDto(ProjectDto dto)
	{
		var skeleton = new Skeleton();
		var bones = new Dictionary<string, Bone>();

		foreach (var b in dto.Bones)
		{
			var bone = new Bone
			{
				Name = b.Name,
				Length = b.Length,
				SetupPosition = b.Position.ToVector2(),
				SetupRotation = b.Rotation,
				SetupScale = b.Scale.ToVector2(),
				InheritRotation = b.InheritRotation,
				InheritScale = b.InheritScale,
				MinOffset = b.MinOffset?.ToVector2(),
				MaxOffset = b.MaxOffset?.ToVector2(),
				SetupTurn = b.Turn,
				SetupTilt = b.Tilt,
			};
			if (!bones.TryAdd(b.Name, bone))
				throw new InvalidDataException($"Two bones are named '{b.Name}'.");

			if (b.Parent == null) skeleton.Roots.Add(bone);
			else if (bones.TryGetValue(b.Parent, out var parent)) parent.AddChild(bone);
			else throw new InvalidDataException($"Bone '{b.Name}' has an unknown parent '{b.Parent}'.");
		}

		Bone FindBone(string name) =>
			bones.TryGetValue(name, out var bone) ? bone : throw new InvalidDataException($"Unknown bone '{name}'.");

		foreach (var s in dto.Slots)
			skeleton.Slots.Add(new Slot(s.Name, FindBone(s.Bone)) { SetupAttachment = s.Attachment });

		foreach (var a in dto.Assets)
		{
			var asset = new Asset { Id = a.Id, Name = a.Name, Width = a.Width, Height = a.Height };
			foreach (var l in a.Layers)
				asset.Layers.Add(new AssetLayer { Id = l.Id, Name = l.Name, Visible = l.Visible, Opacity = l.Opacity });
			skeleton.Assets[asset.Id] = asset;
		}

		foreach (var skinDto in dto.Skins)
		{
			var skin = skinDto.Name == skeleton.DefaultSkin.Name ? skeleton.DefaultSkin : new Skin { Name = skinDto.Name };
			foreach (var e in skinDto.Entries)
			{
				skin.Set(e.Slot, new Attachment
				{
					Name = e.Name,
					AssetId = e.Asset,
					Offset = e.Offset.ToVector2(),
					Rotation = e.Rotation,
					Scale = e.Scale.ToVector2(),
					Pivot = e.Pivot.ToVector2()
				});
			}
			if (skin != skeleton.DefaultSkin && skinDto.Name == dto.ActiveSkin) skeleton.ActiveSkin = skin;
		}

		foreach (var i in dto.IkConstraints)
		{
			var c = new IkConstraint
			{
				Name = i.Name,
				Target = FindBone(i.Target),
				SetupMix = i.Mix,
				Mix = i.Mix,
				BendDirection = i.Bend,
				Iterations = i.Iterations
			};
			c.Bones.AddRange(i.Bones.Select(FindBone));
			skeleton.IkConstraints.Add(c);
		}
		foreach (var l in dto.LookConstraints)
		{
			skeleton.LookConstraints.Add(new LookConstraint
			{
				Name = l.Name,
				Bone = FindBone(l.Bone),
				Target = FindBone(l.Target),
				Shape = l.Shape,
				Radius = l.Radius.ToVector2(),
				Follow = l.Follow,
				SetupMix = l.Mix,
				Mix = l.Mix
			});
		}

		var animations = new List<Animation>();
		foreach (var a in dto.Animations)
		{
			var anim = new Animation { Name = a.Name, Duration = a.Duration, Loop = a.Loop };
			anim.Timelines.AddRange(a.Timelines.Select(TimelineFromDto));
			animations.Add(anim);
		}

		return (skeleton, animations);
	}

	private static Timeline TimelineFromDto(TimelineDto t)
	{
		switch (t.Type)
		{
			case "rotate":
				{
					var tl = new RotateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 0f, k.Curve);
					return tl;
				}
			case "translate":
				{
					var tl = new TranslateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve);
					return tl;
				}
			case "scale":
				{
					var tl = new ScaleTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 1f, k.Y ?? 1f), k.Curve);
					return tl;
				}
			case "ikMix":
				{
					var tl = new IkMixTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 1f, k.Curve);
					return tl;
				}
			case "attachment":
				{
					var tl = new AttachmentTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.Name, CurveType.Stepped);
					return tl;
				}
			case "depth":
				{
					var tl = new DepthTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve);
					return tl;
				}
			default:
				throw new InvalidDataException($"Unknown timeline type '{t.Type}'.");
		}
	}
}