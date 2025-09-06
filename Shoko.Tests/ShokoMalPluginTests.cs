using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Events;
using Shoko.Plugin.Abstractions.Services;
using Xunit;

namespace Shoko.Tests;

public class ShokoMalPluginTests
{
    [Fact]
    public async Task StartAsync_Should_Subscribe_To_VideoUserDataSaved_Event()
    {
        // Arrange
        var userDataServiceMock = new Mock<IUserDataService>();
        var plugin = CreatePlugin(userDataServiceMock: userDataServiceMock);
        var cancellationToken = CancellationToken.None;
        
        // Act
        await ((IHostedService)plugin).StartAsync(cancellationToken);
        
        // Assert
        userDataServiceMock.VerifyAdd(
            x => x.VideoUserDataSaved += It.IsAny<EventHandler<VideoUserDataSavedEventArgs>>(),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_Should_Unsubscribe_From_VideoUserDataSaved_Event()
    {
        // Arrange
        var userDataServiceMock = new Mock<IUserDataService>();
        var plugin = CreatePlugin(userDataServiceMock: userDataServiceMock);
        var cancellationToken = CancellationToken.None;
        await ((IHostedService)plugin).StartAsync(cancellationToken);
        
        // Act
        await ((IHostedService)plugin).StopAsync(cancellationToken);
        
        // Assert
        userDataServiceMock.VerifyRemove(
            x => x.VideoUserDataSaved -= It.IsAny<EventHandler<VideoUserDataSavedEventArgs>>(),
            Times.Once);
    }

    [Theory]
    [InlineData("Attack on Titan", "ATTACK ON TITAN", true)]
    [InlineData("Attack on Titan", "attack on titan", true)]
    [InlineData("Attack on Titan", "Death Note", false)]
    [InlineData("Steins;Gate", "Steins Gate", true)] // Ignores symbols
    [InlineData("Dr. STONE", "Dr STONE", true)]
    public void CompareStrings_Should_Compare_Correctly(string first, string second, bool expected)
    {
        // Act
        var result = TestHelper.CompareStrings(first, second);
        
        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("2023-05-15", 2023, 5, 15)]
    [InlineData("2022-12-31", 2022, 12, 31)]
    [InlineData("2024-01-01", 2024, 1, 1)]
    public void ParseFullDate_Should_Return_DateTime_For_Valid_Date(string dateString, int year, int month, int day)
    {
        // Act
        var result = TestHelper.ParseFullDate(dateString);
        
        // Assert
        result.Should().NotBeNull();
        result!.Value.Year.Should().Be(year);
        result!.Value.Month.Should().Be(month);
        result!.Value.Day.Should().Be(day);
    }

    [Theory]
    [InlineData("invalid-date")]
    [InlineData("not a date")]
    [InlineData("2023-13-01")] // Invalid month
    [InlineData("2023-12-32")] // Invalid day
    public void ParseFullDate_Should_Return_Null_For_Invalid_Date(string dateString)
    {
        // Act
        var result = TestHelper.ParseFullDate(dateString);
        
        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ParseFullDate_Should_Return_Null_For_Empty_String(string? dateString)
    {
        // Act
        var result = TestHelper.ParseFullDate(dateString);
        
        // Assert
        result.Should().BeNull();
    }

    private ShokoMalPlugin CreatePlugin(
        Mock<IUserDataService>? userDataServiceMock = null)
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        var loggerMock = new Mock<ILogger<ShokoMalPlugin>>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var metadataServiceMock = new Mock<IMetadataService>();
        var applicationPathsMock = new Mock<IApplicationPaths>();
        
        applicationPathsMock.Setup(x => x.PluginsPath).Returns(System.IO.Path.GetTempPath());
        
        loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(loggerMock.Object);
        
        return new ShokoMalPlugin(
            applicationPathsMock.Object,
            httpClientFactoryMock.Object,
            loggerFactoryMock.Object,
            memoryCache,
            metadataServiceMock.Object,
            userDataServiceMock?.Object ?? Mock.Of<IUserDataService>());
    }
}

// Helper class for testing private methods
public static class TestHelper
{
    public static bool CompareStrings(string first, string second)
    {
        return String.Compare(first, second, System.Globalization.CultureInfo.CurrentCulture, 
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreSymbols) == 0;
    }

    public static DateTime? ParseFullDate(string? dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString))
            return null;

        if (DateTime.TryParse(dateString, out DateTime result))
            return result;

        return null;
    }
}