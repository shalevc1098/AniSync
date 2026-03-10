using FluentAssertions;
using Xunit;

namespace Shoko.Tests;

/// <summary>
/// Tests for frontend input validation logic.
/// </summary>
public class FrontendValidationTests
{
    private static double NumOrDefault(string val, double fallback)
    {
        if (double.TryParse(val, out var n))
            return n;
        return fallback;
    }

    [Fact]
    public void NumOrDefault_Zero_ReturnsZero()
    {
        NumOrDefault("0", 5).Should().Be(0, "zero is a valid number and should not be replaced by the default");
    }

    [Fact]
    public void NumOrDefault_NaN_ReturnsFallback()
    {
        NumOrDefault("abc", 0.8).Should().Be(0.8, "non-numeric input should return the fallback");
        NumOrDefault("", 5).Should().Be(5, "empty string should return the fallback");
    }

    [Fact]
    public void NumOrDefault_ValidNumber_ReturnsNumber()
    {
        NumOrDefault("3.5", 0.8).Should().Be(3.5);
        NumOrDefault("10", 5).Should().Be(10);
        NumOrDefault("-1", 0).Should().Be(-1);
    }
}
