// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// D-03 pins: the level strip must parse as a whole - partial matches are not evidence.

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class ParseLevelTests
{
    [Theory]
    [InlineData("25", 25)]
    [InlineData("+25", 25)]
    [InlineData(" 0 ", 0)]
    [InlineData("Lv.15", 15)]
    [InlineData("Lv 15", 15)]
    [InlineData("Level 3", 3)]
    [InlineData("+0", 0)]
    public void WholeStringMatches_AreAccepted(string ocr, int expected)
        => Assert.Equal(expected, EchoRecognizer.ParseLevel(ocr));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2 5")]
    [InlineData("25+7")]          // the old regex took "25" out of this
    [InlineData("Lv.15 (+3)")]    // ...and "15" out of this
    [InlineData("0-99")]
    public void PartialOrGarbage_IsRejected(string ocr)
        => Assert.Null(EchoRecognizer.ParseLevel(ocr));

    [Theory]
    [InlineData("99")]
    [InlineData("26")]
    public void OutOfRange_IsRejected(string ocr)
        => Assert.Null(EchoRecognizer.ParseLevel(ocr));
}
