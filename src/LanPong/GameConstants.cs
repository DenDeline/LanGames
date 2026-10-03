namespace LanPong;

/// <summary>Rules and normalized geometry used by the authoritative simulation.</summary>
internal static class GameConstants
{
    public const int SimulationTicksPerSecond = 60;
    public const double FixedStepSeconds = 1.0 / SimulationTicksPerSecond;
    public const double ArenaCenter = 0.5;
    public const double ArenaAspectWidth = 16;
    public const double ArenaAspectHeight = 9;

    public const double LeftPaddleX = 0.045;
    public const double RightPaddleX = 0.955;
    public const double PaddleHalfWidth = 0.009;
    public const double PaddleHalfHeight = 0.09;
    public const double PaddleSpeed = 0.85;
    public const double MinPaddleY = PaddleHalfHeight;
    public const double MaxPaddleY = 1 - PaddleHalfHeight;

    // A circular ball in the 16:9 arena needs different normalized X and Y radii.
    public const double BallRadiusY = 0.012;
    public const double BallRadiusX = BallRadiusY * ArenaAspectHeight / ArenaAspectWidth;
    public const double LeftContactX = LeftPaddleX + PaddleHalfWidth + BallRadiusX;
    public const double RightContactX = RightPaddleX - PaddleHalfWidth - BallRadiusX;
    public const double TopContactY = BallRadiusY;
    public const double BottomContactY = 1 - BallRadiusY;

    public const double ServeCountdownSeconds = 1.6;
    public const double ServeSpeedX = 0.55;
    public const double ServeSpeedY = 0.19;
    public const double BounceSpeedIncrease = 0.035;
    public const double MaximumBounceSpeed = 0.9;
    public const double MaximumBounceAngle = 0.8;
    public const double VerticalBounceScale = 0.8;
    public const int WinningScore = 7;

    // Normalized-coordinate tolerance admits a contact computed just outside a step
    // because of floating-point rounding. Separation moves a missed ball off the
    // paddle plane so the next collision query cannot select the same contact.
    public const double ContactTolerance = 1e-10;
    public const double MissSeparation = 1e-7;

    // The host normally advances by FixedStepSeconds; these bound replay calls and
    // untrusted guest state without constraining valid host speeds/countdowns.
    public const double MaximumAdvanceSeconds = 10;
    public const double MaximumReplicaBallSpeed = 1.5;
    public const double MaximumReplicaCountdown = 5;
}
