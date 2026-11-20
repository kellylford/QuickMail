using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>#901: background news waits until the user pauses, and is said once.</summary>
public class QuietTimeAnnouncementTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    [Fact]
    public void NothingQueued_NothingTaken()
        => Assert.Null(new QuietTimeAnnouncement(Quiet).TakeIfQuiet(T0.AddHours(1)));

    [Fact]
    public void Queued_WaitsForTheQuietPeriod_ThenIsTakenOnce()
    {
        var gate = new QuietTimeAnnouncement(Quiet);
        gate.Queue("kept", T0);

        Assert.True(gate.IsPending);
        Assert.Null(gate.TakeIfQuiet(T0));
        Assert.Null(gate.TakeIfQuiet(T0.AddSeconds(1.9)));
        Assert.Equal("kept", gate.TakeIfQuiet(T0.AddSeconds(2)));
        Assert.False(gate.IsPending);
        Assert.Null(gate.TakeIfQuiet(T0.AddSeconds(30)));
    }

    [Fact]
    public void Activity_StartsThePauseAgain()
    {
        var gate = new QuietTimeAnnouncement(Quiet);
        gate.Queue("kept", T0);

        // Keys every second for ten seconds: never quiet for two.
        for (int s = 1; s <= 10; s++)
        {
            gate.NoteActivity(T0.AddSeconds(s));
            Assert.Null(gate.TakeIfQuiet(T0.AddSeconds(s + 0.5)));
        }

        Assert.Null(gate.TakeIfQuiet(T0.AddSeconds(11.9)));
        Assert.Equal("kept", gate.TakeIfQuiet(T0.AddSeconds(12)));
    }

    [Fact]
    public void ActivityBeforeTheQueue_DoesNotDelayItBeyondTheQuietPeriod()
    {
        var gate = new QuietTimeAnnouncement(Quiet);
        gate.NoteActivity(T0);
        gate.Queue("kept", T0.AddSeconds(10));
        Assert.Null(gate.TakeIfQuiet(T0.AddSeconds(11)));
        Assert.Equal("kept", gate.TakeIfQuiet(T0.AddSeconds(12)));
    }

    [Fact]
    public void Clear_ForgetsWhatWasHeld()
    {
        var gate = new QuietTimeAnnouncement(Quiet);
        gate.Queue("kept", T0);
        gate.Clear();
        Assert.Null(gate.TakeIfQuiet(T0.AddMinutes(1)));
    }
}
