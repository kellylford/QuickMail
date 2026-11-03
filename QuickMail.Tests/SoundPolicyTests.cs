using System;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SoundPolicyTests
{
    private DateTime _now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private SoundPolicy NewPolicy() => new(() => _now);

    private bool Ask(SoundPolicy policy, string key = "sent", SoundOrigin origin = SoundOrigin.Direct,
        bool muted = false, bool quiet = false, int waiting = 0) =>
        policy.ShouldPlay(key, origin, muted, quiet, waiting);

    [Fact]
    public void AnOrdinarySoundPlays() => Assert.True(Ask(NewPolicy()));

    [Theory]
    [InlineData(SoundOrigin.Direct)]
    [InlineData(SoundOrigin.Background)]
    [InlineData(SoundOrigin.Arrival)]
    [InlineData(SoundOrigin.Rule)]
    public void MuteSilencesEveryOrigin(SoundOrigin origin) =>
        Assert.False(Ask(NewPolicy(), origin: origin, muted: true));

    [Theory]
    [InlineData(SoundOrigin.Background)]
    [InlineData(SoundOrigin.Arrival)]
    [InlineData(SoundOrigin.Rule)]
    public void QuietHoldsBackWhatTheyDidNotJustDo(SoundOrigin origin) =>
        Assert.False(Ask(NewPolicy(), origin: origin, quiet: true));

    [Fact]
    public void QuietStillAnswersWhatTheyJustDid() =>
        Assert.True(Ask(NewPolicy(), origin: SoundOrigin.Direct, quiet: true));

    [Theory]
    [InlineData(SoundPolicy.MaxWaiting - 1, true)]
    [InlineData(SoundPolicy.MaxWaiting, false)]
    [InlineData(SoundPolicy.MaxWaiting + 1, false)]
    public void TheQueueIsCapped(int waiting, bool plays) =>
        Assert.Equal(plays, Ask(NewPolicy(), waiting: waiting));

    [Fact]
    public void TheSameSoundIsDroppedInsideTheGapAndPlaysAfterIt()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy));

        _now += SoundPolicy.SameSoundGap - TimeSpan.FromMilliseconds(1);
        Assert.False(Ask(policy));

        _now += TimeSpan.FromMilliseconds(1);
        Assert.True(Ask(policy));
    }

    [Fact]
    public void ADifferentSoundIsNotARepeat()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy, key: "sent"));
        Assert.True(Ask(policy, key: "failed"));
    }

    [Fact]
    public void ADroppedSoundDoesNotExtendTheGap()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy));
        _now += TimeSpan.FromMilliseconds(900);
        Assert.False(Ask(policy));
        _now += TimeSpan.FromMilliseconds(100);   // A full gap after the one that played.
        Assert.True(Ask(policy));
    }

    [Fact]
    public void ARuleSoundHoldsBackTheNewMailSoundBehindIt()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy, key: "matched", origin: SoundOrigin.Rule));

        _now += SoundPolicy.RuleHoldsArrivalFor - TimeSpan.FromMilliseconds(1);
        Assert.False(Ask(policy, key: "arrived", origin: SoundOrigin.Arrival));

        _now += TimeSpan.FromMilliseconds(1);
        Assert.True(Ask(policy, key: "arrived", origin: SoundOrigin.Arrival));
    }

    [Fact]
    public void ARuleSoundDroppedAsARepeatStillHoldsBackNewMail()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy, key: "matched", origin: SoundOrigin.Rule));
        _now += TimeSpan.FromMilliseconds(500);
        Assert.False(Ask(policy, key: "matched", origin: SoundOrigin.Rule));

        _now += TimeSpan.FromMilliseconds(2800);   // 3.3 s after the first rule, 2.8 s after the second.
        Assert.False(Ask(policy, key: "arrived", origin: SoundOrigin.Arrival));
    }

    [Fact]
    public void ARuleSoundDoesNotHoldBackOtherOrigins()
    {
        var policy = NewPolicy();
        Assert.True(Ask(policy, key: "matched", origin: SoundOrigin.Rule));
        Assert.True(Ask(policy, key: "sent", origin: SoundOrigin.Direct));
        Assert.True(Ask(policy, key: "connected", origin: SoundOrigin.Background));
    }

    [Fact]
    public void AMutedRuleDoesNotHoldBackLaterNewMail()
    {
        var policy = NewPolicy();
        Assert.False(Ask(policy, key: "matched", origin: SoundOrigin.Rule, muted: true));
        Assert.True(Ask(policy, key: "arrived", origin: SoundOrigin.Arrival));
    }
}
