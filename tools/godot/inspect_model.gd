# 사용법: godot --headless --path . --script res://tools/godot/inspect_model.gd -- res://경로/모델.fbx
# 가져온 모델의 노드 구조, 메시 크기, 뼈대, 애니메이션 목록을 출력한다.
extends SceneTree

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		print("모델 경로를 주세요")
		quit(1)
		return
	for path in args:
		var packed: PackedScene = load(path)
		if packed == null:
			print("불러오기 실패: ", path)
			continue
		var root := packed.instantiate()
		print("=== ", path)
		_walk(root, 0)
		var aabb := _aabb(root, Transform3D.IDENTITY)
		print("전체 AABB: ", aabb, "  크기: ", aabb.size)
		for player in root.find_children("*", "AnimationPlayer", true, false):
			for anim_name in player.get_animation_list():
				var anim: Animation = player.get_animation(anim_name)
				print("  애니메이션 '", anim_name, "' 길이 ", snappedf(anim.length, 0.01), "초 트랙 ", anim.get_track_count(), " 반복 ", anim.loop_mode)
		root.free()
	quit(0)

func _walk(node: Node, depth: int) -> void:
	var line := "  ".repeat(depth) + node.name + " (" + node.get_class() + ")"
	if node is Node3D:
		line += " pos=" + str(node.position) + " rot=" + str(node.rotation_degrees) + " scale=" + str(node.scale)
	if node is MeshInstance3D and node.mesh:
		line += " 면 " + str(node.mesh.get_surface_count()) + "개, aabb=" + str(node.mesh.get_aabb())
		for i in node.mesh.get_surface_count():
			var mat = node.mesh.surface_get_material(i)
			if mat is StandardMaterial3D:
				line += " [재질 " + str(i) + " " + str(mat.resource_name) + " 색 " + str(mat.albedo_color) + " 텍스처 " + str(mat.albedo_texture != null) + "]"
	if node is Skeleton3D:
		line += " 뼈 " + str(node.get_bone_count()) + "개"
		var names := []
		for i in node.get_bone_count():
			names.append(node.get_bone_name(i))
		line += " " + str(names)
	print(line)
	if depth < 6:
		for child in node.get_children():
			_walk(child, depth + 1)

func _aabb(node: Node, xf: Transform3D) -> AABB:
	var result := AABB()
	var first := true
	var t := xf
	if node is Node3D:
		t = xf * node.transform
	if node is MeshInstance3D and node.mesh:
		var box: AABB = t * node.mesh.get_aabb()
		result = box
		first = false
	for child in node.get_children():
		var sub := _aabb(child, t)
		if sub.size != Vector3.ZERO:
			result = sub if first else result.merge(sub)
			first = false
	return result
