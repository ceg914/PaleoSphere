namespace PaleoSphere.Core;

/// <summary>버전 상수. 시뮬레이션 규칙이 바뀌어 같은 시드의 결과가 달라지면 Simulation 값을 올린다.</summary>
public static class SimVersion
{
    public const string Simulation = "0.3.0";

    /// <summary>세이브 파일 스키마 버전. 형식이 바뀌면 올리고 Persistence/SaveMigrations에 변환을 추가한다.</summary>
    public const int SaveSchema = 3;

    /// <summary>data/ JSON 정의 파일의 스키마 버전.</summary>
    public const int DataSchema = 1;
}
