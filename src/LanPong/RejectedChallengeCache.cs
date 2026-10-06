using System.Net;

namespace LanPong;

/// <summary>
/// Recently rejected challenge IDs, bounded so retried Hello packets cannot reopen them.
/// PongPeer synchronizes access.
/// </summary>
internal sealed class RejectedChallengeCache
{
    private const int MaximumRememberedChallenges = 32;
    private readonly List<(SocketAddress Address, Guid RequestId, DateTime RejectedAt)> _entries = [];

    public bool Contains(SocketAddress address, Guid requestId, DateTime now)
    {
        var found = false;
        var retained = 0;
        var count = _entries.Count;
        for (var index = 0; index < count; index++)
        {
            var entry = _entries[index];
            if (now - entry.RejectedAt >= NetworkConstants.ChallengeLifetime)
                continue;
            if (retained != index)
                _entries[retained] = entry;
            retained++;
            if (entry.RequestId == requestId && entry.Address.Equals(address))
                found = true;
        }
        if (retained < count)
            _entries.RemoveRange(retained, count - retained);
        return found;
    }

    public void Remember(SocketAddress address, Guid requestId, DateTime now)
    {
        if (Contains(address, requestId, now)) return;
        if (_entries.Count == MaximumRememberedChallenges)
            _entries.RemoveAt(0);

        // ReceiveFromAsync reuses its SocketAddress; retain an independent copy.
        var storedAddress = new SocketAddress(address.Family, address.Size);
        for (var index = 0; index < address.Size; index++)
            storedAddress[index] = address[index];
        _entries.Add((storedAddress, requestId, now));
    }

    public void Clear() => _entries.Clear();
}
