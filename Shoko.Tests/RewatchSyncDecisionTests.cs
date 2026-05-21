using FluentAssertions;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Models.Mal;
using Xunit;

namespace Shoko.Tests;

/// <summary>
/// Tests the real <see cref="RewatchSyncDecision.Decide"/> used by the plugin (not a
/// re-implementation), covering the rewatch trigger and the forward/rollback paths.
/// </summary>
public class RewatchSyncDecisionTests
{
    // Convenience wrapper with the common defaults for a watched-episode event.
    private static SyncDecision Watched(
        int episode, int playbackCount, int malCount, int total, Status status,
        bool isRewatching = false, int rewatchCount = 0,
        bool rewatchEnabled = true, bool rollbackEnabled = false)
        => RewatchSyncDecision.Decide(true, episode, playbackCount, malCount, total, status,
            isRewatching, rewatchCount, rewatchEnabled, rollbackEnabled);

    // --- First watch-through: PlaybackCount is 1, so a rewatch must never be detected ---

    [Theory]
    [InlineData(1, 0)]   // first episode, MAL empty
    [InlineData(6, 5)]   // mid progress
    public void FirstWatchThrough_NeverRewatches(int episode, int malCount)
    {
        var d = Watched(episode, playbackCount: 1, malCount: malCount, total: 12, status: malCount == 0 ? Status.Plan_to_watch : Status.Watching);

        d.ShouldUpdate.Should().BeTrue();
        d.NewEpisodeCount.Should().Be(episode);
        d.SetRewatching.Should().BeNull("a first watch is not a rewatch");
        d.NumberOfTimesRewatched.Should().BeNull();
    }

    // --- The bug we fixed: a stray watched-save on a completed show must NOT reset progress ---

    [Theory]
    [InlineData(5, 1)]   // mid episode, only watched once > not a replay
    [InlineData(7, 1)]
    [InlineData(5, 2)]   // replayed, but not episode 1 > not a series restart
    public void StrayWatchOnCompletedShow_DoesNotResetOrRewatch(int episode, int playbackCount)
    {
        var d = Watched(episode, playbackCount, malCount: 12, total: 12, status: Status.Completed);

        d.ShouldUpdate.Should().BeFalse("a non-restart watch on a finished show must leave MAL untouched");
        d.NewEpisodeCount.Should().Be(12, "progress must not be reset");
    }

    // --- Genuine rewatch start: completed show, episode 1 actually replayed ---

    [Fact]
    public void GenuineRestart_StartsRewatch()
    {
        var d = Watched(episode: 1, playbackCount: 2, malCount: 12, total: 12, status: Status.Completed);

        d.ShouldUpdate.Should().BeTrue();
        d.NewEpisodeCount.Should().Be(1);
        d.SetRewatching.Should().Be(true);
        d.NewStatus.Should().BeNull("status stays Completed during a rewatch");
        d.NumberOfTimesRewatched.Should().BeNull("count only increments when the rewatch finishes");
    }

    [Fact]
    public void RewatchDisabled_NoRewatchOnRestart()
    {
        var d = Watched(episode: 1, playbackCount: 2, malCount: 12, total: 12, status: Status.Completed, rewatchEnabled: false);

        d.ShouldUpdate.Should().BeFalse();
        d.SetRewatching.Should().BeNull();
    }

    [Fact]
    public void NonCompletedReplay_DoesNotFlagRewatching()
    {
        // All episodes watched but status still Watching > must not set is_rewatching.
        var d = Watched(episode: 1, playbackCount: 2, malCount: 12, total: 12, status: Status.Watching);

        d.ShouldUpdate.Should().BeFalse();
        d.SetRewatching.Should().BeNull();
    }

    // --- Progressing and finishing a rewatch ---

    [Fact]
    public void RewatchProgress_AdvancesEpisode()
    {
        // Already rewatching, MAL at 1, watch episode 2.
        var d = Watched(episode: 2, playbackCount: 2, malCount: 1, total: 12, status: Status.Completed, isRewatching: true);

        d.ShouldUpdate.Should().BeTrue();
        d.NewEpisodeCount.Should().Be(2);
        d.NumberOfTimesRewatched.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public void RewatchFinale_IncrementsCountAndClearsFlag(int currentRewatchCount, int expectedCount)
    {
        // Rewatching, reaching the final episode.
        var d = Watched(episode: 12, playbackCount: 2, malCount: 11, total: 12, status: Status.Completed,
            isRewatching: true, rewatchCount: currentRewatchCount);

        d.ShouldUpdate.Should().BeTrue();
        d.NewEpisodeCount.Should().Be(12);
        d.NewStatus.Should().Be(Status.Completed);
        d.SetRewatching.Should().Be(false);
        d.NumberOfTimesRewatched.Should().Be(expectedCount);
    }

    [Fact]
    public void SingleEpisodeMovie_Replay_IsOneShotRewatch()
    {
        var d = Watched(episode: 1, playbackCount: 2, malCount: 1, total: 1, status: Status.Completed, rewatchCount: 0);

        d.ShouldUpdate.Should().BeTrue();
        d.NewStatus.Should().Be(Status.Completed);
        d.SetRewatching.Should().Be(false);
        d.NumberOfTimesRewatched.Should().Be(1);
    }

    // --- Forward path basics (non-rewatch) ---

    [Fact]
    public void Forward_FirstAdd_SetsWatching()
    {
        var d = Watched(episode: 1, playbackCount: 1, malCount: 0, total: 12, status: Status.Plan_to_watch);

        d.ShouldUpdate.Should().BeTrue();
        d.NewStatus.Should().Be(Status.Watching);
    }

    [Fact]
    public void Forward_ReachingFinale_Completes()
    {
        var d = Watched(episode: 12, playbackCount: 1, malCount: 11, total: 12, status: Status.Watching);

        d.ShouldUpdate.Should().BeTrue();
        d.NewStatus.Should().Be(Status.Completed);
        d.SetRewatching.Should().Be(false);
        d.NumberOfTimesRewatched.Should().BeNull("not a rewatch");
    }

    [Fact]
    public void Forward_CapsAtTotal()
    {
        var d = Watched(episode: 99, playbackCount: 1, malCount: 5, total: 12, status: Status.Watching);
        d.NewEpisodeCount.Should().Be(12);
    }

    [Fact]
    public void ZeroTotal_DoesNotComplete()
    {
        var d = Watched(episode: 5, playbackCount: 1, malCount: 4, total: 0, status: Status.Watching);
        d.ShouldUpdate.Should().BeTrue();
        d.NewStatus.Should().BeNull();
    }

    // --- Rollback (unwatched) path ---

    [Fact]
    public void Unwatch_HighestEpisode_RollsBackWhenEnabled()
    {
        var d = RewatchSyncDecision.Decide(false, 12, 1, 12, 12, Status.Completed, false, 0, true, rollbackEnabled: true);

        d.ShouldUpdate.Should().BeTrue();
        d.NewEpisodeCount.Should().Be(11);
        d.NewStatus.Should().Be(Status.Watching);
        d.SetRewatching.Should().Be(false);
    }

    [Fact]
    public void Unwatch_RollbackDisabled_NoChange()
    {
        var d = RewatchSyncDecision.Decide(false, 12, 1, 12, 12, Status.Completed, false, 0, true, rollbackEnabled: false);
        d.ShouldUpdate.Should().BeFalse();
        d.NewEpisodeCount.Should().Be(12);
    }

    [Fact]
    public void Unwatch_DuringRewatch_KeepsRewatchFlag()
    {
        var d = RewatchSyncDecision.Decide(false, 12, 2, 12, 12, Status.Completed, isRewatching: true, 0, true, rollbackEnabled: true);
        d.SetRewatching.Should().BeNull("don't clear is_rewatching while a rewatch is in progress");
    }
}
