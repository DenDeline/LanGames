using System.Net;

namespace LanPong.Tests;

public sealed class RejectedChallengeCacheTests
{
    [Test]
    public async Task EntryExpiresAtChallengeLifetimeBoundary()
    {
        var cache = new RejectedChallengeCache();
        var address = Address(4000);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        cache.Remember(address, id, now);

        await Assert.That(cache.Contains(address, id, now + NetworkConstants.ChallengeLifetime - TimeSpan.FromTicks(1))).IsTrue();
        await Assert.That(cache.Contains(address, id, now + NetworkConstants.ChallengeLifetime)).IsFalse();
    }

    [Test]
    public async Task DuplicateDoesNotConsumeCapacity()
    {
        var cache = new RejectedChallengeCache();
        var now = DateTime.UtcNow;
        var firstAddress = Address(4000);
        var firstId = Guid.NewGuid();
        cache.Remember(firstAddress, firstId, now);
        cache.Remember(firstAddress, firstId, now);

        for (var index = 0; index < 31; index++)
            cache.Remember(Address(4001 + index), Guid.NewGuid(), now);

        await Assert.That(cache.Contains(firstAddress, firstId, now)).IsTrue();
        cache.Remember(Address(5000), Guid.NewGuid(), now);
        await Assert.That(cache.Contains(firstAddress, firstId, now)).IsFalse();
    }

    [Test]
    public async Task CapacityEvictsOldestEntryFirst()
    {
        var cache = new RejectedChallengeCache();
        var now = DateTime.UtcNow;
        var oldest = Address(4000);
        var oldestId = Guid.NewGuid();
        var second = Address(4001);
        var secondId = Guid.NewGuid();
        cache.Remember(oldest, oldestId, now);
        cache.Remember(second, secondId, now);
        for (var index = 0; index < 30; index++)
            cache.Remember(Address(4002 + index), Guid.NewGuid(), now);

        cache.Remember(Address(5000), Guid.NewGuid(), now);

        await Assert.That(cache.Contains(oldest, oldestId, now)).IsFalse();
        await Assert.That(cache.Contains(second, secondId, now)).IsTrue();
    }

    [Test]
    public async Task StoredAddressDoesNotChangeWhenReceiveAddressIsReused()
    {
        var cache = new RejectedChallengeCache();
        var source = Address(4000);
        var original = Address(4000);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        cache.Remember(source, id, now);
        source[3] = 1;

        await Assert.That(cache.Contains(original, id, now)).IsTrue();
        await Assert.That(cache.Contains(source, id, now)).IsFalse();
        cache.Clear();
        await Assert.That(cache.Contains(original, id, now)).IsFalse();
    }

    private static SocketAddress Address(int port) => new IPEndPoint(IPAddress.Loopback, port).Serialize();
}
