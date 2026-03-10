using System;
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

        // Act & Assert - null input now returns empty string instead of throwing
        var result = StringFormatter.RemoveSpecialCharacters(input!);
        Assert.Equal(string.Empty, result);
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

    // ========================================================================
    // ContainsExtended empty string guard
    // After sanitization, if either string is empty, ContainsExtended must
    // return false to avoid string.Contains("") -> true.
    // ========================================================================

    private static bool ContainsExtended(string first, string second)
    {
        var sanitizedFirst = StringFormatter.RemoveSpecialCharacters(first);
        var sanitizedSecond = StringFormatter.RemoveSpecialCharacters(second);
        if (string.IsNullOrEmpty(sanitizedFirst) || string.IsNullOrEmpty(sanitizedSecond))
            return false;
        return sanitizedFirst.Contains(sanitizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainsExtended_Returns_False_When_First_Sanitizes_To_Empty()
    {
        ContainsExtended("!?!", "Naruto").Should().BeFalse(
            "first string sanitizes to empty, should not match anything");
    }

    [Fact]
    public void ContainsExtended_Returns_False_When_Second_Sanitizes_To_Empty()
    {
        ContainsExtended("Naruto", "!?!").Should().BeFalse(
            "second string sanitizes to empty, should not spuriously match");
    }

    [Fact]
    public void ContainsExtended_Returns_False_When_Both_Sanitize_To_Empty()
    {
        ContainsExtended("!!!", "???").Should().BeFalse(
            "both sanitize to empty, should not match");
    }

    [Fact]
    public void ContainsExtended_Returns_True_For_Valid_Substring()
    {
        ContainsExtended("Fate/stay night", "Fate stay").Should().BeTrue(
            "'Fatestaynight' contains 'Fatestay' after sanitization");
    }

    // ========================================================================
    // RemoveSpaces behavior
    // Should keep alphanumerics + spaces, strip special characters.
    // ========================================================================

    [Fact]
    public void RemoveSpaces_Keeps_Alphanumerics_And_Spaces()
    {
        StringFormatter.RemoveSpaces("Attack on Titan (2013)")
            .Should().Be("Attack on Titan 2013");
    }

    [Fact]
    public void RemoveSpaces_Strips_Special_Characters()
    {
        StringFormatter.RemoveSpaces("Fate/stay night: UBW")
            .Should().Be("Fatestay night UBW");
    }

    [Fact]
    public void RemoveSpaces_All_Special_Returns_Empty()
    {
        StringFormatter.RemoveSpaces("!@#$%")
            .Should().BeEmpty();
    }

    [Fact]
    public void RemoveSpaces_Already_Clean_Unchanged()
    {
        StringFormatter.RemoveSpaces("Naruto")
            .Should().Be("Naruto");
    }

    [Fact]
    public void RemoveSpaces_Preserves_Spaces_Between_Words()
    {
        StringFormatter.RemoveSpaces("One Piece")
            .Should().Be("One Piece");
    }

    // ========================================================================
    // StringFormatter null safety
    // Both methods should return empty string for null/empty input.
    // ========================================================================

    [Fact]
    public void RemoveSpecialCharacters_Null_Returns_Empty()
    {
        StringFormatter.RemoveSpecialCharacters(null!).Should().BeEmpty();
    }

    [Fact]
    public void RemoveSpecialCharacters_Empty_Returns_Empty()
    {
        StringFormatter.RemoveSpecialCharacters("").Should().BeEmpty();
    }

    [Fact]
    public void RemoveSpaces_Null_Returns_Empty()
    {
        StringFormatter.RemoveSpaces(null!).Should().BeEmpty();
    }

    [Fact]
    public void RemoveSpaces_Empty_Returns_Empty()
    {
        StringFormatter.RemoveSpaces("").Should().BeEmpty();
    }
}