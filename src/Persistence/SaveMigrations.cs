using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PaleoSphere.Core;

namespace PaleoSphere.Persistence;

/// <summary>
/// 구버전 세이브를 현재 스키마로 올리는 변환 사슬. 버전 n → n+1 변환을 차례로 적용한다.
/// 형식을 바꿀 때 여기에 추가하고 tests/SaveCompatibility에 이전 버전 샘플을 남긴다.
///
/// 1 → 2 (시뮬레이션 0.2.0, 포식): 사체 목록이 생겼고 행동 점수가 6개에서 7개(사냥)로 늘었다.
///   토양 영양 배열과 개체의 사냥 상태는 비어 있으면 기본값으로 읽으므로 손대지 않는다.
/// </summary>
public static class SaveMigrations
{
    private static readonly Dictionary<int, Func<JsonObject, JsonObject>> Steps = new()
    {
        [1] = V1ToV2,
    };

    private static JsonObject V1ToV2(JsonObject state)
    {
        if (state["carcasses"] is not JsonArray) state["carcasses"] = new JsonArray();
        if (state["creatures"] is JsonArray creatures)
        {
            foreach (JsonNode? node in creatures)
            {
                if (node is not JsonObject creature || creature["scores"] is not JsonArray scores) continue;
                while (scores.Count < 7) scores.Add(0.0);
            }
        }
        return state;
    }

    /// <summary>fromVersion → fromVersion + 1 변환을 등록한다.</summary>
    public static void Register(int fromVersion, Func<JsonObject, JsonObject> step) => Steps[fromVersion] = step;

    internal static void Unregister(int fromVersion) => Steps.Remove(fromVersion);

    public static bool CanMigrate(int fromVersion)
    {
        for (int v = fromVersion; v < SimVersion.SaveSchema; v++)
            if (!Steps.ContainsKey(v)) return false;
        return true;
    }

    /// <summary>state 객체를 현재 스키마까지 올린다. 적용한 단계를 log에 적는다.</summary>
    public static JsonObject Upgrade(JsonObject state, int fromVersion, List<string> log)
    {
        JsonObject current = state;
        for (int v = fromVersion; v < SimVersion.SaveSchema; v++)
        {
            current = Steps[v](current);
            log.Add($"세이브 스키마 {v} → {v + 1} 변환을 적용했습니다.");
        }
        return current;
    }
}
