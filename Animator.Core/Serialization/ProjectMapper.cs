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
		StageHeight = scene.HasStage ? scene.StageHeight : null,
		GridSize = MathF.Abs(scene.GridSize - 10f) < 1e-6f ? null : scene.GridSize,
		Guides = scene.Guides.Count > 0 ? scene.Guides.Select(g => new GuideDto { Vertical = g.Vertical, At = g.At }).ToList() : null,
		Sounds = scene.Sounds.Count > 0 ? new Dictionary<string, string>(scene.Sounds) : null
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
					Animation = c.Animation.Name, Start = c.Start, Length = c.Length, Offset = c.Offset, Loop = c.Loop, MixIn = c.MixIn > 0 ? c.MixIn : null
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
			StageHeight = Math.Max(0, dto.StageHeight ?? 0),
			GridSize = dto.GridSize is float grid && grid > 0 ? grid : 10f
		};
		foreach (var g in dto.Guides ?? new List<GuideDto>())
			scene.Guides.Add(new Guide(g.Vertical, g.At));
		foreach (var (name, path) in dto.Sounds ?? new Dictionary<string, string>())
			scene.Sounds[name] = path;
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
				obj.Clips.Add(new SceneClip(animation, Math.Max(0f, c.Start), c.Length) { Offset = Math.Max(0f, c.Offset), Loop = c.Loop, MixIn = Math.Max(0f, c.MixIn ?? 0f) });
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

		// Keys for a drawing's colour layers go with the copied layers
		var copyOf = layerSources.ToDictionary(p => p.Value.ToString("N"), p => p.Key.ToString("N"));
		foreach (var timeline in dto.Animations.SelectMany(a => a.Timelines))
			if (timeline.Type == "layerAdjust" && copyOf.TryGetValue(timeline.Target, out var copied))
				timeline.Target = copied;

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
				Hidden = b.Hidden ? true : null,
				Locked = b.Locked ? true : null,
				EditorColor = b.EditorColor is { } color ? Rgb.From(color) : null,
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
				Opacity = s.SetupOpacity >= 1f ? null : s.SetupOpacity,
				Hidden = s.Hidden ? true : null,
				Locked = s.Locked ? true : null,
				Mask = s.IsMask ? new MaskDto { Until = s.MaskUntil?.Name, Shows = s.MaskShows ? true : null } : null
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
					Opacity = l.Opacity,
					Adjust = l.Adjust is { } adjust ? AdjustToDto(adjust) : null
				}).ToList()
			});
		}

		foreach (var skin in skeleton.AllSkins()) dto.Skins.Add(SkinToDto(skin));
		if (skeleton.ShownSkins.Count > 0)
			dto.ShownSkins = skeleton.Skins.Where(skeleton.ShownSkins.Contains).Select(s => s.Name).ToList();

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

		if (skeleton.Mixes.Pairs.Count > 0 || MathF.Abs(skeleton.Mixes.Default - 0.2f) > 1e-6f)
			dto.Mixes = new MixDto
			{
				Default = skeleton.Mixes.Default,
				Pairs = skeleton.Mixes.Pairs.Count > 0 ? skeleton.Mixes.Pairs.Select(p => new MixPairDto { From = p.From, To = p.To, Duration = p.Duration }).ToList() : null
			};

		if (skeleton.CopyConstraints.Count > 0)
			dto.CopyConstraints = skeleton.CopyConstraints.Select(c => new CopyDto
			{
				Name = c.Name, Bone = c.Bone.Name, Source = c.Source.Name, Local = c.Local,
				Rotate = c.RotateMix, Move = c.MoveMix, Scale = c.ScaleMix,
				RotationOffset = c.RotationOffset, PositionOffset = Vec.From(c.PositionOffset), ScaleOffset = Vec.From(c.ScaleOffset),
				Mix = c.SetupMix
			}).ToList();
		if (skeleton.JiggleConstraints.Count > 0)
			dto.JiggleConstraints = skeleton.JiggleConstraints.Select(c => new JiggleDto
			{
				Name = c.Name, Bone = c.Bone.Name, Stiffness = c.Stiffness, Damping = c.Damping, Gravity = c.Gravity, Mix = c.SetupMix
			}).ToList();

		if (skeleton.ColorLayers.Count > 0)
		{
			var parts = skeleton.Slots.ToHashSet();
			dto.ColorLayers = skeleton.ColorLayers.Select(l => new ColorLayerDto
			{
				Name = l.Name,
				Visible = l.Visible,
				Adjust = AdjustToDto(l.Adjust),
				LeaveOut = l.LeftOut.Count == 0 ? null : l.LeftOut.Where(parts.Contains).Select(s => s.Name).ToList()
			}).ToList();
		}

		if (skeleton.Hitboxes.Count > 0)
			dto.Hitboxes = skeleton.Hitboxes.Select(h => new HitboxDto
			{
				Name = h.Name, Bone = h.Bone.Name, Shape = h.Shape == HitboxShape.Circle ? "circle" : "box",
				Offset = Vec.From(h.Offset), Rotation = h.Rotation, Size = Vec.From(h.Size), Kind = h.Kind, Active = h.SetupActive
			}).ToList();
		if (skeleton.Points.Count > 0)
			dto.Points = skeleton.Points.Select(p => new PointDto
			{
				Name = p.Name, Bone = p.Bone.Name, Offset = Vec.From(p.Offset), Rotation = p.Rotation
			}).ToList();

		if (skeleton.Poses.Count > 0)
			dto.Poses = skeleton.Poses.Select(p => new PoseDto
			{
				Name = p.Name,
				Bones = p.Bones.Select(b => new PoseBoneDto
				{
					Name = b.Key, Position = Vec.From(b.Value.Position), Rotation = b.Value.Rotation, Scale = Vec.From(b.Value.Scale),
					Turn = b.Value.Turn == 0 ? null : b.Value.Turn, Tilt = b.Value.Tilt == 0 ? null : b.Value.Tilt
				}).ToList(),
				Parts = p.Parts.Count == 0 ? null : p.Parts.Select(s => new PosePartDto
				{
					Name = s.Key, Offset = Vec.From(s.Value.Offset), Rotation = s.Value.Rotation, Scale = Vec.From(s.Value.Scale)
				}).ToList()
			}).ToList();

		if (skeleton.Sets.Count > 0)
		{
			var bones = skeleton.AllBones().ToHashSet();
			var slots = skeleton.Slots.ToHashSet();
			dto.Sets = skeleton.Sets.Select(set => new SetDto
			{
				Name = set.Name,
				Bones = set.Bones.Where(bones.Contains).Select(b => b.Name).ToList(),
				Parts = set.Slots.Where(slots.Contains).Select(s => s.Name).ToList()
			}).ToList();
		}

		foreach (var anim in animations)
		{
			dto.Animations.Add(new AnimationDto
			{
				Name = anim.Name,
				Duration = anim.Duration,
				Loop = anim.Loop,
				Timelines = anim.Timelines.Select(TimelineToDto).ToList(),
				Sound = anim.Sound is { } sound ? new SoundTrackDto { Path = sound.Path, Offset = sound.Offset, Volume = sound.Volume } : null
			});
		}

	}

	private static float[] MatrixToDto(Matrix3x2 m) => new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
	private static Matrix3x2 MatrixFromDto(float[]? v) => v is { Length: 6 } ? new Matrix3x2(v[0], v[1], v[2], v[3], v[4], v[5]) : Matrix3x2.Identity;

	private static MeshDto MeshToDto(Mesh mesh) => new()
	{
		W = mesh.Width,
		H = mesh.Height,
		Points = mesh.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
		Triangles = mesh.Triangles.ToArray(),
		Weights = mesh.Weights?.Select(w => w.Select(s => new WeightDto { Bone = s.Bone.Name, W = s.Weight }).ToList()).ToList(),
		Bind = mesh.Weighted ? MatrixToDto(mesh.Bind) : null,
		BoneBind = mesh.Weighted ? mesh.BoneBind.ToDictionary(p => p.Key.Name, p => MatrixToDto(p.Value)) : null
	};

	// Bones a mesh named that aren't there: their shares are left out
	private static Mesh MeshFromDto(MeshDto dto, IReadOnlyDictionary<string, Bone> bones)
	{
		var points = Enumerable.Range(0, dto.Points.Length / 2).Select(i => new Vector2(dto.Points[2 * i], dto.Points[2 * i + 1])).ToArray();
		var mesh = new Mesh(dto.W, dto.H, points, dto.Triangles);
		if (dto.Weights is not { } weights || weights.Count != points.Length) return mesh;
		var shares = weights.Select(w => w.Where(s => bones.ContainsKey(s.Bone)).Select(s => new BoneWeight(bones[s.Bone], s.W)).ToArray()).ToArray();
		if (shares.Any(s => s.Length == 0)) return mesh;
		var boneBind = (dto.BoneBind ?? new()).Where(p => bones.ContainsKey(p.Key)).ToDictionary(p => bones[p.Key], p => MatrixFromDto(p.Value));
		return mesh.WithWeights(shares, MatrixFromDto(dto.Bind), boneBind);
	}

	private static SkinDto SkinToDto(Skin skin) => new()
	{
		Name = skin.Name,
		Category = skin.Category.Length > 0 ? skin.Category : null,
		Overlay = skin.Overlay,
		ColorLayers = skin.ColorLayers.Count > 0 ? skin.ColorLayers.Select(l => l.Name).ToList() : null,
		Entries = skin.Entries.Select(e => new SkinEntryDto
		{
			Slot = e.Slot,
			Name = e.Attachment.Name,
			Asset = e.Attachment.AssetId,
			Offset = Vec.From(e.Attachment.Offset),
			Rotation = e.Attachment.Rotation,
			Scale = Vec.From(e.Attachment.Scale),
			Pivot = Vec.From(e.Attachment.Pivot),
			Mesh = e.Attachment.Mesh is { } mesh ? MeshToDto(mesh) : null
		}).ToList()
	};

	private static TimelineDto TimelineToDto(Timeline timeline) => timeline switch
	{
		RotateTimeline r => new TimelineDto
		{
			Type = "rotate",
			Target = r.Target,
			Keys = r.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value }).ToList()
		},
		TranslateTimeline t => new TimelineDto
		{
			Type = "translate",
			Target = t.Target,
			Keys = t.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		ScaleTimeline sc => new TimelineDto
		{
			Type = "scale",
			Target = sc.Target,
			Keys = sc.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		IkMixTimeline ik => new TimelineDto
		{
			Type = "ikMix",
			Target = ik.Target,
			Keys = ik.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value }).ToList()
		},
		AttachmentTimeline at => new TimelineDto
		{
			Type = "attachment",
			Target = at.Target,
			Keys = at.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), Name = k.Value }).ToList()
		},
		DepthTimeline d => new TimelineDto
		{
			Type = "depth",
			Target = d.Target,
			Keys = d.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		SpaceTimeline sp => new TimelineDto
		{
			Type = "space",
			Target = sp.Target,
			Keys = sp.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), Name = k.Value }).ToList()
		},
		PartTranslateTimeline pt => new TimelineDto
		{
			Type = "partTranslate",
			Target = pt.Target,
			Keys = pt.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		PartRotateTimeline pr => new TimelineDto
		{
			Type = "partRotate",
			Target = pr.Target,
			Keys = pr.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value }).ToList()
		},
		PartScaleTimeline ps => new TimelineDto
		{
			Type = "partScale",
			Target = ps.Target,
			Keys = ps.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y }).ToList()
		},
		PartColorTimeline pc => new TimelineDto
		{
			Type = "partColor",
			Target = pc.Target,
			Keys = pc.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value.X, Y = k.Value.Y, Z = k.Value.Z }).ToList()
		},
		PartOpacityTimeline po => new TimelineDto
		{
			Type = "partOpacity",
			Target = po.Target,
			Keys = po.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), X = k.Value }).ToList()
		},
		EventTimeline ev => new TimelineDto
		{
			Type = "events",
			Target = ev.Target,
			Keys = ev.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), Name = k.Value.Name, Data = k.Value.Data,
				X = k.Value.Volume != 1f ? k.Value.Volume : null, Y = k.Value.Pitch != 1f ? k.Value.Pitch : null }).ToList()
		},
		DrawOrderTimeline order => new TimelineDto
		{
			Type = "drawOrder",
			Target = order.Target,
			Keys = order.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), Order = k.Value.ToList() }).ToList()
		},
		ColorLayerTimeline cl => new TimelineDto
		{
			Type = "colorLayer",
			Target = cl.Target,
			Keys = cl.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), V = k.Value.ToArray() }).ToList()
		},
		LayerAdjustTimeline la => new TimelineDto
		{
			Type = "layerAdjust",
			Target = la.Target,
			Caption = la.Caption.Length > 0 ? la.Caption : null,
			Keys = la.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), V = k.Value.ToArray() }).ToList()
		},
		DeformTimeline df => new TimelineDto
		{
			Type = "deform",
			Target = df.Target,
			Keys = df.Track.Keys.Select(k => new KeyDto
			{
				T = k.Time, Curve = k.Curve, B = HandlesToDto(k.Curve, k.Bezier), Name = k.Value.Drawing,
				V = k.Value.Offsets.SelectMany(o => new[] { o.X, o.Y }).ToArray()
			}).ToList()
		},
		HitboxTimeline hb => new TimelineDto
		{
			Type = "hitbox",
			Target = hb.Target,
			Keys = hb.Track.Keys.Select(k => new KeyDto { T = k.Time, Curve = CurveType.Stepped, X = k.Value ? 1f : 0f }).ToList()
		},
		_ => throw new NotSupportedException($"Saving '{timeline.GetType().Name}' is not supported yet.")
	};

	// A Bézier key's handles (none for the other curves)
	private static float[]? HandlesToDto(CurveType curve, Bezier bezier) => curve == CurveType.Bezier ? bezier.ToArray() : null;

	private static AdjustDto AdjustToDto(ColorAdjust adjust) => new()
	{
		Kind = adjust.Kind,
		Colorize = adjust.Colorize ? true : null,
		KeepLight = adjust.KeepLight ? null : false,
		Values = adjust.Setup.ToArray()
	};

	private static ColorAdjust AdjustFromDto(AdjustDto dto)
	{
		var values = AdjustValues.FromArray(dto.Values);
		return new ColorAdjust(dto.Kind)
		{
			Colorize = dto.Colorize ?? false,
			KeepLight = dto.KeepLight ?? true,
			Setup = values,
			Now = values
		};
	}

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
				Hidden = b.Hidden ?? false,
				Locked = b.Locked ?? false,
				EditorColor = b.EditorColor?.ToVector3(),
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
				SetupOpacity = s.Opacity ?? 1f,
				Hidden = s.Hidden ?? false,
				Locked = s.Locked ?? false
			});
		}
		foreach (var s in dto.Slots)   // masks name parts: once they're all there
			if (s.Mask is { } mask && skeleton.FindSlot(s.Name) is { } slot)
				MaskOps.MakeMask(skeleton, slot, mask.Until != null ? skeleton.FindSlot(mask.Until) ?? slot : null, mask.Shows ?? false);

		foreach (var a in dto.Assets)
		{
			var asset = new Asset { Id = a.Id, Name = a.Name, Width = a.Width, Height = a.Height };
			foreach (var l in a.Layers)
				asset.Layers.Add(new AssetLayer
				{
					Id = l.Id, Name = l.Name, Visible = l.Visible, Opacity = l.Opacity,
					Adjust = l.Adjust is { } adjust ? AdjustFromDto(adjust) : null
				});
			skeleton.Assets[asset.Id] = asset;
		}

		foreach (var skinDto in dto.Skins)
		{
			var skin = skinDto.Name == skeleton.DefaultSkin.Name ? skeleton.DefaultSkin
				: new Skin { Name = skinDto.Name, Category = skinDto.Category ?? "", Overlay = skinDto.Overlay };
			if (skin != skeleton.DefaultSkin) skeleton.Skins.Add(skin);
			foreach (var e in skinDto.Entries)
			{
				skin.Set(e.Slot, new Attachment
				{
					Name = e.Name,
					AssetId = e.Asset,
					Offset = e.Offset.ToVector2(),
					Rotation = e.Rotation,
					Scale = e.Scale.ToVector2(),
					Pivot = e.Pivot.ToVector2(),
					Mesh = e.Mesh is { } mesh ? MeshFromDto(mesh, bones) : null
				});
			}
			if (skin != skeleton.DefaultSkin && (dto.ShownSkins?.Contains(skinDto.Name) ?? skinDto.Name == dto.ActiveSkin))
				skeleton.ShownSkins.Add(skin);
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

		if (dto.Mixes is { } mixes)
		{
			skeleton.Mixes.Default = mixes.Default;
			foreach (var p in mixes.Pairs ?? new List<MixPairDto>()) skeleton.Mixes.Set(p.From, p.To, p.Duration);
		}

		foreach (var c in dto.CopyConstraints ?? new List<CopyDto>())
			if (bones.TryGetValue(c.Bone, out var bone) && bones.TryGetValue(c.Source, out var source))
				skeleton.CopyConstraints.Add(new CopyConstraint
				{
					Name = c.Name, Bone = bone, Source = source, Local = c.Local,
					RotateMix = c.Rotate, MoveMix = c.Move, ScaleMix = c.Scale,
					RotationOffset = c.RotationOffset, PositionOffset = c.PositionOffset.ToVector2(), ScaleOffset = c.ScaleOffset.ToVector2(),
					SetupMix = c.Mix, Mix = c.Mix
				});
		foreach (var j in dto.JiggleConstraints ?? new List<JiggleDto>())
			if (bones.TryGetValue(j.Bone, out var bone))
				skeleton.JiggleConstraints.Add(new JiggleConstraint
				{
					Name = j.Name, Bone = bone, Stiffness = j.Stiffness, Damping = j.Damping, Gravity = j.Gravity, SetupMix = j.Mix, Mix = j.Mix
				});

		foreach (var c in dto.ColorLayers ?? new List<ColorLayerDto>())
		{
			var layer = new ColorLayer { Name = c.Name, Visible = c.Visible, Adjust = AdjustFromDto(c.Adjust ?? new AdjustDto()) };
			foreach (var name in c.LeaveOut ?? new List<string>())
				if (skeleton.FindSlot(name) is { } slot) layer.LeftOut.Add(slot);
			skeleton.ColorLayers.Add(layer);
		}
		foreach (var skinDto in dto.Skins)
			if (skinDto.ColorLayers != null && skeleton.FindSkin(skinDto.Name) is { } skin)
				foreach (var name in skinDto.ColorLayers)
					if (skeleton.FindColorLayer(name) is { } layer && skeleton.SkinOf(layer) == null) skin.ColorLayers.Add(layer);

		foreach (var h in dto.Hitboxes ?? new List<HitboxDto>())
			if (bones.TryGetValue(h.Bone, out var bone))
				skeleton.Hitboxes.Add(new Hitbox(h.Name, bone)
				{
					Shape = h.Shape == "circle" ? HitboxShape.Circle : HitboxShape.Box,
					Offset = h.Offset.ToVector2(), Rotation = h.Rotation, Size = h.Size.ToVector2(), Kind = h.Kind,
					SetupActive = h.Active, Active = h.Active
				});
		foreach (var p in dto.Points ?? new List<PointDto>())
			if (bones.TryGetValue(p.Bone, out var bone))
				skeleton.Points.Add(new RigPoint(p.Name, bone) { Offset = p.Offset.ToVector2(), Rotation = p.Rotation });

		foreach (var p in dto.Poses ?? new List<PoseDto>())
		{
			var pose = new SavedPose { Name = p.Name };
			foreach (var b in p.Bones)
				pose.Bones[b.Name] = (b.Position.ToVector2(), b.Rotation, b.Scale.ToVector2(), b.Turn ?? 0f, b.Tilt ?? 0f);
			foreach (var s in p.Parts ?? new List<PosePartDto>())
				pose.Parts[s.Name] = (s.Offset.ToVector2(), s.Rotation, s.Scale.ToVector2());
			skeleton.Poses.Add(pose);
		}

		// Selection sets: members that aren't there (any more) are left out
		foreach (var s in dto.Sets ?? new List<SetDto>())
		{
			var set = new SelectionSet { Name = s.Name };
			foreach (var name in s.Bones ?? new List<string>())
				if (bones.TryGetValue(name, out var bone)) set.Bones.Add(bone);
			foreach (var name in s.Parts ?? new List<string>())
				if (skeleton.FindSlot(name) is { } slot) set.Slots.Add(slot);
			skeleton.Sets.Add(set);
		}

		var animations = new List<Animation>();
		foreach (var a in dto.Animations)
		{
			var anim = new Animation
			{
				Name = a.Name, Duration = a.Duration, Loop = a.Loop,
				Sound = a.Sound is { Path.Length: > 0 } sound ? new SoundTrack(sound.Path, sound.Offset, sound.Volume) : null
			};
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
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 0f, k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "translate":
				{
					var tl = new TranslateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "scale":
				{
					var tl = new ScaleTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 1f, k.Y ?? 1f), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "ikMix":
				{
					var tl = new IkMixTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 1f, k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "deform":
				{
					var tl = new DeformTimeline(t.Target);
					foreach (var k in t.Keys)
					{
						var v = k.V ?? Array.Empty<float>();
						var offsets = Enumerable.Range(0, v.Length / 2).Select(i => new Vector2(v[2 * i], v[2 * i + 1])).ToArray();
						tl.Track.Add(k.T, new DeformKey(k.Name ?? "", offsets), k.Curve, Bezier.FromArray(k.B));
					}
					return tl;
				}
			case "hitbox":
				{
					var tl = new HitboxTimeline(t.Target);
					foreach (var k in t.Keys) tl.Add(k.T, (k.X ?? 1f) > 0.5f);
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
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve, Bezier.FromArray(k.B));
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
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 0f, k.Y ?? 0f), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "partRotate":
				{
					var tl = new PartRotateTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 0f, k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "partScale":
				{
					var tl = new PartScaleTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector2(k.X ?? 1f, k.Y ?? 1f), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "partColor":
				{
					var tl = new PartColorTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, new Vector3(k.X ?? 1f, k.Y ?? 1f, k.Z ?? 1f), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "partOpacity":
				{
					var tl = new PartOpacityTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, k.X ?? 1f, k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "events":
				{
					var tl = new EventTimeline();
					foreach (var k in t.Keys) tl.Add(k.T, new AnimationEvent(k.Name ?? "event", k.Data, k.X ?? 1f, k.Y ?? 1f));
					return tl;
				}
			case "colorLayer":
				{
					var tl = new ColorLayerTimeline(t.Target);
					foreach (var k in t.Keys) tl.Track.Add(k.T, AdjustValues.FromArray(k.V), k.Curve, Bezier.FromArray(k.B));
					return tl;
				}
			case "layerAdjust":
				{
					var tl = new LayerAdjustTimeline(t.Target) { Caption = t.Caption ?? "" };
					foreach (var k in t.Keys) tl.Track.Add(k.T, AdjustValues.FromArray(k.V), k.Curve, Bezier.FromArray(k.B));
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
		var copy = new Animation { Name = newName, Duration = source.Duration, Loop = source.Loop, Sound = source.Sound };
		copy.Timelines.AddRange(source.Timelines.Select(TimelineToDto).Select(TimelineFromDto));
		return copy;
	}
}