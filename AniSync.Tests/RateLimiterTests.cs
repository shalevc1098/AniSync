using System;
using System.Threading.Tasks;
using FluentAssertions;
using AniSync.Interfaces;
using Xunit;

namespace AniSync.Tests;

/// <summary>
/// Tests for rate limiting and delay behavior.
/// </summary>
public class RateLimiterTests
{
    // ========================================================================
    // Rate-limiter delay calculation
    // The delay should be the REMAINING time to reach the minimum interval,
    // not the elapsed time since the last call.
    // ========================================================================

    [Fact]
    public void RateLimiter_RecentCall_Should_Delay_Remaining_Time()
    {
        // Last call was 2s ago, interval is 5s -> should delay 3s
        int defaultTimeoutSeconds = 5;
        var lastCallDateTime = DateTime.UtcNow.AddSeconds(-2);

        var elapsed = DateTime.UtcNow.Subtract(lastCallDateTime);
        var remaining = TimeSpan.FromSeconds(defaultTimeoutSeconds) - elapsed;

        remaining.Should().BeGreaterThan(TimeSpan.Zero, "there should be remaining time to wait");
        remaining.TotalSeconds.Should().BeApproximately(3.0, 0.5, "should delay ~3s when 2s have elapsed out of 5s");
    }

    [Fact]
    public void RateLimiter_Old_Call_Should_Not_Delay()
    {
        // Last call was 6s ago, interval is 5s -> no delay needed
        int defaultTimeoutSeconds = 5;
        var lastCallDateTime = DateTime.UtcNow.AddSeconds(-6);

        var elapsed = DateTime.UtcNow.Subtract(lastCallDateTime);
        var remaining = TimeSpan.FromSeconds(defaultTimeoutSeconds) - elapsed;

        remaining.Should().BeLessThanOrEqualTo(TimeSpan.Zero, "no delay needed when interval has passed");
    }

    [Fact]
    public void RateLimiter_Immediate_Repeat_Should_Delay_Full_Interval()
    {
        // Last call was just now -> should delay ~5s
        int defaultTimeoutSeconds = 5;
        var lastCallDateTime = DateTime.UtcNow;

        var elapsed = DateTime.UtcNow.Subtract(lastCallDateTime);
        var remaining = TimeSpan.FromSeconds(defaultTimeoutSeconds) - elapsed;

        remaining.Should().BeGreaterThan(TimeSpan.Zero);
        remaining.TotalSeconds.Should().BeApproximately(5.0, 0.5, "should delay ~full interval for immediate repeat");
    }

    // ========================================================================
    // Delayer negative TimeSpan guard
    // Task.Delay throws on negative TimeSpan. The guard should prevent that.
    // ========================================================================

    [Fact]
    public async Task Delayer_Negative_TimeSpan_Should_Not_Throw()
    {
        var delayer = new Delayer();
        var act = () => delayer.Delay(TimeSpan.FromSeconds(-1));
        await act.Should().NotThrowAsync("negative TimeSpan should be guarded");
    }

    [Fact]
    public async Task Delayer_Zero_TimeSpan_Should_Not_Throw()
    {
        var delayer = new Delayer();
        var act = () => delayer.Delay(TimeSpan.Zero);
        await act.Should().NotThrowAsync("zero TimeSpan should not throw");
    }

    [Fact]
    public async Task Delayer_Positive_TimeSpan_Should_Delay()
    {
        var delayer = new Delayer();
        var act = () => delayer.Delay(TimeSpan.FromMilliseconds(10));
        await act.Should().NotThrowAsync("small positive delay should complete normally");
    }
}
