extends SceneTree
# The Harlequin runtime's tests, headless:
#   godot --headless --path godot -s tests/run_tests.gd            (checks against tests/expected.json)
#   godot --headless --path godot -s tests/run_tests.gd -- --update (writes tests/expected.json again)
#
# expected.json holds poses of the sample rig worked out here; Animator.Tests (GodotRuntimeTests)
# works the same steps out with Harlequin's own C# code and checks it gets the same: so the two
# agree on every part of the format the sample uses.

const RIG := "res://demo/sample.hqrig.json"
const EXPECTED := "res://tests/expected.json"

var failures := 0
var checks := 0


func _initialize() -> void:
	var update := "--update" in OS.get_cmdline_user_args()
	var data := HarlequinRigData.open(RIG)
	check(data.error == "", "the sample reads: " + data.error)
	if data.error != "":
		_finish()
		return
	_test_data(data)
	_test_curves()
	_test_poses(data, update)
	_test_node()


func _finish() -> void:
	print("%d checks, %d failed" % [checks, failures])
	quit(1 if failures > 0 else 0)


func check(ok: bool, what: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAILED: " + what)


# ---------------------------------------------------------------- reading

func _test_data(data: HarlequinRigData) -> void:
	check(data.bones.size() == 17, "17 bones")
	check(data.bones[data.bone_index["head"]].parent == data.bone_index["body"], "head on body")
	check(data.slots[data.slot_index["eye"]].is_mask, "the eye is a mask")
	check(data.slots[data.slot_index["eye"]].mask_until == data.slot_index["pupil"], "it clips up to the pupil")
	check(data.skins[0].name == "default" and data.skins.size() == 4, "default skin first")
	check(data.shown_skins == ["cone"], "cone on at the start")
	check(data.animation_names == ["walk", "idle"], "two animations")
	check(data.pages.size() == 1 and data.pages[0] != null, "the atlas page loads")
	check(absf(data.mix_pairs["walk\nidle"] - 0.1) < 1e-6 and absf(data.mix_default - 0.25) < 1e-6, "mixes")
	check(data.slots[data.slot_index["hat"]].blend == HarlequinRigData.Blend.ADD, "blend modes")
	var forearm: Dictionary = data.skins[0].entries["forearm"]["forearm"]
	check(forearm.mesh != null and forearm.mesh.weights.size() == 8, "the weighted mesh")


func _test_curves() -> void:
	check(absf(HarlequinPose.bezier([0.42, 0, 0.58, 1], 0.5) - 0.5) < 1e-4, "ease in-out is half way at half time")
	check(HarlequinPose.bezier([0.34, 1.56, 0.64, 1], 0.6) > 1.0, "overshoot goes past")
	var keys := [{"t": 0.0, "curve": HarlequinRigData.Interp.SMOOTH, "b": null, "v": 0.0}, {"t": 1.0, "curve": 0, "b": null, "v": 10.0}]
	check(absf(HarlequinPose.sample(keys, 0.25) - 1.5625) < 1e-4, "smooth: 3t² − 2t³")
	check(HarlequinPose.sample(keys, -1.0) == 0.0 and HarlequinPose.sample(keys, 2.0) == 10.0, "before and after the keys")
	check(absf(HarlequinPose.shortest_angle(3 * PI) - PI) < 1e-5 or absf(HarlequinPose.shortest_angle(3 * PI) + PI) < 1e-5, "shortest angle")


# ---------------------------------------------------------------- poses, against expected.json

func _steps() -> Array:
	var times := []
	for i in 14:
		times.append(i * 0.1)
	return [
		{"label": "rest", "skins": ["cone"], "do": "rest"},
		{"label": "walk", "skins": [], "do": "play", "anim": "walk", "times": times},
		{"label": "walk, bucket and glow", "skins": ["bucket", "glow", "cone"], "do": "play", "anim": "walk", "times": [0.35, 0.65]},
		{"label": "walk into idle", "skins": [], "do": "crossfade", "from": "walk", "from_time": 0.6, "to": "idle", "to_time": 0.05, "progress": 0.4},
		{"label": "walk over idle", "skins": [], "do": "layer", "from": "idle", "from_time": 0.5, "to": "walk", "to_time": 0.3, "progress": 0.5},
		{"label": "idle after its end", "skins": [], "do": "play", "anim": "idle", "times": [2.0]},
	]


func _test_poses(data: HarlequinRigData, update: bool) -> void:
	var pose := HarlequinPose.new(data)
	var samples := []
	for step in _steps():
		pose.shown_skins.clear()
		for n in step.skins:
			pose.show_skin(n)
		pose.reset_physics()
		match step.do:
			"rest":
				pose.setup_pose()
				pose.update()
				samples.append(_record(data, pose, step.label))
			"play":
				for t in step.times:
					pose.setup_pose()
					pose.apply(data.animations[step.anim], t)
					pose.update()
					pose.step_physics(t)
					samples.append(_record(data, pose, "%s @ %.2f" % [step.label, t]))
			"crossfade", "layer":
				pose.setup_pose()
				if step.do == "crossfade":
					pose.crossfade(data.animations[step.from], step.from_time, data.animations[step.to], step.to_time, step.progress)
				else:
					pose.apply(data.animations[step.from], step.from_time)
					pose.layer(data.animations[step.to], step.to_time, step.progress)
				pose.update()
				pose.step_physics(0.0)
				samples.append(_record(data, pose, step.label))
	# What the skins do
	pose.shown_skins.clear()
	pose.show_skin("bucket")
	pose.show_skin("cone")
	check(pose.shown_skins.keys() == ["cone"], "one hat skin at a time")
	pose.show_skin("glow")
	check(pose.shown_skins.size() == 2, "an overlay stacks")

	var file := {"rig": "demo/sample.hqrig.json", "steps": _steps(), "samples": samples}
	if update or not FileAccess.file_exists(EXPECTED):
		var out := FileAccess.open(EXPECTED, FileAccess.WRITE)
		out.store_string(JSON.stringify(file, " ", false))
		out.close()
		print("wrote " + EXPECTED)
		return
	var expected = JSON.parse_string(FileAccess.get_file_as_string(EXPECTED))
	check(expected is Dictionary and expected.samples.size() == samples.size(), "expected.json has every sample")
	if not (expected is Dictionary):
		return
	for i in mini(samples.size(), expected.samples.size()):
		_compare(samples[i], expected.samples[i], samples[i].label)


func _record(data: HarlequinRigData, pose: HarlequinPose, label: String) -> Dictionary:
	var bones := {}
	for i in data.bones.size():
		var o := pose.world[i].origin
		bones[data.bones[i].name] = [o.x, o.y, pose.world_rotation(i)]
	var parts := {}
	var drawings := {}
	var colors := {}
	for s in data.slots.size():
		var name: String = data.slots[s].name
		var c := pose.part_color[s]
		colors[name] = [c.x, c.y, c.z, pose.part_opacity[s]]
		var drawing = pose.drawing_of(s)
		if drawing == null or not data.regions.has(drawing.asset):
			drawings[name] = null
			parts[name] = null
			continue
		drawings[name] = drawing.name + " " + drawing.asset
		var region: Dictionary = data.regions[drawing.asset]
		var points := PackedVector2Array()
		var mesh = drawing.mesh
		if mesh != null and mesh.w == region.width and mesh.h == region.height and not mesh.triangles.is_empty():
			points = pose.posed_mesh(s, drawing, mesh)
		else:
			var m := pose.picture_to_object(s, drawing, region.width, region.height)
			points = PackedVector2Array([m * Vector2.ZERO, m * Vector2(region.width, 0), m * Vector2(region.width, region.height), m * Vector2(0, region.height)])
		var flat := []
		for p in points:
			flat.append(p.x)
			flat.append(p.y)
		parts[name] = flat
	var order := []
	for s in pose.draw_order:
		order.append(data.slots[s].name)
	var hitboxes := []
	for n in data.hitboxes.size():
		if pose.hitbox_active[n]:
			hitboxes.append(data.hitboxes[n].name)
	var layers := {}
	for n in data.color_layers.size():
		layers[data.color_layers[n].name] = Array(pose.color_values[n].slice(0, 6))
	var clipped := []
	for s in pose.clipped_by(data.slot_index["eye"]):
		clipped.append(data.slots[s].name)
	return {"label": label, "bones": bones, "parts": parts, "drawings": drawings, "colors": colors,
			"order": order, "hitboxes": hitboxes, "layers": layers, "clipped": clipped}


func _compare(got: Dictionary, want: Dictionary, label: String) -> void:
	for name in want.bones:
		var a: Array = got.bones[name]
		var b: Array = want.bones[name]
		check(absf(a[0] - b[0]) < 0.01 and absf(a[1] - b[1]) < 0.01 and absf(HarlequinPose.shortest_angle(a[2] - b[2])) < 1e-3,
			"%s: bone %s at %s, expected %s" % [label, name, a, b])
	for name in want.parts:
		var a = got.parts[name]
		var b = want.parts[name]
		var same: bool = (a == null) == (b == null)
		if same and a != null:
			same = a.size() == b.size()
			for k in mini(a.size(), b.size()):
				same = same and absf(a[k] - b[k]) < 0.01
		check(same, "%s: part %s at %s, expected %s" % [label, name, a, b])
	check(got.drawings == want.drawings, "%s: drawings %s, expected %s" % [label, got.drawings, want.drawings])
	check(got.order == want.order, "%s: draw order %s, expected %s" % [label, got.order, want.order])
	check(got.hitboxes == want.hitboxes, "%s: hitboxes %s, expected %s" % [label, got.hitboxes, want.hitboxes])
	check(got.clipped == want.clipped, "%s: clipped %s, expected %s" % [label, got.clipped, want.clipped])
	for name in want.colors:
		var a: Array = got.colors[name]
		var b: Array = want.colors[name]
		var same := true
		for k in 4:
			same = same and absf(a[k] - b[k]) < 1e-4
		check(same, "%s: %s's colour %s, expected %s" % [label, name, a, b])
	for name in want.layers:
		var a: Array = got.layers[name]
		var b: Array = want.layers[name]
		var same := true
		for k in b.size():
			same = same and absf(a[k] - b[k]) < 1e-4
		check(same, "%s: colour layer %s %s, expected %s" % [label, name, a, b])


# ---------------------------------------------------------------- the node

func _test_node() -> void:
	var rig := HarlequinRig.new()
	rig.rig_file = RIG
	root.add_child(rig)
	var heard := []
	rig.event.connect(func(n, d, v, p): heard.append([n, d, v, p]))
	var finished := []
	rig.animation_finished.connect(func(n, t): finished.append(n))
	check(rig.get_animation_names() == PackedStringArray(["walk", "idle"]), "the node lists the animations")
	rig.play("walk")
	for i in 40:
		rig.advance(1.0 / 30.0)
	check(heard.size() >= 3, "events fire (%s)" % [heard])
	check(heard.size() > 0 and heard[0][0] == "step" and heard[0][1] == "left", "the first event is the step at 0")
	var right := heard.filter(func(e): return e[1] == "right")
	check(right.size() == 1 and absf(right[0][2] - 0.8) < 1e-5 and absf(right[0][3] - 1.2) < 1e-5, "volume and pitch")
	var parts := rig.get_child(0, true)
	check(parts.get_child_count() > 0, "the parts are nodes")
	var eye := _find(parts, "eye")
	check(eye != null and eye.clip_children == CanvasItem.CLIP_CHILDREN_AND_DRAW, "the eye clips (and shows)")
	check(eye != null and eye.get_child_count() == 1 and eye.get_child(0).name == "pupil", "the pupil is inside the eye")
	var hat: Polygon2D = _find(parts, "hat")
	check(hat != null and hat.material is ShaderMaterial, "an Add part has the shader")
	var forearm: Polygon2D = _find(parts, "forearm")
	check(forearm != null and forearm.polygons.size() == 6 and forearm.polygon.size() == 8, "the mesh is triangles")
	check(rig.get_hitboxes(false).size() == 2, "hitboxes")
	var hand := rig.get_point_transform("hand")
	var forearm_bone := rig.get_bone_transform("forearm")
	check(hand.origin.distance_to(forearm_bone * Vector2(36, 0)) < 0.01, "the hand point is at the forearm's end")
	rig.show_skin("bucket")
	check(rig.get_shown_skins() == PackedStringArray(["bucket"]), "skins switch")
	rig.play("idle")
	check(rig.get_current_animation() == "idle", "idle plays")
	for i in 60:
		rig.advance(1.0 / 30.0)
	check(finished.has("idle"), "idle finishes")
	check(heard.any(func(e): return e[0] == "blink"), "idle's event fires")
	rig.set_track(1, "walk", 0.5)
	rig.advance(0.1)
	check(rig.get_current_animation(1) == "walk", "a second track")
	rig.stop()
	check(rig.get_current_animation() == "", "stopped")
	rig.queue_free()
	_finish.call_deferred()


func _find(node: Node, name: String) -> Node:
	for c in node.get_children():
		if c.name == name:
			return c
		var inner := _find(c, name)
		if inner != null:
			return inner
	return null
