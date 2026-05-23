using System;

namespace AniSync.Helpers
{
    public static class RatingHelpers
    {
        public static int NormalizeRating(double rating) => (int)Math.Clamp(Math.Round(rating), 0, 10);
    }
}
