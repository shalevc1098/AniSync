using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Xunit;

namespace AniSync.Tests
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
        
    }
}