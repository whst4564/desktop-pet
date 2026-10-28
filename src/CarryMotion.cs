using System;

namespace CodexCompanion
{
    // Damped rigid-sprite suspension. All forces use body-heights per second.
    // No independent limb state, image scale, or translated lift is used.
    public sealed class CarryMotion
    {
        const double Degrees = 180.0 / Math.PI;
        const double MaximumAngle = 24.0 / Degrees;
        const double SoftAngle = 20.0 / Degrees;
        const double MaximumAngularSpeed = 300.0 / Degrees;
        const double MaximumStep = 1.0 / 240;
        const double BodyWidthRatio = 192.0 / 208;
        bool heldLast, hasCursor, preparedInput;
        double cursorLastX, cursorLastY;
        double velocityX, velocityY, accelerationX, accelerationY;
        double theta, angularVelocity;
        public CarryMotion() { GrabX = .5; GrabY = .25; }
        public double Amount { get; private set; }
        public double Angle { get { return theta * Degrees; } }
        public double AngularVelocity { get { return angularVelocity * Degrees; } }
        public double GrabX { get; private set; }
        public double GrabY { get; private set; }
        public double OffsetX { get; private set; }
        public double OffsetY { get; private set; }
        public double Lift { get { return 0; } }
        public bool Active { get { return heldLast || Amount != 0 || theta != 0 || angularVelocity != 0 || OffsetX != 0 || OffsetY != 0; } }

        public void BeginGrab(double grabX, double grabY, double displayedX, double displayedY,
                              double cursorX, double cursorY, double height)
        { BeginGrab(grabX, grabY, displayedX, displayedY, cursorX, cursorY, height, Angle); }

        public void BeginGrab(double grabX, double grabY, double displayedX, double displayedY,
                              double cursorX, double cursorY, double height, double presentationAngleDegrees)
        {
            GrabX = Bound(Finite(grabX) ? grabX : GrabX, 0, 1);
            GrabY = Bound(Finite(grabY) ? grabY : GrabY, 0, 1);
            // A rotated visible point can be outside the unrotated sprite.
            // Keep its true display offset, including negative coordinates.
            displayedX = DisplayCoordinate(displayedX, GrabX + OffsetX);
            displayedY = DisplayCoordinate(displayedY, GrabY + OffsetY);
            OffsetX = displayedX - GrabX;
            OffsetY = displayedY - GrabY;
            if (Finite(presentationAngleDegrees))
                theta = Bound(presentationAngleDegrees / Degrees, -MaximumAngle, MaximumAngle);
            // Rebinding preserves angular velocity; the caller's source-to-
            // display offset makes the complete before/after matrices match.
            ResetInput();
            if (Finite(cursorX) && Finite(cursorY))
            { cursorLastX = cursorX; cursorLastY = cursorY; hasCursor = true; }
            preparedInput = true;
        }

        public void Update(bool held, double cursorX, double cursorY, double height, double dt)
        {
            bool entered = held && !heldLast;
            heldLast = held;
            if (!Finite(dt) || dt <= 0)
            { ResetInput(); preparedInput = false; return; }
            if (!held && dt > .5)
            { SetRest(); ResetInput(); preparedInput = false; return; }
            bool gap = dt > .25;
            if (gap) ResetInput();
            double elapsed = Math.Min(dt, .25);
            double normalHeight = Finite(height) && height > 1 ? Bound(height, 40, 10000) : 208;
            if (held)
            {
                if (entered && !preparedInput) ResetInput();
                SampleCursor(cursorX, cursorY, normalHeight, dt, gap);
            }
            else ResetInput();
            preparedInput = false;
            Amount = Approach(Amount, held ? 1 : 0, elapsed, held ? .10 : .14);
            Amount = Bound(Amount, 0, 1);
            if (held && 1 - Amount < .0001) Amount = 1;
            if (!held && Amount < .0001) Amount = 0;
            int steps = Math.Max(1, (int)Math.Ceiling(elapsed / MaximumStep));
            double step = elapsed / steps;
            for (int i = 0; i < steps; i++) Integrate(held, step);
            if (!held)
            {
                OffsetX = Approach(OffsetX, 0, elapsed, .14);
                OffsetY = Approach(OffsetY, 0, elapsed, .14);
                if (Math.Abs(OffsetX) < .00001) OffsetX = 0;
                if (Math.Abs(OffsetY) < .00001) OffsetY = 0;
                if (Math.Abs(theta) < .005 / Degrees && Math.Abs(angularVelocity) < .02 / Degrees)
                { theta = 0; angularVelocity = 0; }
            }
        }

        void SampleCursor(double x, double y, double height, double dt, bool gap)
        {
            if (!Finite(x) || !Finite(y)) { ResetInput(); return; }
            double rawX = 0, rawY = 0;
            if (hasCursor && !gap)
            {
                rawX = (x - cursorLastX) / height / dt;
                rawY = (y - cursorLastY) / height / dt;
                if (!Finite(rawX) || !Finite(rawY)) { rawX = 0; rawY = 0; }
            }
            cursorLastX = x; cursorLastY = y; hasCursor = true;
            LimitVector(ref rawX, ref rawY, 12);
            double oldX = velocityX, oldY = velocityY;
            velocityX = Approach(velocityX, rawX, dt, .050);
            velocityY = Approach(velocityY, rawY, dt, .050);
            double rawAX = (velocityX - oldX) / dt, rawAY = (velocityY - oldY) / dt;
            if (!Finite(rawAX) || !Finite(rawAY)) { rawAX = 0; rawAY = 0; }
            LimitVector(ref rawAX, ref rawAY, 80);
            accelerationX = Approach(accelerationX, rawAX, dt, .035);
            accelerationY = Approach(accelerationY, rawAY, dt, .035);
            LimitVector(ref accelerationX, ref accelerationY, 80);
        }

        void Integrate(bool held, double dt)
        {
            double alpha;
            if (held)
            {
                double rx = (.5 - GrabX) * BodyWidthRatio, ry = .52 - GrabY;
                double inertia = .10 + rx * rx + ry * ry;
                double co = Math.Cos(theta), si = Math.Sin(theta);
                double rotatedX = co * rx - si * ry, rotatedY = si * rx + co * ry;
                // Effective force = gravity - anchor acceleration - weak air
                // resistance. A top-held body lags left when pulled right.
                double fx = -accelerationX - 2.2 * velocityX;
                double fy = 4 - accelerationY - 2.2 * velocityY;
                double torque = (rotatedX * fy - rotatedY * fx) / inertia;
                double frequency = Math.Sqrt(64 + 4 * Math.Max(0, ry) / inertia);
                alpha = torque - 64 * theta - 2 * .38 * frequency * angularVelocity;
            }
            else alpha = -100 * theta - 2 * .9 * 10 * angularVelocity;
            double over = Math.Max(0, Math.Abs(theta) - SoftAngle) / (MaximumAngle - SoftAngle);
            if (over > 0) alpha -= Math.Sign(theta) * 200 * over * over * over;
            angularVelocity = Bound(angularVelocity + alpha * dt, -MaximumAngularSpeed, MaximumAngularSpeed);
            theta += angularVelocity * dt;
            if (theta > MaximumAngle)
            { theta = MaximumAngle; if (angularVelocity > 0) angularVelocity = 0; }
            if (theta < -MaximumAngle)
            { theta = -MaximumAngle; if (angularVelocity < 0) angularVelocity = 0; }
        }

        void ResetInput()
        { hasCursor = false; velocityX = velocityY = accelerationX = accelerationY = 0; }
        void SetRest() { Amount = theta = angularVelocity = OffsetX = OffsetY = 0; }
        static double DisplayCoordinate(double value, double fallback)
        { return Finite(value) && Math.Abs(value) < 1000000 ? value : fallback; }
        static void LimitVector(ref double x, ref double y, double limit)
        {
            if (!Finite(x) || !Finite(y)) { x = 0; y = 0; return; }
            double largest = Math.Max(Math.Abs(x), Math.Abs(y));
            if (largest == 0) return;
            double nx = x / largest, ny = y / largest, scaledLength = Math.Sqrt(nx * nx + ny * ny);
            if (largest > limit / scaledLength)
            { x = nx * limit / scaledLength; y = ny * limit / scaledLength; }
        }
        static double Approach(double value, double target, double dt, double seconds)
        { return target + (value - target) * Math.Exp(-dt / seconds); }
        static double Bound(double value, double low, double high)
        { return Math.Max(low, Math.Min(high, value)); }
        static bool Finite(double value)
        { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
