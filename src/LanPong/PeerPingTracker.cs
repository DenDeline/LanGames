using System.Diagnostics;

namespace LanPong;

/// <summary>Tracks the round-trip time for one connected peer. The caller synchronizes access.</summary>
internal sealed class PeerPingTracker
{
    private long _sequence;
    private long _pendingSequence;
    private long _sentTimestamp;
    private DateTime _lastSent = DateTime.MinValue;
    private DateTime _lastPongSeen = DateTime.MinValue;

    public double? PingMs { get; private set; }

    public PingPacket? CreatePing(Guid sessionId, DateTime now)
    {
        if (now - _lastSent < NetworkConstants.PingInterval) return null;

        _lastSent = now;
        _pendingSequence = ++_sequence;
        _sentTimestamp = Stopwatch.GetTimestamp();
        return new PingPacket { SessionId = sessionId, Sequence = _pendingSequence };
    }

    public void Observe(PongPacket packet)
    {
        if (_pendingSequence == 0 || packet.Sequence != _pendingSequence) return;

        var sample = Stopwatch.GetElapsedTime(_sentTimestamp).TotalMilliseconds;
        if (sample >= 0 && sample < NetworkConstants.MaximumPingRoundTrip.TotalMilliseconds)
        {
            PingMs = PingMs is { } previous
                ? previous * (1 - NetworkConstants.PingSmoothingAlpha) + sample * NetworkConstants.PingSmoothingAlpha
                : sample;
            _lastPongSeen = DateTime.UtcNow;
        }
        _pendingSequence = 0;
    }

    public void Expire(DateTime now)
    {
        if (PingMs is not null && now - _lastPongSeen > NetworkConstants.PingStaleAfter)
            PingMs = null;
    }

    public void Reset()
    {
        _sequence = _pendingSequence = _sentTimestamp = 0;
        PingMs = null;
        _lastSent = _lastPongSeen = DateTime.MinValue;
    }
}
