using System;
using System.Collections.Generic;
using Godot;

namespace PaleoSphere.Presentation;

/// <summary>
/// 코드로 만드는 식물 메시. 외부 파일 없이 형태만 흉내 낸 양식화 표현이다(게임적 단순화).
/// 백악기 후기 북아메리카 서부 식생(속씨식물 활엽수, 침엽수, 양치류, 소철·베넷티테스류, 속새)을 염두에 두었고,
/// 그 시기에 넓게 퍼지지 않았던 풀밭은 만들지 않는다.
/// 모든 메시의 원점은 밑동(땅)이고 위가 +Y다. 색은 정점 색이며 개체마다 MultiMesh 색을 곱한다.
/// </summary>
public static class PlantMeshes
{
    private sealed class Builder
    {
        private readonly List<Vector3> _v = new();
        private readonly List<Vector3> _n = new();
        private readonly List<Color> _c = new();
        private readonly List<int> _i = new();

        public int Vertex(Vector3 p, Vector3 n, Color c)
        {
            _v.Add(p);
            _n.Add(n.LengthSquared() > 1e-10f ? n.Normalized() : Vector3.Up);
            _c.Add(c);
            return _v.Count - 1;
        }

        public void Tri(int a, int b, int c)
        {
            _i.Add(a);
            _i.Add(b);
            _i.Add(c);
        }

        /// <summary>
        /// 휘어진 잎(띠). 밑동에서 dir 방향으로 뻗으며 끝으로 갈수록 가늘어지고 아래로 처진다.
        /// 잎자루 양옆에 작은잎이 달린 것처럼 폭을 톱니 모양으로 바꾼다.
        /// </summary>
        public void Frond(Vector3 basePos, Vector3 dir, float length, float width, float lift, float droop, Color baseColor, Color tipColor, int segments, bool serrated)
        {
            dir = new Vector3(dir.X, 0f, dir.Z).Normalized();
            Vector3 side = new Vector3(-dir.Z, 0f, dir.X);
            int prevL = -1, prevR = -1;
            for (int s = 0; s <= segments; s++)
            {
                float t = s / (float)segments;
                // 위로 솟았다가 끝에서 처지는 곡선.
                float y = lift * t - droop * t * t;
                Vector3 center = basePos + dir * (length * t) + new Vector3(0f, y, 0f);
                float w = width * MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.15f + 0.08f));
                if (serrated && s % 2 == 1) w *= 1.45f;
                Vector3 up = new Vector3(0f, 1f, 0f) - dir * (lift - 2f * droop * t) / Math.Max(length, 0.01f);
                Color col = baseColor.Lerp(tipColor, t);
                int l = Vertex(center - side * w, up, col);
                int r = Vertex(center + side * w, up, col);
                if (prevL >= 0)
                {
                    Tri(prevL, l, prevR);
                    Tri(prevR, l, r);
                }
                prevL = l;
                prevR = r;
            }
        }

        /// <summary>from에서 to로 가늘어지는 원뿔대. 끝 반지름이 0이면 뾰족하다.</summary>
        public void Cone(Vector3 from, Vector3 to, float r0, float r1, Color c0, Color c1, int sides, float jitter = 0f, int seed = 0)
        {
            Vector3 axis = (to - from).Normalized();
            Vector3 side = axis.Cross(Mathf.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 up = side.Cross(axis);
            int first = _v.Count;
            for (int ring = 0; ring < 2; ring++)
            {
                Vector3 center = ring == 0 ? from : to;
                float r = Math.Max(ring == 0 ? r0 : r1, 0.001f);
                for (int k = 0; k < sides; k++)
                {
                    float a = k * Mathf.Tau / sides;
                    float j = 1f + jitter * (Hash(seed * 31 + k * 7 + ring) - 0.5f) * 2f;
                    Vector3 n = side * MathF.Cos(a) + up * MathF.Sin(a);
                    Vertex(center + n * r * j, n + axis * ((r0 - r1) / Math.Max((to - from).Length(), 0.01f)), ring == 0 ? c0 : c1);
                }
            }
            for (int k = 0; k < sides; k++)
            {
                int a = first + k, b = first + (k + 1) % sides;
                Tri(a, b, a + sides);
                Tri(b, b + sides, a + sides);
            }
        }

        /// <summary>울퉁불퉁한 덩어리(활엽수 수관, 덤불). 반지름에 노이즈를 섞는다.</summary>
        public void Blob(Vector3 center, Vector3 radii, Color top, Color bottom, int seed, float roughness = 0.22f, int segments = 9, int rings = 6)
        {
            int first = _v.Count;
            for (int r = 0; r <= rings; r++)
            {
                float phi = r * MathF.PI / rings;
                for (int s = 0; s < segments; s++)
                {
                    float theta = s * Mathf.Tau / segments;
                    var unit = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    float bumpy = (r == 0 || r == rings) ? 1f : 1f + roughness * (Hash(seed * 101 + r * 13 + s) - 0.5f) * 2f;
                    Vector3 p = center + unit * radii * bumpy;
                    float shade = 0.5f + 0.5f * unit.Y;
                    Vertex(p, unit / radii, bottom.Lerp(top, shade));
                }
            }
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = first + r * segments + s, b = first + r * segments + (s + 1) % segments;
                    Tri(a, b, a + segments);
                    Tri(b, b + segments, a + segments);
                }
            }
        }

        public ArrayMesh Build()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _i.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }

    /// <summary>[0, 1) 결정론적 해시.</summary>
    public static float Hash(int n)
    {
        uint x = (uint)n * 0x9E3779B1u;
        x ^= x >> 15;
        x *= 0x85EBCA77u;
        x ^= x >> 13;
        x *= 0xC2B2AE3Du;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }

    private static Color C(float r, float g, float b) => new(r, g, b);

    /// <summary>양치류 한 포기(지름 약 1.6 m, 높이 약 0.5 m).</summary>
    public static ArrayMesh Fern()
    {
        var b = new Builder();
        int fronds = 8;
        for (int i = 0; i < fronds; i++)
        {
            float a = i * Mathf.Tau / fronds + Hash(i) * 0.5f;
            var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            float len = 0.65f + 0.25f * Hash(i + 50);
            b.Frond(new Vector3(0f, 0.02f, 0f), dir, len, 0.10f, 0.55f + 0.2f * Hash(i + 90), 0.45f, C(0.11f, 0.24f, 0.08f), C(0.27f, 0.43f, 0.14f), 6, serrated: true);
        }
        return b.Build();
    }

    /// <summary>속새 무더기(높이 약 1 m). 마디 무늬가 있는 가는 줄기.</summary>
    public static ArrayMesh Horsetail()
    {
        var b = new Builder();
        for (int i = 0; i < 11; i++)
        {
            float a = Hash(i + 3) * Mathf.Tau;
            float r = 0.25f * MathF.Sqrt(Hash(i + 17));
            var basePos = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            float h = 0.6f + 0.6f * Hash(i + 29);
            var lean = new Vector3(MathF.Cos(a) * 0.15f, 0f, MathF.Sin(a) * 0.15f);
            int nodes = 4;
            for (int k = 0; k < nodes; k++)
            {
                float t0 = k / (float)nodes, t1 = (k + 1) / (float)nodes;
                Color band = k % 2 == 0 ? C(0.30f, 0.42f, 0.20f) : C(0.36f, 0.50f, 0.24f);
                b.Cone(basePos + lean * t0 + Vector3.Up * h * t0, basePos + lean * t1 + Vector3.Up * h * t1,
                    0.022f * (1f - t0 * 0.6f), 0.022f * (1f - t1 * 0.6f), band.Darkened(0.15f), band, 4);
            }
        }
        return b.Build();
    }

    /// <summary>키 작은 덤불(지름 약 1.4 m).</summary>
    public static ArrayMesh Shrub()
    {
        var b = new Builder();
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Tau / 4f + Hash(i + 7);
            float r = i == 0 ? 0f : 0.35f;
            var c = new Vector3(MathF.Cos(a) * r, 0.30f + 0.08f * Hash(i + 11), MathF.Sin(a) * r);
            float s = i == 0 ? 0.55f : 0.40f;
            b.Blob(c, new Vector3(s, s * 0.7f, s), C(0.46f, 0.56f, 0.26f), C(0.25f, 0.32f, 0.15f), i + 1, 0.35f, 8, 5);
        }
        return b.Build();
    }

    /// <summary>침엽수(높이 약 12 m). 층층이 처진 가지 다발을 원뿔로 나타낸다.</summary>
    public static ArrayMesh Conifer()
    {
        var b = new Builder();
        Color bark = C(0.30f, 0.21f, 0.14f);
        b.Cone(Vector3.Zero, new Vector3(0f, 12f, 0f), 0.38f, 0.06f, bark.Darkened(0.2f), bark, 7);
        int tiers = 6;
        for (int t = 0; t < tiers; t++)
        {
            float f = t / (float)(tiers - 1);
            float y0 = 3.0f + f * 7.0f;
            float radius = 2.6f * (1f - f * 0.75f);
            float height = 2.4f - f * 0.8f;
            Color dark = C(0.05f, 0.14f, 0.07f), light = C(0.12f, 0.27f, 0.12f);
            b.Cone(new Vector3(0f, y0, 0f), new Vector3(0f, y0 + height, 0f), radius, 0.02f, dark, light.Lerp(dark, 0.3f * f), 9, 0.25f, t + 1);
        }
        return b.Build();
    }

    /// <summary>활엽수(속씨식물, 높이 약 9 m).</summary>
    public static ArrayMesh Broadleaf()
    {
        var b = new Builder();
        Color bark = C(0.33f, 0.25f, 0.18f);
        b.Cone(Vector3.Zero, new Vector3(0.2f, 5.5f, 0f), 0.36f, 0.20f, bark.Darkened(0.2f), bark, 7);
        b.Cone(new Vector3(0.15f, 4.2f, 0f), new Vector3(1.6f, 6.4f, 0.4f), 0.16f, 0.08f, bark, bark, 5);
        b.Cone(new Vector3(0.15f, 4.6f, 0f), new Vector3(-1.3f, 6.6f, -0.6f), 0.15f, 0.08f, bark, bark, 5);
        Color top = C(0.26f, 0.42f, 0.15f), bottom = C(0.09f, 0.18f, 0.07f);
        b.Blob(new Vector3(0.1f, 7.3f, 0f), new Vector3(3.0f, 2.1f, 2.8f), top, bottom, 3, 0.22f, 11, 7);
        b.Blob(new Vector3(1.7f, 6.6f, 0.6f), new Vector3(1.8f, 1.5f, 1.7f), top, bottom, 5, 0.25f, 9, 6);
        b.Blob(new Vector3(-1.5f, 6.8f, -0.7f), new Vector3(1.9f, 1.5f, 1.8f), top, bottom, 7, 0.25f, 9, 6);
        b.Blob(new Vector3(0.3f, 8.6f, -0.4f), new Vector3(1.7f, 1.3f, 1.6f), top, bottom, 9, 0.25f, 9, 6);
        return b.Build();
    }

    /// <summary>소철류(높이 약 2.5 m). 비늘 무늬 줄기 위에 깃 모양 잎이 둥글게 퍼진다.</summary>
    public static ArrayMesh Cycad()
    {
        var b = new Builder();
        int rings = 5;
        for (int k = 0; k < rings; k++)
        {
            float y0 = k * 0.32f, y1 = (k + 1) * 0.32f;
            Color c = k % 2 == 0 ? C(0.36f, 0.30f, 0.20f) : C(0.28f, 0.23f, 0.15f);
            b.Cone(new Vector3(0f, y0, 0f), new Vector3(0f, y1, 0f), 0.42f - k * 0.03f, 0.40f - k * 0.03f, c, c, 8);
        }
        int fronds = 14;
        for (int i = 0; i < fronds; i++)
        {
            float a = i * Mathf.Tau / fronds + Hash(i + 200) * 0.3f;
            var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            b.Frond(new Vector3(0f, rings * 0.32f, 0f), dir, 1.7f + 0.3f * Hash(i + 220), 0.16f, 1.2f, 1.0f, C(0.15f, 0.30f, 0.12f), C(0.30f, 0.48f, 0.18f), 7, serrated: true);
        }
        return b.Build();
    }
}
