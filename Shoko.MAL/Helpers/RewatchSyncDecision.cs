using Shoko.AniSync.Models.Mal;

namespace Shoko.AniSync.Helpers
{
    /// <summary>
    /// The outcome of evaluating a single episode watch/unwatch event against the
    /// current MAL list status. Null fields mean "leave unchanged" (the MAL update
    /// layer omits null fields from the request).
    /// </summary>
    public readonly struct SyncDecision
    {
        public bool ShouldUpdate { get; init; }
        public int NewEpisodeCount { get; init; }
        public Status? NewStatus { get; init; }
        public bool? SetRewatching { get; init; }
        public int? NumberOfTimesRewatched { get; init; }

        public static SyncDecision NoChange(int currentEpisodeCount) => new()
        {
            ShouldUpdate = false,
            NewEpisodeCount = currentEpisodeCount
        };
    }

    /// <summary>
    /// Pure decision logic for syncing a Shoko episode watch state to MAL. Kept free of
    /// I/O and config lookups so it can be unit-tested directly (the production handler
    /// and the tests call this same method).
    /// </summary>
    public static class RewatchSyncDecision
    {
        /// <param name="isWatched">Episode marked watched (true) or unwatched (false) in Shoko.</param>
        /// <param name="shokoEpisodeNumber">The episode number from the event.</param>
        /// <param name="playbackCount">How many times this episode has been played (>=2 means a genuine replay).</param>
        /// <param name="malEpisodeCount">Current num_watched_episodes on MAL.</param>
        /// <param name="totalEpisodes">Total episodes for the series (0 if unknown).</param>
        /// <param name="currentStatus">Current MAL list status.</param>
        /// <param name="isRewatching">Whether MAL currently has is_rewatching=true.</param>
        /// <param name="currentRewatchCount">Current num_times_rewatched on MAL.</param>
        /// <param name="rewatchDetectionEnabled">User setting.</param>
        /// <param name="rollbackEnabled">User setting (AllowRollback).</param>
        public static SyncDecision Decide(
            bool isWatched,
            int shokoEpisodeNumber,
            int playbackCount,
            int malEpisodeCount,
            int totalEpisodes,
            Status currentStatus,
            bool isRewatching,
            int currentRewatchCount,
            bool rewatchDetectionEnabled,
            bool rollbackEnabled)
        {
            if (isWatched)
            {
                if (shokoEpisodeNumber > malEpisodeCount)
                {
                    var newEpisodeCount = (totalEpisodes > 0 && shokoEpisodeNumber > totalEpisodes)
                        ? totalEpisodes
                        : shokoEpisodeNumber;

                    Status? newStatus = null;
                    if (currentStatus != Status.Watching && currentStatus != Status.Completed)
                        newStatus = Status.Watching;

                    bool? setRewatching = null;
                    int? numberOfTimesRewatched = null;
                    bool reachedEnd = totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes;
                    if (reachedEnd)
                    {
                        newStatus = Status.Completed;
                        setRewatching = false;
                        if (isRewatching)
                            numberOfTimesRewatched = currentRewatchCount + 1;
                    }

                    return new SyncDecision
                    {
                        ShouldUpdate = true,
                        NewEpisodeCount = newEpisodeCount,
                        NewStatus = newStatus,
                        SetRewatching = setRewatching,
                        NumberOfTimesRewatched = numberOfTimesRewatched
                    };
                }

                bool isRewatchStart = rewatchDetectionEnabled
                    && currentStatus == Status.Completed
                    && !isRewatching
                    && playbackCount >= 2
                    && shokoEpisodeNumber == 1;

                if (isRewatchStart)
                {
                    if (totalEpisodes == 1)
                    {
                        return new SyncDecision
                        {
                            ShouldUpdate = true,
                            NewEpisodeCount = totalEpisodes,
                            NewStatus = Status.Completed,
                            SetRewatching = false,
                            NumberOfTimesRewatched = currentRewatchCount + 1
                        };
                    }

                    return new SyncDecision
                    {
                        ShouldUpdate = true,
                        NewEpisodeCount = 1,
                        NewStatus = null, // stays Completed while rewatching
                        SetRewatching = true,
                        NumberOfTimesRewatched = null
                    };
                }

                return SyncDecision.NoChange(malEpisodeCount);
            }

            if (rollbackEnabled && shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
            {
                Status? newStatus = null;
                bool? setRewatching = null;
                if (currentStatus == Status.Completed)
                {
                    newStatus = Status.Watching;
                    if (!isRewatching)
                        setRewatching = false;
                }

                return new SyncDecision
                {
                    ShouldUpdate = true,
                    NewEpisodeCount = malEpisodeCount - 1,
                    NewStatus = newStatus,
                    SetRewatching = setRewatching,
                    NumberOfTimesRewatched = null
                };
            }

            return SyncDecision.NoChange(malEpisodeCount);
        }
    }
}
