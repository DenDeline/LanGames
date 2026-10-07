namespace LanPong;

/// <summary>Chooses only the right paddle's input from the authoritative game state.</summary>
internal interface ILocalOpponentController
{
    void Reset();
    int GetAxis(GameState state);
}

// Step 1 provides the session boundary; a playing policy is added in the next step.
internal sealed class StationaryLocalOpponentController : ILocalOpponentController
{
    public void Reset() { }

    public int GetAxis(GameState state) => 0;
}
