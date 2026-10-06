namespace LanPong.Tests;

public sealed class PeerPingTrackerTests
{
    private static readonly Guid SessionId = Guid.ParseExact("ffeeddccbbaa99887766554433221100", "N");

    [Test]
    public async Task CreatePing_UsesIntervalAndResetStartsSequenceAgain()
    {
        var tracker = new PeerPingTracker();
        var now = DateTime.UtcNow;

        var first = tracker.CreatePing(SessionId, now);
        await Assert.That(first is { SessionId: var sessionId, Sequence: 1 } && sessionId == SessionId).IsTrue();
        await Assert.That(tracker.CreatePing(SessionId, now + NetworkConstants.PingInterval - TimeSpan.FromTicks(1))).IsNull();
        var second = tracker.CreatePing(SessionId, now + NetworkConstants.PingInterval);
        await Assert.That(second?.Sequence).IsEqualTo(2);

        tracker.Reset();

        await Assert.That(tracker.PingMs).IsNull();
        await Assert.That(tracker.CreatePing(SessionId, now)?.Sequence).IsEqualTo(1);
    }

    [Test]
    public async Task Observe_RequiresPendingSequenceAndSampleExpires()
    {
        var tracker = new PeerPingTracker();
        var now = DateTime.UtcNow;
        var ping = tracker.CreatePing(SessionId, now)!;

        tracker.Observe(new PongPacket { SessionId = SessionId, Sequence = ping.Sequence + 1 });
        await Assert.That(tracker.PingMs).IsNull();

        tracker.Observe(new PongPacket { SessionId = SessionId, Sequence = ping.Sequence });
        await Assert.That(tracker.PingMs is >= 0).IsTrue();

        tracker.Expire(DateTime.UtcNow + NetworkConstants.PingStaleAfter + TimeSpan.FromSeconds(1));
        await Assert.That(tracker.PingMs).IsNull();
    }
}
