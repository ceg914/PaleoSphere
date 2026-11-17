// 지형 표면.
// 생물군계 색(셀당 한 텍셀)을 노이즈로 흐트러뜨려 격자 경계가 보이지 않게 하고,
// 식생량 텍스처로 맨땅과 식생 덮인 땅을 섞어 '먹힌 자리'가 화면에 드러나게 한다.
// 경사가 급하면 바위·흙 색, 물가는 젖은 진흙 색으로 바꾼다. 무늬는 모두 월드 좌표 노이즈라 이음매가 없다.
shader_type spatial;

uniform sampler2D lush_tex : source_color, filter_linear, repeat_disable;
uniform sampler2D bare_tex : source_color, filter_linear, repeat_disable;
uniform sampler2D veg_tex : filter_linear, repeat_disable;
uniform float water_level = 0.0;
uniform float cell_count = 256.0;
uniform float world_size = 512.0;
uniform vec3 rock_color : source_color = vec3(0.42, 0.38, 0.33);
uniform vec3 mud_color : source_color = vec3(0.28, 0.24, 0.18);
uniform vec3 highlight_center = vec3(0.0);
uniform float highlight_radius = 0.0;
uniform vec4 highlight_color : source_color = vec4(1.0, 0.9, 0.3, 1.0);

varying vec3 world_pos;
varying vec3 world_normal;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash12(i);
	float b = hash12(i + vec2(1.0, 0.0));
	float c = hash12(i + vec2(0.0, 1.0));
	float d = hash12(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p) {
	float sum = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 4; i++) {
		sum += amp * value_noise(p);
		p = p * 2.03 + vec2(17.1, 9.2);
		amp *= 0.5;
	}
	return sum;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec2 xz = world_pos.xz;

	// 생물군계 경계를 2~6 m 정도 흔들어 셀 모양이 드러나지 않게 한다.
	vec2 warp = vec2(fbm(xz * 0.09), fbm(xz * 0.09 + vec2(31.7, 5.3))) - 0.5;
	vec2 uv = (xz + warp * 7.0) / world_size;
	vec3 lush = texture(lush_tex, uv).rgb;
	vec3 bare = texture(bare_tex, uv).rgb;

	// 식생량. 노이즈로 문턱을 흔들어 고르게 옅어지지 않고 군데군데 맨땅이 드러나게 한다.
	float veg = texture(veg_tex, UV).r;
	float patchy = fbm(xz * 0.35);
	float cover = smoothstep(0.05, 0.75, veg + (patchy - 0.5) * 0.35);
	vec3 col = mix(bare, lush, cover);

	// 큰 얼룩과 잔무늬(1~20 m).
	float macro = fbm(xz * 0.045);
	float detail = fbm(xz * 1.3);
	col *= 0.86 + 0.24 * macro;
	col *= 0.92 + 0.16 * detail;
	// 식생이 많은 곳은 초록 기운을 조금 더한다.
	col = mix(col, col * vec3(0.92, 1.05, 0.90), cover * (detail - 0.3));

	// 급경사: 바위·흙.
	float slope = 1.0 - clamp(world_normal.y, 0.0, 1.0);
	float rock = smoothstep(0.22, 0.42, slope + (detail - 0.5) * 0.12);
	col = mix(col, rock_color * (0.8 + 0.35 * detail), rock);

	// 물가 띠: 젖은 진흙.
	float above = world_pos.y - water_level;
	float shore = 1.0 - smoothstep(0.05, 0.7 + 0.4 * macro, above);
	col = mix(col, mud_color * (0.85 + 0.3 * detail), shore * 0.85);
	// 물속 바닥은 어둡게.
	float depth = clamp(-above / 1.6, 0.0, 1.0);
	col = mix(col, vec3(0.10, 0.15, 0.15), depth * 0.8);

	// 잔무늬로 법선을 살짝 흔들어 평평한 면에도 결이 보이게 한다.
	float e = 0.35;
	float h0 = fbm(xz * 0.9);
	float hx = fbm((xz + vec2(e, 0.0)) * 0.9);
	float hz = fbm((xz + vec2(0.0, e)) * 0.9);
	vec3 bump = normalize(vec3((h0 - hx) * 1.6, 1.0, (h0 - hz) * 1.6));
	vec3 n = normalize(world_normal + (bump - vec3(0.0, 1.0, 0.0)) * (0.6 + rock));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	// 배치·교란 도구의 범위 표시.
	if (highlight_radius > 0.0) {
		float d = distance(world_pos.xz, highlight_center.xz);
		float ring = smoothstep(highlight_radius, highlight_radius - 0.6, d) * smoothstep(highlight_radius - 2.2, highlight_radius - 1.0, d);
		float fill = smoothstep(highlight_radius, highlight_radius - 0.6, d) * 0.12;
		col = mix(col, highlight_color.rgb, clamp(ring * 0.9 + fill, 0.0, 1.0));
	}

	ALBEDO = col;
	ROUGHNESS = mix(0.95, 0.55, shore);
	SPECULAR = mix(0.12, 0.35, shore);
}
