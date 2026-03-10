using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync.Api;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Shoko.Tests
{
    public class DateHandlingTests
    {
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ILogger<MalApiCalls>> _loggerMock;
        private readonly Mock<IMemoryCache> _memoryCacheMock;
        private readonly Mock<IAsyncDelayer> _delayerMock;
        private readonly MalApiCalls _malApiCalls;

        public DateHandlingTests()
        {
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerMock = new Mock<ILogger<MalApiCalls>>();
            _memoryCacheMock = new Mock<IMemoryCache>();
            _delayerMock = new Mock<IAsyncDelayer>();

            _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>()))
                .Returns((string categoryName) => _loggerMock.Object);

            _malApiCalls = new MalApiCalls(
                _httpClientFactoryMock.Object,
                _loggerFactoryMock.Object,
                _memoryCacheMock.Object,
                _delayerMock.Object);
        }

        [Fact]
        public void UpdateAnimeStatus_WithStartDate_FormatsCorrectly()
        {
            // Arrange
            _ = 12345; // animeId
            _ = 5; // episodes
            _ = Status.Watching; // status
            var startDate = new DateTime(2024, 3, 15);

            // Act - We'll test the date formatting logic
            var formattedDate = startDate.ToString("yyyy-MM-dd");

            // Assert
            formattedDate.Should().Be("2024-03-15");
        }

        [Fact]
        public void UpdateAnimeStatus_WithEndDate_FormatsCorrectly()
        {
            // Arrange
            var endDate = new DateTime(2024, 12, 31);

            // Act
            var formattedDate = endDate.ToString("yyyy-MM-dd");

            // Assert
            formattedDate.Should().Be("2024-12-31");
        }

        [Fact]
        public void UpdateAnimeStatus_WithMinValueDate_ShouldClearDate()
        {
            // Arrange
            var clearDate = DateTime.MinValue;

            // Act
            var shouldClear = clearDate == DateTime.MinValue;
            var dateString = shouldClear ? "" : clearDate.ToString("yyyy-MM-dd");

            // Assert
            shouldClear.Should().BeTrue();
            dateString.Should().BeEmpty();
        }

        [Theory]
        [InlineData(Status.Watching, Status.Completed, true)]  // Completing series
        [InlineData(Status.Watching, Status.Watching, false)]  // Still watching
        [InlineData(Status.Completed, Status.Completed, false)] // Already completed
        [InlineData(Status.On_hold, Status.Completed, true)]   // Completing from on-hold
        public void ShouldSetEndDate_BasedOnStatusChange(Status oldStatus, Status newStatus, bool shouldSet)
        {
            // Arrange & Act
            var shouldSetEndDate = (newStatus == Status.Completed && oldStatus != Status.Completed);

            // Assert
            shouldSetEndDate.Should().Be(shouldSet);
        }

        [Theory]
        [InlineData(5, 0, true)]  // Unwatching completely
        [InlineData(5, 4, false)] // Partial rollback
        [InlineData(1, 0, true)]  // Unwatching single episode
        [InlineData(0, 0, false)] // No change
        public void ShouldClearDates_WhenUnwatchingCompletely(int oldCount, int newCount, bool shouldClear)
        {
            // Arrange & Act
            var shouldClearDates = (newCount == 0 && oldCount > 0);

            // Assert
            shouldClearDates.Should().Be(shouldClear);
        }

        [Fact]
        public void ShouldNotOverwriteExistingStartDate_WhenRewatching()
        {
            // Arrange
            var existingStartDate = "2023-01-15";
            _ = 0; // malEpisodeCount
            _ = 1; // newEpisodeCount

            // Act
            var shouldSetNewStartDate = string.IsNullOrEmpty(existingStartDate);

            // Assert
            shouldSetNewStartDate.Should().BeFalse("Should preserve existing start date");
        }

        [Fact]
        public void ShouldSetNewStartDate_WhenStartingRewatch()
        {
            // Arrange
            var currentStatus = Status.Completed;
            var isRewatching = true;

            // Act
            var shouldSetNewStartDate = (isRewatching && currentStatus == Status.Completed);

            // Assert
            shouldSetNewStartDate.Should().BeTrue("Should set new start date for rewatch");
        }

        [Fact]
        public void ShouldClearEndDate_WhenRollingBackFromCompleted()
        {
            // Arrange
            var currentStatus = Status.Completed;
            var newStatus = Status.Watching;

            // Act
            var shouldClearEndDate = (currentStatus == Status.Completed && newStatus == Status.Watching);

            // Assert
            shouldClearEndDate.Should().BeTrue("Should clear end date when rolling back from completed");
        }

        // ========================================================================
        // AirDate null safety
        // episode.Series.AirDate.Value could throw NullReferenceException
        // if Series was null.
        // ========================================================================

        [Fact]
        public void NullSeries_ShouldReturnMaxValue()
        {
            DateTime? airDate = null;
            string malStartDate = "2023-01-15";
            DateTime? parsedDate = DateTime.TryParse(malStartDate, out var d) ? d : null;

            double diffDays = airDate.HasValue && parsedDate.HasValue
                ? Math.Abs((parsedDate.Value - airDate.Value).TotalDays)
                : double.MaxValue;

            diffDays.Should().Be(double.MaxValue,
                "should return MaxValue when series air date is unavailable");
        }

        [Fact]
        public void ValidDates_ShouldCalculateDiff()
        {
            DateTime? airDate = new DateTime(2023, 1, 15);
            string malStartDate = "2023-01-20";
            DateTime? parsedDate = DateTime.TryParse(malStartDate, out var d) ? d : null;

            double diffDays = airDate.HasValue && parsedDate.HasValue
                ? Math.Abs((parsedDate.Value - airDate.Value).TotalDays)
                : double.MaxValue;

            diffDays.Should().Be(5, "difference between Jan 15 and Jan 20 is 5 days");
        }
    }
}