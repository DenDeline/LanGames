using System.Diagnostics;
using System.Net;

namespace LanPong;

/// <summary>Transport cadence, liveness, and retry thresholds.</summary>
internal static class NetworkConstants
{
    public static readonly IPEndPoint AnyIpv4Endpoint = new(IPAddress.Any, 0);

    // Also shown in the static browser form and mirrored in app.js.
    public const int DefaultUdpPort = 47777;

    // Bound scheduler catch-up; two 60 Hz simulation ticks yield a 30 Hz state stream.
    public const int MaximumSimulationCatchUpSteps = 4;
    public const int StateSendIntervalTicks = 2;
    public const int GuestInputRefreshesPerSecond = 30;
    public static readonly TimeSpan GuestInputSendInterval = TimeSpan.FromSeconds(1.0 / GuestInputRefreshesPerSecond);
    public static readonly long GuestInputSendIntervalTicks =
        (long)Math.Round(Stopwatch.Frequency * GuestInputSendInterval.TotalSeconds);
    public const int BrowserSnapshotsPerSecond = 30;
    public static readonly TimeSpan BrowserSnapshotInterval = TimeSpan.FromSeconds(1.0 / BrowserSnapshotsPerSecond);

    // Controls expire if a tab or remote player stops refreshing them.
    public static readonly TimeSpan InputStaleAfter = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan PeerIdleTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HelloRetryInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RestartRetryInterval = TimeSpan.FromMilliseconds(250);

    // Ping is the process-to-process UDP round trip shown in the UI, not input-to-screen latency.
    // A stale sample disappears before the peer timeout.
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PingStaleAfter = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaximumPingRoundTrip = TimeSpan.FromSeconds(5);
    public const double PingSmoothingAlpha = 0.25;

    public static readonly TimeSpan DiscoveryResponseWindow = TimeSpan.FromMilliseconds(1300);
    public static readonly TimeSpan DiscoveryReceiveRetryDelay = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan UdpReceiveRetryDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan UdpByeTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WebSocketCloseTimeout = TimeSpan.FromSeconds(1);
}
