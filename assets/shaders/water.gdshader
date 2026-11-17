// 수면. 깊이에 따라 색이 짙어지고, 잔물결이 반사광을 흔들며, 물가에는 옅은 거품 띠가 생긴다.
// 깊이는 깊이 버퍼에서 읽으므로 지형 높이와 저절로 맞는다.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D depth_texture : hint_depth_texture, filter_linear_mipmap;
uniform vec3 shallow_color : source_color = vec3(0.28, 0.44, 0.40);
uniform vec3 deep_color : source_color = vec3(0.07, 0.18, 0.22);
uniform float depth_scale = 1.6;
uniform float foam_width = 0.35;

varying vec3 world_pos;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

float ripple(vec2 p, float t) {
	return value_noise(p * 0.55 + vec2(t * 0.20, t * 0.11)) * 0.6
		+ value_noise(p * 1.7 - vec2(t * 0.35, -t * 0.27)) * 0.4;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float t = TIME;
	vec2 p = world_pos.xz;

	// 잔물결 법선.
	float e = 0.2;
	float h = ripple(p, t);
	float hx = ripple(p + vec2(e, 0.0), t);
	float hz = ripple(p + vec2(0.0, e), t);
	vec3 n = normalize(vec3((h - hx) * 0.9, 1.0, (h - hz) * 0.9));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	// 수면 아래 지형까지의 거리(시선 방향 기준).
	float scene_depth = texture(depth_texture, SCREEN_UV).r;
	vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
	vec4 view = INV_PROJECTION_MATRIX * ndc;
	view.xyz /= view.w;
	float water_depth = clamp(VERTEX.z - view.z, 0.0, 50.0);

	float deep = 1.0 - exp(-water_depth / depth_scale);
	vec3 col = mix(shallow_color, deep_color, deep);

	// 물가 거품.
	float foam = 1.0 - smoothstep(0.0, foam_width, water_depth);
	foam *= 0.55 + 0.45 * value_noise(p * 2.5 + t * 0.4);
	col = mix(col, vec3(0.78, 0.82, 0.78), foam * 0.6);

	ALBEDO = col;
	ALPHA = clamp(0.55 + deep * 0.4 + foam * 0.3, 0.0, 0.95);
	ROUGHNESS = 0.08;
	METALLIC = 0.0;
	SPECULAR = 0.6;
}
