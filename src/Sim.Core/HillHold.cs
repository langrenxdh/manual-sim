namespace Sim.Core;

/// <summary>Golf hill-start assist: a brake-pressure state machine (docs/design.md, 坡道与起步辅助).</summary>
internal sealed class HillHold
{
    public HillHoldState State { get; private set; } = HillHoldState.Inactive;
    public double RemainingS { get; private set; }
    /// <summary>Retained brake force. Acts as max(driver brake, retained), not in addition to it.</summary>
    public double ForceN { get; private set; }

    /// <param name="driveForceN">Drive force at the wheels from the previous step.</param>
    /// <param name="gravityAlongRoadN">Signed gravity component along the road (negative = pulls backwards).</param>
    public void Update(HillHoldParams p, double brakePedal, double speedMps, double grade,
        double driveForceN, double gravityAlongRoadN, double dt)
    {
        bool onSlope = p.Enabled && Math.Abs(grade) >= p.MinGrade;
        bool stationary = Math.Abs(speedMps) <= p.StandstillSpeedMps;
        bool braking = brakePedal >= p.BrakePedalThreshold;

        switch (State)
        {
            case HillHoldState.Inactive:
                if (onSlope && stationary && braking) Arm(p);
                break;

            case HillHoldState.Armed:
                if (!onSlope || !stationary) Deactivate();
                else if (!braking)
                {
                    State = HillHoldState.Holding;
                    RemainingS = p.HoldTimeS;
                }
                break;

            case HillHoldState.Holding:
                if (!p.Enabled) { Deactivate(); break; }
                if (braking) { Arm(p); break; }
                RemainingS = Math.Max(0, RemainingS - dt);
                // Uphill drive force opposes gravity: compare along the uphill direction.
                double uphill = -Friction.Sign(gravityAlongRoadN);
                if (driveForceN * uphill >= p.DriveAwayForceRatio * Math.Abs(gravityAlongRoadN))
                    State = HillHoldState.ReleasingDriveAway;
                else if (RemainingS <= 0)
                    State = HillHoldState.ReleasingRollback;
                break;

            case HillHoldState.ReleasingDriveAway:
            case HillHoldState.ReleasingRollback:
                ForceN = Math.Max(0, ForceN - p.ReleaseRateNPerS * dt);
                if (ForceN == 0) Deactivate();
                break;
        }
    }

    private void Arm(HillHoldParams p)
    {
        State = HillHoldState.Armed;
        ForceN = p.HoldForceN;
        RemainingS = p.HoldTimeS;
    }

    private void Deactivate()
    {
        State = HillHoldState.Inactive;
        ForceN = 0;
        RemainingS = 0;
    }
}
