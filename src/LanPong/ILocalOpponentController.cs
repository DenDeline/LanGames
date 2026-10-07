namespace LanPong;

/// <summary>Chooses only the right paddle's input from the authoritative game state.</summary>
internal interface ILocalOpponentController
{
    void Reset();
    int GetAxis(GameState state);
}
