using System.Collections.Generic;
using FluentAssertions;
using AniSync.Helpers;
using Xunit;

namespace AniSync.Tests;

public class UrlBuilderTests
{
    [Fact]
    public void Build_Should_Return_Base_Url_When_No_Parameters()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Be("https://api.example.com/endpoint");
    }

    [Fact]
    public void Build_Should_Append_Single_Query_Parameter()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key", "value"));

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Be("https://api.example.com/endpoint?key=value");
    }

    [Fact]
    public void Build_Should_Append_Multiple_Query_Parameters()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key1", "value1"));
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key2", "value2"));
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key3", "value3"));

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Be("https://api.example.com/endpoint?key1=value1&key2=value2&key3=value3");
    }

    [Fact]
    public void Build_Should_Handle_Special_Characters()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("search", "Attack on Titan"));
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("symbols", "!@#$%"));

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Contain("Attack%20on%20Titan");
        result.Should().Contain("%21%40%23%24%25");
    }

    [Fact]
    public void Build_Should_Handle_Empty_Parameters_List()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Be("https://api.example.com/endpoint");
    }

    [Fact]
    public void Build_Should_Handle_Integer_Values_As_Strings()
    {
        // Arrange
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("limit", "10"));
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("offset", "20"));

        // Act
        var result = urlBuilder.Build();

        // Assert
        result.Should().Be("https://api.example.com/endpoint?limit=10&offset=20");
    }

    // ========================================================================
    // Duplicate parameters (UrlBuilder uses a List, so duplicates are allowed)
    // ========================================================================

    [Fact]
    public void Build_Should_Handle_Duplicate_Key_Value_Pairs()
    {
        var urlBuilder = new UrlBuilder { Base = "https://api.example.com/endpoint" };
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key", "val"));
        urlBuilder.Parameters.Add(new KeyValuePair<string, string>("key", "val"));

        var result = urlBuilder.Build();

        result.Should().Be("https://api.example.com/endpoint?key=val&key=val");
    }
}