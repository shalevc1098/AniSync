using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using Xunit;

namespace Shoko.Tests;

public class ApiCallHelpersTests
{
    [Fact]
    public async Task SearchAnime_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var originalInstance = AniSync.Plugin.Instance;
        try
        {
            var plugin = new AniSync.Plugin();
            typeof(AniSync.Plugin).GetProperty("Instance")?.SetValue(null, plugin);
            plugin.GetType().GetProperty("Config")?.SetValue(plugin, new Config("test-config.json") { UpdateNsfw = true });

            var apiCallHelpers = new ApiCallHelpers();
            
            // Act
            var result = await apiCallHelpers.SearchAnime("test");

            // Assert
            result.Should().BeNull();
        }
        finally
        {
            typeof(AniSync.Plugin).GetProperty("Instance")?.SetValue(null, originalInstance);
        }
    }

    [Fact]
    public async Task GetAnime_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();
        
        // Act
        var result = await apiCallHelpers.GetAnime(123);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAnime_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();
        
        // Act
        var result = await apiCallHelpers.UpdateAnime(123, 5, Status.Watching);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUser_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();
        
        // Act
        var result = await apiCallHelpers.GetUser();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAnimeList_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();
        
        // Act
        var result = await apiCallHelpers.GetAnimeList(Status.Watching);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Constructor_Should_Accept_MalApiCalls()
    {
        // Arrange
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        var memoryCacheMock = new Mock<IMemoryCache>();
        var delayerMock = new Mock<IAsyncDelayer>();

        var malApiCalls = new MalApiCalls(
            httpClientFactoryMock.Object,
            loggerFactoryMock.Object,
            memoryCacheMock.Object,
            delayerMock.Object);

        // Act
        var apiCallHelpers = new ApiCallHelpers(malApiCalls);

        // Assert
        apiCallHelpers.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_Should_Accept_Null_Parameters()
    {
        // Act
        var apiCallHelpers = new ApiCallHelpers();

        // Assert
        apiCallHelpers.Should().NotBeNull();
    }
}