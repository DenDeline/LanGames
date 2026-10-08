using System.Reflection;

namespace LanPong.Tests;

/// <summary>Tests seed a near-winning Playing checkpoint; the production clock scores the seventh goal.</summary>
internal static class PeerTestAccess
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static T Field<T>(PongPeer peer, string name) =>
        (T)typeof(PongPeer).GetField(name, Fields)!.GetValue(peer)!;

    internal static void UnderGate(PongPeer peer, Action action)
    {
        lock (Field<Lock>(peer, "_gate")) action();
    }

    internal static void DeliverGuest(PongPeer peer, WirePacket packet) => Deliver(peer, "HandleGuestPacketLocked", packet);
    internal static void DeliverHostSession(PongPeer peer, WirePacket packet) => Deliver(peer, "HandleHostSessionPacketLocked", packet);

    private static void Deliver(PongPeer peer, string method, WirePacket packet)
    {
        UnderGate(peer, () =>
        {
            var actions = Activator.CreateInstance(typeof(PongPeer).GetNestedType("PacketActions", BindingFlags.NonPublic)!);
            typeof(PongPeer).GetMethod(method, Fields)!.Invoke(peer, [packet, DateTime.UtcNow, actions]);
        });
    }

    internal static async Task<PongSnapshot> FinishAsync(PongPeer peer)
    {
        UnderGate(peer, () =>
        {
            var game = Field<GameEngine>(peer, "_game");
            game.RestoreCheckpoint(game.Capture() with
            {
                Phase = GamePhase.Playing, LeftScore = GameConstants.WinningScore - 1, RightScore = 2,
                BallX = 1, BallY = 0.5, BallVx = 1, BallVy = 0, Countdown = 0
            });
            var hostSide = Field<PaddleSide?>(peer, "_hostSide")!.Value;
            Field<HostRollbackTimeline>(peer, "_hostTimeline").Reset(hostSide);
        });
        return await BotTestSupport.WaitForAsync(peer, state => state.Phase == GamePhase.GameOver);
    }
}
