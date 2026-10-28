using System;
using CodexCompanion;

public static class CarryMotionTests
{
    static int checks;
    const double H = 208;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    static bool Valid(CarryMotion c)
    {
        return Finite(c.Angle) && Finite(c.AngularVelocity) && Finite(c.Amount)
            && Finite(c.OffsetX) && Finite(c.OffsetY) && Math.Abs(c.Angle) <= 24.000000001
            && Math.Abs(c.AngularVelocity) <= 300.0000001 && c.Amount >= 0 && c.Amount <= 1
            && c.GrabX >= 0 && c.GrabX <= 1 && c.GrabY >= 0 && c.GrabY <= 1 && c.Lift == 0;
    }
    static CarryMotion Grab(double gx, double gy)
    {
        CarryMotion c = new CarryMotion();
        c.BeginGrab(gx, gy, gx, gy, 0, 0, H);
        return c;
    }
    static CarryMotion Move(double gx, double gy, double speedX, double speedY, double seconds, double dt, out double maximum)
    {
        CarryMotion c = Grab(gx, gy);maximum = 0;
        int n = (int)Math.Round(seconds / dt);
        for (int i = 1; i <= n; i++)
        {
            c.Update(true, i * dt * speedX, i * dt * speedY, H, dt);
            maximum = Math.Max(maximum, Math.Abs(c.Angle));
            if (!Valid(c)) throw new Exception("nonfinite moving state");
        }
        return c;
    }
    // Same complete rotation+offset that the renderer is expected to apply.
    static double[] Transform(CarryMotion c, double x, double y)
    {
        double theta = c.Angle * Math.PI / 180, co = Math.Cos(theta), si = Math.Sin(theta);
        double rx = (x - c.GrabX) * 192, ry = (y - c.GrabY) * 208;
        return new[] {(co * rx - si * ry) / 192 + c.GrabX + c.OffsetX,
                      (si * rx + co * ry) / 208 + c.GrabY + c.OffsetY};
    }
    static CarryMotion Smooth(double dt)
    {
        CarryMotion c = Grab(.5, .12);double t = 0;
        while (t < 1 - 1e-10)
        {
            double step = Math.Min(dt, 1 - t);t += step;
            double x = H * (.8 * t - .8 / (2 * Math.PI) * Math.Sin(2 * Math.PI * t));
            c.Update(true, x, 0, H, step);
        }
        return c;
    }

    public static void Main()
    {
        CarryMotion c = new CarryMotion();
        Check(!c.Active && c.Angle == 0 && c.AngularVelocity == 0 && c.Lift == 0,
            "initial rigid sprite is exactly at rest with no artificial lift");
        c.BeginGrab(.5, .12, .5, .12, 80000, -90000, H);
        c.Update(true, 80000, -90000, H, 1.0 / 120);
        Check(c.Angle == 0 && c.AngularVelocity == 0,
            "first grab at a distant screen position creates no old-position impulse");

        double slowMax, fastMax, unused;
        CarryMotion slow = Move(.5, .12, 50, 0, .4, 1.0 / 120, out slowMax);
        CarryMotion fast = Move(.5, .12, 600, 0, .4, 1.0 / 120, out fastMax);
        Console.WriteLine("DETAIL speed peaks slow=" + slowMax.ToString("F4") + " fast=" + fastMax.ToString("F4"));
        Check(fastMax > slowMax * 2 && fastMax > 6,
            "a fast horizontal pull produces a materially larger swing than a slow pull");

        CarryMotion top = Move(.5, .12, 300, 0, .18, 1.0 / 120, out unused);
        CarryMotion bottom = Move(.5, .92, 300, 0, .18, 1.0 / 120, out unused);
        Check(top.Angle > 0 && bottom.Angle < 0,
            "top and bottom grabs reverse angular sign under the same rightward pull");
        double[] topCom = Transform(top, .5, .52), bottomCom = Transform(bottom, .5, .52);
        Check(topCom[0] < .5 && bottomCom[0] < .5,
            "the center of mass lags opposite the mouse for both top and bottom grab points");

        double centerMax, nearMax;
        Move(.5, .52, 600, 0, .4, 1.0 / 120, out centerMax);
        Move(.5, .515, 600, 0, .4, 1.0 / 120, out nearMax);
        Check(centerMax == 0 && nearMax < fastMax * .15,
            "grabbing at or near the center of mass produces little torque");

        CarryMotion leftward = Move(.5, .12, -400, 0, .4, 1.0 / 120, out unused);
        CarryMotion rightward = Move(.5, .12, 400, 0, .4, 1.0 / 120, out unused);
        Check(Math.Abs(leftward.Angle + rightward.Angle) < 1e-9
              && Math.Abs(leftward.AngularVelocity + rightward.AngularVelocity) < 1e-9,
            "left and right pulls have mirrored responses");
        CarryMotion sideA = Move(.22, .30, 260, -80, .3, 1.0 / 120, out unused);
        CarryMotion sideB = Move(.78, .30, -260, -80, .3, 1.0 / 120, out unused);
        Check(Math.Abs(sideA.Angle + sideB.Angle) < 1e-9,
            "mirrored side grab points preserve two-dimensional symmetry");

        CarryMotion upward = Move(.23, .26, 0, -400, .18, 1.0 / 120, out unused);
        CarryMotion downward = Move(.23, .26, 0, 400, .18, 1.0 / 120, out unused);
        Check(upward.Angle > downward.Angle + 2,
            "vertical pointer acceleration changes the torque for a side grab");
        CarryMotion centerVertical = Move(.5, .12, 0, -400, .3, 1.0 / 120, out unused);
        Check(centerVertical.Angle == 0,
            "pure vertical motion through the centerline creates no spurious sideways torque");

        c = Move(.5, .12, 450, 0, .35, 1.0 / 120, out unused);
        double lastX = 450 * Math.Round(.35 * 120) / 120.0;
        double stoppedMin = c.Angle;
        for (int i = 0; i < 240; i++)
        {
            c.Update(true, lastX, 0, H, 1.0 / 120);
            stoppedMin = Math.Min(stoppedMin, c.Angle);
        }
        Console.WriteLine("DETAIL stop reverse minimum=" + stoppedMin.ToString("F4"));
        Check(stoppedMin < -.5, "an abrupt stop causes a damped reverse swing");

        CarryMotion sideRest = Grab(.2, .25);
        for (int i = 0; i < 360; i++) sideRest.Update(true, 0, 0, H, 1.0 / 120);
        Check(sideRest.Angle > .5 && sideRest.Angle < 10,
            "off-center grabbing creates a restrained gravity-dependent resting bias");
        double restAngle = sideRest.Angle;
        for (int i = 0; i < 240; i++) sideRest.Update(true, 0, 0, H, 1.0 / 120);
        Check(Math.Abs(sideRest.Angle - restAngle) < .005,
            "a stationary grab converges without a synthetic periodic sway");

        c = Grab(.5, .12);
        c.BeginGrab(.2, .8, -.35, 1.4, 0, 0, H);
        double offsetX = c.OffsetX, offsetY = c.OffsetY;
        Check(Math.Abs(offsetX + .55) < 1e-12 && Math.Abs(offsetY - .6) < 1e-12,
            "displayed grab coordinates outside the source rectangle keep their true offset");
        for (int i = 1; i <= 100; i++)
        {
            c.Update(true, i * 3, i, H, 1.0 / 120);
            double[] pin = Transform(c, c.GrabX, c.GrabY);
            if (Math.Abs(pin[0] + .35) > 1e-10 || Math.Abs(pin[1] - 1.4) > 1e-10)
                throw new Exception("grab pin drifted");
        }
        Check(c.OffsetX == offsetX && c.OffsetY == offsetY,
            "the actual grab point remains pinned throughout the swing");

        c = Move(.5, .12, 300, 0, .18, 1.0 / 120, out unused);
        double angleBefore = c.Angle, angularBefore = c.AngularVelocity;
        double[][] samples = { new[] {.1,.15}, new[] {.5,.52}, new[] {.8,.9} };
        double[][] before = { Transform(c,.1,.15), Transform(c,.5,.52), Transform(c,.8,.9) };
        double[] newDisplayed = Transform(c,.77,.7);
        c.BeginGrab(.77,.7,newDisplayed[0],newDisplayed[1],500,800,H);
        bool continuous = Math.Abs(c.Angle-angleBefore)<1e-10 && Math.Abs(c.AngularVelocity-angularBefore)<1e-10;
        for (int i=0;i<samples.Length;i++)
        {
            double[] after = Transform(c,samples[i][0],samples[i][1]);
            continuous &= Math.Abs(after[0]-before[i][0])<1e-10 && Math.Abs(after[1]-before[i][1])<1e-10;
        }
        Check(continuous,"regrabbing a new source point preserves the complete displayed matrix and angular motion");
        c.BeginGrab(.6,.2,.61,.21,500,800,H,.65);
        Check(Math.Abs(c.Angle-.65)<1e-12 && Math.Abs(c.AngularVelocity-angularBefore)<1e-10,
            "explicit presentation angle initialization includes the existing gaze tilt without resetting angular velocity");

        c.Update(false,500,800,H,1.0/120);
        Check(c.Active && c.Angle!=0,"release keeps a continuous response instead of instantly snapping upright");
        for(int i=0;i<360;i++)c.Update(false,500,800,H,1.0/120);
        Check(c.Angle==0 && c.AngularVelocity==0 && c.OffsetX==0 && c.OffsetY==0 && c.Amount==0 && !c.Active,
            "release converges to exact upright rest and zero grab offset");

        CarryMotion coarse=Smooth(1.0/30),fine=Smooth(1.0/144);
        Console.WriteLine("DETAIL frame-rate angles 30=" + coarse.Angle.ToString("F4") + " 144=" + fine.Angle.ToString("F4")
            +" velocities "+coarse.AngularVelocity.ToString("F4")+" / "+fine.AngularVelocity.ToString("F4"));
        Check(Math.Abs(coarse.Angle-fine.Angle)<1.5 && Math.Abs(coarse.AngularVelocity-fine.AngularVelocity)<10,
            "30Hz and 144Hz samples give comparable responses for the same smooth motion");

        c = Grab(.5,.1);bool valid=true;
        for(int i=0;i<500;i++)
        {
            double dt=i%3==0?1.0/30:1.0/144;
            c.Update(true,(i%2==0?1:-1)*100000,i*500,H,dt);valid&=Valid(c);
        }
        Check(valid,"extreme pointer reversals respect the soft/hard angle and angular-speed limits");
        double[] times={0,-1,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,1e-300};
        foreach(double dt in times){c.Update(true,Double.MaxValue,-Double.MaxValue,Double.NaN,dt);valid&=Valid(c);}
        double[] coordinates={Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,Double.MaxValue,-Double.MaxValue,0};
        foreach(double x in coordinates){c.Update(true,x,-x,Double.PositiveInfinity,1.0/120);valid&=Valid(c);}
        Check(valid,"invalid clock, cursor, and size samples never produce NaN or unbounded output");
        c.BeginGrab(Double.NaN,Double.PositiveInfinity,Double.MaxValue,Double.NaN,Double.NaN,Double.PositiveInfinity,H,Double.NaN);
        Check(Valid(c),"invalid grab geometry falls back safely while preserving finite state");
        c.Update(true,1000000,-1000000,H,2);
        Check(Valid(c),"a long hidden-frame gap does not turn cursor displacement into an explosive impulse");
        c.Update(false,0,0,H,Double.MaxValue);
        Check(Valid(c) && !c.Active && c.Angle==0 && c.OffsetX==0 && c.OffsetY==0,
            "a long released gap completes settlement safely");
        Check(typeof(CarryMotion).GetProperty("Scale")==null && c.Lift==0,
            "the physics model has no scaling, limb, or additional lift output");
        Console.WriteLine("ALL " + checks + " PHYSICS CHECKS PASSED");
    }
}
