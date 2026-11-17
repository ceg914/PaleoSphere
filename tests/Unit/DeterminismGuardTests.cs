using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PaleoSphere.Tests.Unit;

/// <summary>
/// 시뮬레이션 코어에 플랫폼 수학 함수가 다시 들어오지 않았는지 검사한다.
/// MathF.Sin·Exp·Pow 등은 운영체제·CPU에 따라 마지막 비트가 달라 같은 시드의 결과를 갈라 놓는다.
/// 코어는 DetMath(사칙연산 기반)와 IEEE가 결과를 정해 둔 Sqrt·Floor·Abs만 쓴다.
/// </summary>
public class DeterminismGuardTests
{
    private static readonly string[] CoreFolders = { "Core", "World", "Ecology", "Creatures", "AI", "Simulation" };

    [Fact]
    public void CoreCode_DoesNotUsePlatformTranscendentals()
    {
        var banned = new Regex(@"\bMathF?\.(Sin|Cos|Tan|Atan2?|Asin|Acos|Exp|Pow|Log|Log10|Log2|Cbrt|Sinh|Cosh|Tanh)\s*\(");
        var offenders = CoreFolders
            .SelectMany(f => Directory.GetFiles(Path.Combine(TestData.RepoRoot, "src", f), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.EndsWith("DetMath.cs"))
            .SelectMany(path => File.ReadAllLines(path).Select((line, i) => (path, line, i)))
            .Where(x => banned.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.path)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "코어에 플랫폼 수학 함수가 있습니다(DetMath를 쓰세요):\n" + string.Join("\n", offenders));
    }
}
