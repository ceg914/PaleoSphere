// 식물(지피 식생·나무). 색은 정점 색 × 개체 색(MultiMesh)이다.
// 바람에 흔들리는 정도는 높이의 제곱에 비례하고, 위상은 개체 위치로 정해 서로 다르게 흔들린다.
shader_type spatial;
render_mode cull_disabled, diffuse_burley;

uniform float sway = 0.06;
uniform float sway_speed = 1.3;
uniform float translucency = 0.35;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float phase = origin.x * 0.37 + origin.z * 0.23;
	float h = max(VERTEX.y, 0.0);
	float bend = h * h * sway;
	VERTEX.x += sin(TIME * sway_speed + phase) * bend;
	VERTEX.z += cos(TIME * sway_speed * 0.8 + phase * 1.3) * bend * 0.6;
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 0.85;
	SPECULAR = 0.15;
	// 잎 뒷면에서도 어둡게 뭉개지지 않게 약간의 투과광을 준다.
	BACKLIGHT = COLOR.rgb * translucency;
}
