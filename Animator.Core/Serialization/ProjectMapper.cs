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

	// A single rig in the old (format 5 and earlier) shape. Kept for tests and old-style callers.
	public static ProjectDto ToDto(Skeleton skeleton, IEnumerable<Animation> animations, int fps)
	{
		var dto = new ProjectDto { Fps = fps };
		WriteRig(dto, skeleton, animations);
		return dto;
	}

	// The whole scene (format 6): every object with its rig and animations
	public static ProjectDto SceneToDto(Scene scene) => new()
	{
		Fps = scene.Fps,
		Objects = scene.Objects.Select(ObjectToDto).ToList(),
		Kind = scene.Kind,
		SceneDuration = scene.Duration,
		SceneLoop = scene.Loop,
		StageWidth = scene.HasStage ? scene.StageWidth : null,
		StageHeight = scene.HasStage ? scene.StageHeight : null
	};

	public static ObjectDto ObjectToDto(SceneObject o)
	{
		var dto = new ObjectDto
		{
			Name = o.Name,
			Kind = o.Kind,
			Position = Vec.From(o.Position),
			Rotation = o.Rotation,
			Scale = Vec.From(o.Scale),
			Visible = o.Visible,
			Locked = o.Locked,
			Export = o.Export,
			Onion = o.Onion,
			PreviewAnimation = o.PreviewAnimation,
			AttachObject = o.AttachObject,
			AttachBone = o.AttachBone,
			Clips = o.LiveClips.Any()
				? o.LiveClips.Select(c => new ClipDto
				{
					Animation = c.Animation.Name, Start = c.Start, Length = c.Length, Offset = c.Offset, Loop = c.Loop
				}).ToList()
				: null
		};
		WriteRig(dto, o.Skeleton, o.Animations);
		return dto;
	}

	// Reads a scene. Files from before scenes (one rig, no objects) become one object.
	public static Scene SceneFromDto(ProjectDto dto, string singleObjectName = "Character")
	{
		var scene = new Scene
		{
			Fps = dto.Fps,
			Kind = dto.Kind ?? ProjectKind.Character,
			Duration = dto.SceneDuration is float d && d > 0 ? d : 5f,
			Loop = dto.SceneLoop ?? true,
			StageWidth = Math.Max(0, dto.StageWidth ?? 0),
			StageHeight = Math.Max(0, dto.StageHeight ?? 0)
		};
		if (dto.Objects is not { Count: > 0 } objects)
		{
			var (skeleton, animations) = FromDto(dto);
			var only = new SceneObject(singleObjectName, ObjectKind.Rigged, skeleton);
			only.Animations.AddRange(animations);
			scene.Objects.Add(only);
			return scene;
		}

		foreach (var o in objects)
		{
			if (scene.Find(o.Name) != null) throw new InvalidDataException($"Two objects are named '{o.Name}'.");
			scene.Objects.Add(ObjectFromDto(o));
		}
		return scene;
	}

	public static SceneObject ObjectFromDto(ObjectDto o)
	{
		var (skeleton, animations) = FromDto(o);
		var obj = new SceneObject(o.Name, o.Kind, skeleton)
		{
			Position = o.Position.ToVector2(),
			Rotation = o.Rotation,
			Scale = o.Scale.ToVector2(),
			Visible = o.Visible,
			Locked = o.Locked,
			Export = o.Export,
			Onion = o.Onion,
			PreviewAnimation = o.PreviewAnimation,
			AttachObject = o.AttachObject,
			AttachBone = o.AttachBone
		};
		obj.Animations.AddRange(animations);

		// Clips name their animation; a clip whose animation isn't there is dropped
		foreach (var c in o.Clips ?? new List<ClipDto>())
			if (obj.FindAnimation(c.Animation) is { } animation && c.Length > 0)
				obj.Clips.Add(new SceneClip(animation, Math.Max(0f, c.Start), c.Length) { Offset = Math.Max(0f, c.Offset), Loop = c.Loop });
		obj.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
		return obj;
	}

	// A deep copy of an object: its own bones, parts, animations and settings, sharing nothing.
	// Its pictures and their layers get new IDs, so painting the copy never changes the original.
	// The pixels live outside the core: LayerSources tells which old layer each new one copies.
	public static (SceneObject Copy, Dictionary<Guid, Guid> LayerSources) CloneObject(SceneObject source, string name)
	{
		var dto = ObjectToDto(source);
		dto.Name = name;

		var newAssetIds = new Dictionary<Guid, Guid>();
		var layerSources = new Dictionary<Guid, Guid>();   // new layer id → the layer it copies
		foreach (var asset in dto.Assets)
		{
			var id = Guid.NewGuid();
			newAssetIds[asset.Id] = id;
			asset.Id = id;
			foreach (var layer in asset.Layers)
			{
				var layerId = Guid.NewGuid();
				layerSources[layerId] = layer.Id;
				layer.Id = layerId;
			}
		}
		foreach (var entry in dto.Skins.SelectMany(s => s.Entries))
			if (newAssetIds.TryGetValue(entry.Asset, out var id))
				entry.Asset = id;

		return (ObjectFromDto(dto), layerSources);
	}

	private static void WriteRig(RigDto dto, Skeleton skeleton, IEnumerable<Animation> animations)
	{

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
				Connected = b.Connected && b.Parent != null ? true : null,
			});
		}

		foreach (var s in skeleton.Slots)
		{
			dto.Slots.Add(new SlotDto
			{
				Name = s.Name,
				Bone = s.Bone.Name,
				Attachment = s.SetupAttachment,
				Blend = s.Blend == BlendMode.Normal ? null : s.Blend,
				Color = s.SetupColor == Vector3.One ? null : Rgb.From(s.SetupColor),
				Opacity = s.SetupOpacity >= 1f ? null : s.SetupOpacity
			});
		}

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

		foreach (var c in skeleton.IkConstraints)
		{
			dto.IkConstraints.Add(new IkDto
			{
				Name = c.Name,
				Bones = c.Bones.Select(b => b.Name).ToList(),
				Target = c.Target.Name,
				Mix = c.SetupMix,
				Bend = c.BendDirection,
				Iterations = c.Iterations
			});
		}

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
		SpaceTimeline sp => new TimelineDto
		{
			Type = "space",
			Target = sp.Target,
			Keys = sp.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, Name = k.Value }).ToList()
		},
		PartTranslateTimeline pt => new TimelineDto
		{
			Type = "partTranslate",
			Target = pt.Target,
			Keys = pt.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		PartRotateTimeline pr => new TimelineDto
		{
			Type = "partRotate",
			Target = pr.Target,
			Keys = pr.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value }).ToList()
		},
		PartScaleTimeline ps => new TimelineDto
		{
			Type = "partScale",
			Target = ps.Target,
			Keys = ps.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		PartColorTimeline pc => new TimelineDto
		{
			Type = "partColor",
			Target = pc.Target,
			Keys = pc.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value.X, Y = k.Value.Y, Z = k.Value.Z }).ToList()
		},
		PartOpacityTimeline po => new TimelineDto
		{
			Type = "partOpacity",
			Target = po.Target,
			Keys = po.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, X = k.Value }).ToList()
		},
		EventTimeline ev => new TimelineDto
		{
			Type = "events",
			Target = ev.Target,
			Keys = ev.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, Name = k.Value.Name, Data = k.Value.Data }).ToList()
		},
		DrawOrderTimeline order => new TimelineDto
		{
			Type = "drawOrder",
			Target = order.Target,
			Keys = order.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, Order = k.Value.ToList() }).ToList()
		},
		_ => throw new NotSupportedException($"Saving '{timeline.GetType().Name}' is not supported yet.")
	};

	// ======================= file -> model =======================

	public static (Skeleton Skeleton, List<Animation> Animations) FromDto(RigDto dto)
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
				Connected = b.Connected ?? false,
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
		{
			skeleton.Slots.Add(new Slot(s.Name, FindBone(s.Bone))
			{
				SetupAttachment = s.Attachment,
				Blend = s.Blend ?? BlendMode.Normal,
				SetupColor = s.Color?.ToVector3() ?? Vector3.One,
				SetupOpacity = s.Opacity ?? 1f
			});
		}

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
			case "space":
				{
					var tl = new SpaceTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.Name, CurveType.Stepped);
					return tl;
				}
			case "partTranslate":
				{
					var tl = new PartTranslateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve);
					return tl;
				}
			case "partRotate":
				{
					var tl = new PartRotateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 0f, k.Curve);
					return tl;
				}
			case "partScale":
				{
					var tl = new PartScaleTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 1f, k.Y ?? 1f), k.Curve);
					return tl;
				}
			case "partColor":
				{
					var tl = new PartColorTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector3(k.X ?? 1f, k.Y ?? 1f, k.Z ?? 1f), k.Curve);
					return tl;
				}
			case "partOpacity":
				{
					var tl = new PartOpacityTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 1f, k.Curve);
					return tl;
				}
			case "events":
				{
					var tl = new EventTimeline();
					foreach (var k in t.Keys) tl.Add(k.T, new AnimationEvent(k.Name ?? "event", k.Data));
					return tl;
				}
			case "drawOrder":
				{
					var tl = new DrawOrderTimeline();
					foreach (var k in t.Keys) tl.Add(k.T, k.Order ?? new List<string>());
					return tl;
				}
			default:
				throw new InvalidDataException($"Unknown timeline type '{t.Type}'.");
		}
	}
	// A deep copy (the same trick as saving and re-loading), with a new name
	public static Animation CloneAnimation(Animation source, string newName)
	{
		var copy = new Animation { Name = newName, Duration = source.Duration, Loop = source.Loop };
		copy.Timelines.AddRange(source.Timelines.Select(TimelineToDto).Select(TimelineFromDto));
		return copy;
	}
}