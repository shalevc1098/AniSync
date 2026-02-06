using FluentAssertions;
using Shoko.AniSync.Helpers;
using Xunit;

namespace Shoko.Tests;

public class StringFormatterTests
{
    [Theory]
    [InlineData("Attack on Titan", "AttackonTitan")]
    [InlineData("Dr. Stone", "DrStone")]
    [InlineData("One-Punch Man", "OnePunchMan")]
    [InlineData("K-ON!", "KON")]
    [InlineData("Steins;Gate", "SteinsGate")]
    [InlineData("Re:Zero", "ReZero")]
    [InlineData("Fate/stay night", "Fatestaynight")]
    [InlineData("Code Geass: Lelouch of the Rebellion", "CodeGeassLelouchoftheRebellion")]
    [InlineData("   Spaces   ", "Spaces")]
    [InlineData("", "")]
    public void RemoveSpecialCharacters_Should_Remove_All_Special_Characters(string input, string expected)
    {
        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("ABC123", "ABC123")]
    [InlineData("NoSpecialChars", "NoSpecialChars")]
    [InlineData("AlreadyClean", "AlreadyClean")]
    public void RemoveSpecialCharacters_Should_Keep_Alphanumeric_Characters(string input, string expected)
    {
        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("!!!@@@###$$$", "")]
    [InlineData("***---___", "")]
    [InlineData("()[]{}", "")]
    [InlineData(".,;:?!", "")]
    public void RemoveSpecialCharacters_Should_Return_Empty_For_Only_Special_Characters(string input, string expected)
    {
        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void RemoveSpecialCharacters_Should_Handle_Null()
    {
        // Arrange
        string? input = null;

        // Act & Assert
        Assert.Throws<NullReferenceException>(() => StringFormatter.RemoveSpecialCharacters(input!));
    }

    [Fact]
    public void RemoveSpecialCharacters_Should_Remove_Unicode_Characters()
    {
        // Arrange
        var input = "進撃の巨人";
        var expected = ""; // Unicode characters are removed

        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void RemoveSpecialCharacters_Should_Handle_Mixed_Content()
    {
        // Arrange
        var input = "Hunter x Hunter (2011)";
        var expected = "HunterxHunter2011";

        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("lower case", "lowercase")]
    [InlineData("UPPER CASE", "UPPERCASE")]
    [InlineData("MiXeD CaSe", "MiXeDCaSe")]
    public void RemoveSpecialCharacters_Should_Preserve_Case(string input, string expected)
    {
        // Act
        var result = StringFormatter.RemoveSpecialCharacters(input);

        // Assert
        result.Should().Be(expected);
    }
}