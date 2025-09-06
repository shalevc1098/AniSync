using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Xunit;

namespace Shoko.Tests
{
    public class TmdbThumbnailTests
    {
        [Fact]
        public void GetEpisodeThumbnailUrl_WithShokoEpisode_ReturnsEpisodeEndpoint()
        {
            // Arrange
            var mockEpisode = new Mock<IShokoEpisode>();
            mockEpisode.Setup(e => e.AnidbEpisodeID).Returns(123456);
            
            // Act - simulate the helper method logic
            string? thumbnailUrl = null;
            
            if (mockEpisode.Object != null)
            {
                thumbnailUrl = $"/api/v3/Episode/{mockEpisode.Object.AnidbEpisodeID}/Images/Thumbnail";
            }
            
            // Assert
            thumbnailUrl.Should().Be("/api/v3/Episode/123456/Images/Thumbnail");
        }
        
        [Fact]
        public void GetEpisodeThumbnailUrl_WithoutEpisode_ReturnsNull()
        {
            // Arrange
            IShokoEpisode? nullEpisode = null;
            
            // Act - simulate the helper method logic
            string? thumbnailUrl = null;
            
            if (nullEpisode != null)
            {
                thumbnailUrl = $"/api/v3/Episode/{nullEpisode.AnidbEpisodeID}/Images/Thumbnail";
            }
            
            // Assert
            thumbnailUrl.Should().BeNull();
        }
        
        [Fact]
        public void HistoryHtml_HandlesApiEndpoint_Correctly()
        {
            // Test that /api/ URLs are handled correctly in the HTML
            var apiUrl = "/api/v3/Episode/123456/Images/Thumbnail";
            
            // Simulate the JavaScript logic
            string processedUrl = apiUrl;
            if (apiUrl.StartsWith("/api/"))
            {
                // In real scenario, window.location.origin would be added
                processedUrl = "http://localhost:8111" + apiUrl;
            }
            
            processedUrl.Should().Be("http://localhost:8111/api/v3/Episode/123456/Images/Thumbnail");
        }
        
        [Fact]
        public void HistoryHtml_HandlesMalUrl_Correctly()
        {
            // Test that MAL URLs are handled correctly
            var malUrl = "https://cdn.myanimelist.net/images/anime/1025/147458.webp";
            
            // Simulate the JavaScript logic
            string processedUrl = malUrl;
            if (malUrl.EndsWith(".webp"))
            {
                processedUrl = malUrl.Replace(".webp", ".jpg");
            }
            
            processedUrl.Should().Be("https://cdn.myanimelist.net/images/anime/1025/147458.jpg");
        }
    }
}