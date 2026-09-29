// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// C-01 boundary pins for the level -> substat-count rule (feeds SubstatShort/Excess).

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class EchoRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(19, 3)]
    [InlineData(20, 4)]
    [InlineData(24, 4)]
    [InlineData(25, 5)]
    [InlineData(30, 5)]
    public void ExpectedSubstatCount_FollowsTheLevelRule(int level, int expected)
        => Assert.Equal(expected, EchoRules.ExpectedSubstatCount(level));
}
