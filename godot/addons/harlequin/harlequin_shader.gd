@tool
class_name HarlequinShader
extends RefCounted
## The shader a Harlequin part is drawn with when it needs one: a blend mode other than Normal,
## or the object's colour layers. One shader for each blend mode, made once.

const MAX_LAYERS := 8

const _CODE := """// A part of a Harlequin rig: its picture times its tint and opacity, then the object's colour
// layers that change it, first to last (Animator.Viewer/Rendering/ColorFilters.cs).
shader_type canvas_item;
render_mode BLEND_MODE;

const int MAX_LAYERS = 8;
uniform int layer_count = 0;
// Each layer: kind (0 hue / saturation, 1 brightness / contrast, 2 tint, 3 colour balance) and
// strength, its hue / saturation matrix (three rows, already blended by strength), lightness,
// brightness, contrast, keep light (tint), the tint colour and amount, and the colour balance
uniform vec2 kind_strength[MAX_LAYERS];
uniform vec3 row0[MAX_LAYERS];
uniform vec3 row1[MAX_LAYERS];
uniform vec3 row2[MAX_LAYERS];
uniform vec4 values[MAX_LAYERS];
uniform vec4 tint[MAX_LAYERS];
uniform vec3 shadows[MAX_LAYERS];
uniform vec3 midtones[MAX_LAYERS];
uniform vec3 highlights[MAX_LAYERS];

float lum(vec3 c) { return dot(c, vec3(0.213, 0.715, 0.072)); }

vec3 layer_curve(int i, vec3 x) {
	int k = int(kind_strength[i].x + 0.5);
	float s = kind_strength[i].y;
	vec3 f;
	if (k == 0) {
		float l = values[i].x;
		if (abs(l) <= 1e-4) return x;
		f = l > 0.0 ? x + (1.0 - x) * l : x * (1.0 + l);
	} else if (k == 1) {
		f = (x - 0.5) * (1.0 + values[i].z) + 0.5 + values[i].y * 0.5;
	} else if (k == 2) {
		if (values[i].w > 0.5)
			f = x * (1.0 + (tint[i].rgb / max(0.05, lum(tint[i].rgb)) - 1.0) * tint[i].a);
		else
			f = x + (tint[i].rgb - x) * tint[i].a;
	} else if (k == 3) {
		f = x + 0.5 * (shadows[i] * (1.0 - x) * (1.0 - x) + midtones[i] * 4.0 * x * (1.0 - x) + highlights[i] * x * x);
	} else {
		return x;
	}
	return clamp(x + (clamp(f, 0.0, 1.0) - x) * s, 0.0, 1.0);
}

void fragment() {
	vec4 c = texture(TEXTURE, UV) * COLOR;
	vec3 rgb = c.rgb;
	for (int i = 0; i < MAX_LAYERS; i++) {
		if (i >= layer_count) break;
		if (kind_strength[i].y <= 0.0) continue;
		if (int(kind_strength[i].x + 0.5) == 0)
			rgb = clamp(vec3(dot(row0[i], rgb), dot(row1[i], rgb), dot(row2[i], rgb)), 0.0, 1.0);
		rgb = layer_curve(i, rgb);
	}
	OUTPUT
}
"""

static var _made := {}


static func for_blend(blend: int) -> Shader:
	if _made.has(blend):
		return _made[blend]
	var mode := "blend_mix"
	var output := "COLOR = vec4(rgb, c.a);"
	match blend:
		HarlequinRigData.Blend.ADD:
			mode = "blend_add"
		HarlequinRigData.Blend.MULTIPLY:
			mode = "blend_mul"
			output = "COLOR = vec4(mix(vec3(1.0), rgb, c.a), 1.0);"
		HarlequinRigData.Blend.SCREEN:
			# (Godot has no screen blend: lighter by the part's colour, much as screen is)
			mode = "blend_premul_alpha"
			output = "vec3 p = rgb * c.a; COLOR = vec4(p, max(max(p.r, p.g), p.b));"
	var shader := Shader.new()
	shader.code = _CODE.replace("BLEND_MODE", mode).replace("OUTPUT", output)
	_made[blend] = shader
	return shader


## The uniforms for these colour layers (each { kind, colorize, keep_light, values }), first to last
static func set_layers(material: ShaderMaterial, layers: Array) -> void:
	var n := mini(layers.size(), MAX_LAYERS)
	var kind_strength := PackedVector2Array()
	var r0 := PackedVector3Array()
	var r1 := PackedVector3Array()
	var r2 := PackedVector3Array()
	var values := []
	var tint := []
	var shadows := PackedVector3Array()
	var midtones := PackedVector3Array()
	var highlights := PackedVector3Array()
	for i in MAX_LAYERS:
		if i >= n:
			kind_strength.append(Vector2.ZERO)
			r0.append(Vector3(1, 0, 0)); r1.append(Vector3(0, 1, 0)); r2.append(Vector3(0, 0, 1))
			values.append(Vector4.ZERO); tint.append(Vector4.ZERO)
			shadows.append(Vector3.ZERO); midtones.append(Vector3.ZERO); highlights.append(Vector3.ZERO)
			continue
		var layer: Dictionary = layers[i]
		var v: PackedFloat32Array = layer.values
		var s := clampf(v[0], 0.0, 1.0)   # (strength 1, times its mix)
		kind_strength.append(Vector2(layer.kind, s))
		var m := hue_saturation_matrix(layer, s)
		r0.append(Vector3(m[0], m[1], m[2])); r1.append(Vector3(m[3], m[4], m[5])); r2.append(Vector3(m[6], m[7], m[8]))
		values.append(Vector4(v[3], v[4], v[5], 1.0 if layer.keep_light else 0.0))
		tint.append(Vector4(v[6], v[7], v[8], v[9]))
		shadows.append(Vector3(v[10], v[11], v[12]))
		midtones.append(Vector3(v[13], v[14], v[15]))
		highlights.append(Vector3(v[16], v[17], v[18]))
	material.set_shader_parameter("layer_count", n)
	material.set_shader_parameter("kind_strength", kind_strength)
	material.set_shader_parameter("row0", r0)
	material.set_shader_parameter("row1", r1)
	material.set_shader_parameter("row2", r2)
	material.set_shader_parameter("values", values)
	material.set_shader_parameter("tint", tint)
	material.set_shader_parameter("shadows", shadows)
	material.set_shader_parameter("midtones", midtones)
	material.set_shader_parameter("highlights", highlights)


const LUM_R := 0.213
const LUM_G := 0.715
const LUM_B := 0.072


## Hue / saturation's 3×3 matrix, row by row, blended from no change by s (ColorFilters.Matrix)
static func hue_saturation_matrix(layer: Dictionary, s: float) -> PackedFloat32Array:
	var id := PackedFloat32Array([1, 0, 0, 0, 1, 0, 0, 0, 1])
	if layer.kind != 0:
		return id
	var v: PackedFloat32Array = layer.values
	var hue_deg := v[1]
	var saturation := v[2]
	var m: PackedFloat32Array
	if layer.colorize:
		var hue := hue_color(hue_deg)
		var lum := maxf(0.05, hue.x * LUM_R + hue.y * LUM_G + hue.z * LUM_B)
		var rich := clampf((saturation + 1.0) / 2.0, 0.0, 1.0)
		m = PackedFloat32Array()
		m.resize(9)
		for row in 3:
			var f := 1.0 + (hue[row] / lum - 1.0) * rich
			m[row * 3] = LUM_R * f
			m[row * 3 + 1] = LUM_G * f
			m[row * 3 + 2] = LUM_B * f
	elif absf(hue_deg) < 1e-4 and absf(saturation) < 1e-4:
		m = id
	else:
		m = _multiply(_saturation(1.0 + saturation), _hue(deg_to_rad(hue_deg)))
	for i in 9:
		m[i] = id[i] + (m[i] - id[i]) * s
	return m


static func hue_color(degrees: float) -> Vector3:
	var h := fmod(fmod(degrees, 360.0) + 360.0, 360.0) / 60.0
	var x := 1.0 - absf(fmod(h, 2.0) - 1.0)
	match int(h):
		0: return Vector3(1, x, 0)
		1: return Vector3(x, 1, 0)
		2: return Vector3(0, 1, x)
		3: return Vector3(0, x, 1)
		4: return Vector3(x, 0, 1)
	return Vector3(1, 0, x)


static func _hue(r: float) -> PackedFloat32Array:
	var c := cos(r)
	var n := sin(r)
	return PackedFloat32Array([
		LUM_R + c * (1 - LUM_R) - n * LUM_R, LUM_G - c * LUM_G - n * LUM_G, LUM_B - c * LUM_B + n * (1 - LUM_B),
		LUM_R - c * LUM_R + n * 0.143, LUM_G + c * (1 - LUM_G) + n * 0.140, LUM_B - c * LUM_B - n * 0.283,
		LUM_R - c * LUM_R - n * (1 - LUM_R), LUM_G - c * LUM_G + n * LUM_G, LUM_B + c * (1 - LUM_B) + n * LUM_B])


static func _saturation(a: float) -> PackedFloat32Array:
	return PackedFloat32Array([
		LUM_R + (1 - LUM_R) * a, LUM_G - LUM_G * a, LUM_B - LUM_B * a,
		LUM_R - LUM_R * a, LUM_G + (1 - LUM_G) * a, LUM_B - LUM_B * a,
		LUM_R - LUM_R * a, LUM_G - LUM_G * a, LUM_B + (1 - LUM_B) * a])


static func _multiply(a: PackedFloat32Array, b: PackedFloat32Array) -> PackedFloat32Array:
	var m := PackedFloat32Array()
	m.resize(9)
	for r in 3:
		for c in 3:
			m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c]
	return m
