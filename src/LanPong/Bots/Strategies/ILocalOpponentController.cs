namespace LanPong.Bots.Strategies;

/// <summary>Chooses a vertical axis from the canonical right-paddle policy view.</summary>
internal interface ILocalOpponentController
{
    void Reset();
    int GetAxis(GameState state);
}
