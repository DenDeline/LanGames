namespace LanPong.Bots.Strategies;

/// <summary>
/// Presents either physical paddle as the canonical right-side policy input. Keep this view out of
/// authoritative worlds and network replay checkpoints. Offline policies may use it in their own
/// counterfactual simulations, as the training teacher already does.
/// </summary>
internal static class BotPolicyView
{
    internal static GameState ForSide(GameState state, PaddleSide side)
    {
        PaddleSides.Validate(side);
        return side == PaddleSide.Right ? state : state with
        {
            LeftY = state.RightY,
            RightY = state.LeftY,
            BallX = 1 - state.BallX,
            BallVx = -state.BallVx,
            LeftScore = state.RightScore,
            RightScore = state.LeftScore,
            ServeDirection = -state.ServeDirection
        };
    }
}
