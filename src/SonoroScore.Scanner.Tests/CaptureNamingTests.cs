// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Pins the capture filename convention everything downstream depends on:
// echo_pXX_rYY_cZZ_idxNNN.png (zero-padded, 1-based, grows past 999).

using AlephalSonata.Automation;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class CaptureNamingTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, "echo_p01_r01_c01_idx001.png")]
    [InlineData(11, 4, 2, 1485, "echo_p12_r05_c03_idx1485.png")]
    [InlineData(99, 3, 0, 1495, "echo_p100_r04_c01_idx1495.png")]
    [InlineData(0, 1, 1, 999, "echo_p01_r02_c02_idx999.png")]
    public void FileName_MatchesTheSessionConvention(int page, int row, int col, int index, string expected)
        => Assert.Equal(expected, CaptureNaming.FileName(page, row, col, index));
}
