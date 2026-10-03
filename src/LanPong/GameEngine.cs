namespace LanPong;

/// <summary>The host owns this simulation; guests only display its snapshots.</summary>
internal sealed class GameEngine
{
    private const double PaddleHalfHeight = 0.09;
    private const double PaddleSpeed = 0.85;
    // The arena is 16:9; the ball is circular in pixels, not in normalized coordinates.
    private const double BallRadiusX = 0.012 * 9.0 / 16.0;
    private const double BallRadiusY = 0.012;
    private const double LeftPaddleX = 0.045;
    private const double RightPaddleX = 0.955;
    private const double PaddleHalfWidth = 0.009;
    private const double LeftContactX = LeftPaddleX + PaddleHalfWidth + BallRadiusX;
    private const double RightContactX = RightPaddleX - PaddleHalfWidth - BallRadiusX;
    private const double ContactTolerance = 1e-10;
    private const int WinningScore = 7;

    private int _serveDirection = 1;
    private int _hits;

    // C# 14 field-backed properties keep paddle centres inside the arena.
    public double LeftY { get; private set => field = Math.Clamp(value, PaddleHalfHeight, 1 - PaddleHalfHeight); } = 0.5;
    public double RightY { get; private set => field = Math.Clamp(value, PaddleHalfHeight, 1 - PaddleHalfHeight); } = 0.5;
    public double BallX { get; private set; } = 0.5;
    public double BallY { get; private set; } = 0.5;
    public double BallVx { get; private set; }
    public double BallVy { get; private set; }
    public int LeftScore { get; private set; }
    public int RightScore { get; private set; }
    public string Phase { get; private set; } = "waiting";
    public double Countdown { get; private set; }
    public long TickNumber { get; private set; }
    public int RoundId { get; private set; }

    public void ResetWaiting()
    {
        LeftY = RightY = BallX = BallY = 0.5;
        BallVx = BallVy = 0;
        LeftScore = RightScore = 0;
        Countdown = 0;
        TickNumber = 0;
        Phase = "waiting";
        _hits = 0;
    }

    public void StartMatch()
    {
        LeftY = RightY = 0.5;
        LeftScore = RightScore = 0;
        RoundId++;
        StartRound(1);
    }

    private void StartRound(int direction)
    {
        _serveDirection = direction;
        _hits = 0;
        BallX = BallY = 0.5;
        BallVx = BallVy = 0;
        Countdown = 1.6;
        Phase = "countdown";
    }

    public void Advance(double dt, int leftAxis, int rightAxis)
    {
        TickNumber++;
        if (Phase is "waiting" or "gameover") return;

        leftAxis = Math.Clamp(leftAxis, -1, 1);
        rightAxis = Math.Clamp(rightAxis, -1, 1);
        var startLeftY = LeftY;
        var startRightY = RightY;
        LeftY += leftAxis * PaddleSpeed * dt;
        RightY += rightAxis * PaddleSpeed * dt;

        if (Phase == "countdown")
        {
            Countdown = Math.Max(0, Countdown - dt);
            if (Countdown <= 0)
            {
                Phase = "playing";
                BallVx = 0.55 * _serveDirection;
                BallVy = (_serveDirection > 0 ? 0.19 : -0.19);
            }
            return;
        }

        // Resolve the first contact inside this fixed step, then spend the remaining
        // time with the reflected velocity. This avoids tunnelling and early bounces.
        var remaining = dt;
        var elapsed = 0.0;
        var leftPaddleChecked = false;
        var rightPaddleChecked = false;
        for (var contacts = 0; contacts < 4 && remaining > 0; contacts++)
        {
            var firstTime = remaining + 1;
            var first = Contact.None;

            if (BallVy < 0)
            {
                var time = (BallRadiusY - BallY) / BallVy;
                if (time >= -ContactTolerance && time <= remaining)
                {
                    firstTime = Math.Max(0, time);
                    first = Contact.Top;
                }
            }
            else if (BallVy > 0)
            {
                var time = (1 - BallRadiusY - BallY) / BallVy;
                if (time >= -ContactTolerance && time <= remaining)
                {
                    firstTime = Math.Max(0, time);
                    first = Contact.Bottom;
                }
            }

            if (BallVx < 0 && !leftPaddleChecked && BallX >= LeftContactX - ContactTolerance)
            {
                var time = (LeftContactX - BallX) / BallVx;
                if (time >= -ContactTolerance && time <= remaining && Math.Max(0, time) < firstTime)
                {
                    firstTime = Math.Max(0, time);
                    first = Contact.LeftPaddle;
                }
            }
            else if (BallVx > 0 && !rightPaddleChecked && BallX <= RightContactX + ContactTolerance)
            {
                var time = (RightContactX - BallX) / BallVx;
                if (time >= -ContactTolerance && time <= remaining && Math.Max(0, time) < firstTime)
                {
                    firstTime = Math.Max(0, time);
                    first = Contact.RightPaddle;
                }
            }

            if (first == Contact.None)
            {
                BallX += BallVx * remaining;
                BallY += BallVy * remaining;
                remaining = 0;
                break;
            }

            BallX += BallVx * firstTime;
            BallY += BallVy * firstTime;
            elapsed += firstTime;
            remaining -= firstTime;
            switch (first)
            {
                case Contact.Top:
                    BallY = BallRadiusY;
                    BallVy = -BallVy;
                    break;
                case Contact.Bottom:
                    BallY = 1 - BallRadiusY;
                    BallVy = -BallVy;
                    break;
                case Contact.LeftPaddle:
                    leftPaddleChecked = true;
                    var leftAtContact = Math.Clamp(startLeftY + leftAxis * PaddleSpeed * elapsed,
                        PaddleHalfHeight, 1 - PaddleHalfHeight);
                    if (Math.Abs(BallY - leftAtContact) <= PaddleHalfHeight + BallRadiusY)
                    {
                        BallX = LeftContactX;
                        Bounce(leftAtContact, 1);
                    }
                    else BallX = LeftContactX - 1e-7;
                    break;
                case Contact.RightPaddle:
                    rightPaddleChecked = true;
                    var rightAtContact = Math.Clamp(startRightY + rightAxis * PaddleSpeed * elapsed,
                        PaddleHalfHeight, 1 - PaddleHalfHeight);
                    if (Math.Abs(BallY - rightAtContact) <= PaddleHalfHeight + BallRadiusY)
                    {
                        BallX = RightContactX;
                        Bounce(rightAtContact, -1);
                    }
                    else BallX = RightContactX + 1e-7;
                    break;
            }
        }

        if (remaining > 0)
        {
            BallX += BallVx * remaining;
            BallY += BallVy * remaining;
        }

        if (BallX < -BallRadiusX)
        {
            RightScore++;
            AfterPoint(-1);
        }
        else if (BallX > 1 + BallRadiusX)
        {
            LeftScore++;
            AfterPoint(1);
        }
    }

    private enum Contact { None, Top, Bottom, LeftPaddle, RightPaddle }

    private void Bounce(double paddleY, int direction)
    {
        _hits++;
        var speed = Math.Min(0.55 + _hits * 0.035, 0.9);
        var angle = Math.Clamp((BallY - paddleY) / PaddleHalfHeight, -1, 1) * 0.8;
        BallVx = direction * speed * Math.Cos(angle);
        BallVy = speed * Math.Sin(angle) * 0.8;
    }

    private void AfterPoint(int direction)
    {
        if (LeftScore >= WinningScore || RightScore >= WinningScore)
        {
            Phase = "gameover";
            BallVx = BallVy = 0;
            Countdown = 0;
        }
        else
        {
            StartRound(direction);
        }
    }

    public void Load(WirePacket packet)
    {
        LeftY = packet.LeftY;
        RightY = packet.RightY;
        BallX = Math.Clamp(packet.BallX, -BallRadiusX, 1 + BallRadiusX);
        BallY = Math.Clamp(packet.BallY, 0, 1);
        BallVx = Math.Clamp(packet.BallVx, -1.5, 1.5);
        BallVy = Math.Clamp(packet.BallVy, -1.5, 1.5);
        LeftScore = Math.Max(0, packet.LeftScore);
        RightScore = Math.Max(0, packet.RightScore);
        Phase = packet.Phase is "waiting" or "countdown" or "playing" or "gameover" ? packet.Phase : "waiting";
        Countdown = Math.Clamp(packet.Countdown, 0, 5);
        TickNumber = packet.Sequence;
        RoundId = Math.Max(0, packet.RoundId);
    }
}
