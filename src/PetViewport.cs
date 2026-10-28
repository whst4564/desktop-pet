using System;
using System.Windows;

namespace CodexCompanion
{
    /// <summary>
    /// Separates the character's logical size/origin from its transparent
    /// rotation margin. Persist and align ImageRect, not the outer window.
    /// </summary>
    public sealed class PetViewport
    {
        public double ImageHeight { get; private set; }
        public double ImageWidth { get; private set; }
        public double Padding { get; private set; }
        public double WindowWidth { get; private set; }
        public double WindowHeight { get; private set; }

        public PetViewport(double imageHeight) : this(imageHeight, 0)
        {
        }

        public PetViewport(double imageHeight, double minimumPadding)
        {
            if (!Finite(imageHeight)) imageHeight = 224;
            ImageHeight = Math.Max(80.0 * 208 / 192, Math.Min(416, imageHeight));
            ImageWidth = ImageHeight * 192 / 208;
            // For any pivot inside the image, a 24-degree turn displaces a
            // corner by at most 2 * diagonal * sin(12deg), less than 0.6H.
            if (!Finite(minimumPadding) || minimumPadding < 0) minimumPadding = 0;
            Padding = Math.Max(ImageHeight * .6, minimumPadding);
            WindowWidth = ImageWidth + 2 * Padding;
            WindowHeight = ImageHeight + 2 * Padding;
        }

        public Rect ImageRect(double windowLeft, double windowTop)
        {
            return new Rect(Coordinate(windowLeft) + Padding,
                Coordinate(windowTop) + Padding, ImageWidth, ImageHeight);
        }

        public Point WindowOrigin(double imageLeft, double imageTop)
        {
            return new Point(Coordinate(imageLeft) - Padding,
                Coordinate(imageTop) - Padding);
        }

        public Point ClampWindowOrigin(double left, double top, Rect screen)
        {
            Rect image = ImageRect(left, top);
            if (screen.IsEmpty || !Finite(screen.X) || !Finite(screen.Y)
                || !Finite(screen.Width) || !Finite(screen.Height)
                || screen.Width <= 0 || screen.Height <= 0)
                return WindowOrigin(image.Left, image.Top);

            double imageLeft = Clamp(image.Left, screen.Left,
                Math.Max(screen.Left, screen.Right - ImageWidth));
            double imageTop = Clamp(image.Top, screen.Top,
                Math.Max(screen.Top, screen.Bottom - ImageHeight));
            return WindowOrigin(imageLeft, imageTop);
        }

        public Point ResizeKeepingFeet(double oldWindowLeft, double oldWindowTop,
            PetViewport next)
        {
            if (next == null) throw new ArgumentNullException("next");
            Rect image = ImageRect(oldWindowLeft, oldWindowTop);
            return next.WindowOrigin(image.Left + image.Width / 2 - next.ImageWidth / 2,
                image.Bottom - next.ImageHeight);
        }

        static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        static double Coordinate(double value)
        {
            return Finite(value) ? value : 0;
        }

        static double Clamp(double value, double low, double high)
        {
            return Math.Max(low, Math.Min(high, value));
        }
    }
}
