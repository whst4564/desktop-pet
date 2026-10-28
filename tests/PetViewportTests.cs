using System;
using System.Windows;
using CodexCompanion;

public static class PetViewportTests
{
    static int checks;

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    static bool Near(double a, double b)
    {
        return Math.Abs(a - b) < 1e-8;
    }

    static bool SamePoint(Point a, Point b)
    {
        return Near(a.X, b.X) && Near(a.Y, b.Y);
    }

    static Point Turn(Point point, Point pivot, double radians)
    {
        double x = point.X - pivot.X, y = point.Y - pivot.Y;
        return new Point(pivot.X + x * Math.Cos(radians) - y * Math.Sin(radians),
            pivot.Y + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    public static void Main()
    {
        PetViewport view = new PetViewport(224);
        Check(Near(view.ImageHeight, 224) && Near(view.ImageWidth, 224.0 * 192 / 208),
            "logical image size retains the original aspect ratio");
        Check(Near(view.Padding, 224 * .6)
            && Near(view.WindowWidth, view.ImageWidth + 2 * view.Padding)
            && Near(view.WindowHeight, view.ImageHeight + 2 * view.Padding),
            "transparent margin expands only the outer window");

        double[] invalid = { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity };
        bool defaults = true;
        foreach (double height in invalid)
            defaults &= new PetViewport(height).ImageHeight == 224;
        Check(defaults, "nonfinite height uses the documented default");
        Check(Near(new PetViewport(-100).ImageWidth, 80)
            && Near(new PetViewport(0).ImageWidth, 80)
            && new PetViewport(100000).ImageHeight == 416,
            "finite heights are clamped to the native control range");

        Point imageOrigin = new Point(-1750.5, -202.25);
        Point windowOrigin = view.WindowOrigin(imageOrigin.X, imageOrigin.Y);
        Rect logical = view.ImageRect(windowOrigin.X, windowOrigin.Y);
        Check(SamePoint(logical.TopLeft, imageOrigin),
            "logical and outer origins round trip with negative desktop coordinates");
        Check(SamePoint(view.WindowOrigin(logical.Left, logical.Top), windowOrigin),
            "the transparent window origin also round trips exactly");

        PetViewport expanded = new PetViewport(224, 500);
        Check(expanded.ImageHeight == view.ImageHeight && expanded.ImageWidth == view.ImageWidth
            && expanded.Padding == 500,
            "a larger minimum margin leaves the logical image size unchanged");
        Check(Near(expanded.WindowWidth - view.WindowWidth, 2 * (500 - view.Padding))
            && Near(expanded.WindowHeight - view.WindowHeight, 2 * (500 - view.Padding)),
            "a dynamic minimum margin expands only the transparent window");
        Point expandedOrigin = expanded.WindowOrigin(imageOrigin.X, imageOrigin.Y);
        Rect expandedImage = expanded.ImageRect(expandedOrigin.X, expandedOrigin.Y);
        Check(SamePoint(expandedImage.TopLeft, logical.TopLeft)
            && Near(expandedImage.Width, logical.Width) && Near(expandedImage.Height, logical.Height),
            "rebasing an expanded window preserves the entire image in world coordinates");
        Check(SamePoint(expanded.WindowOrigin(expandedImage.Left, expandedImage.Top), expandedOrigin),
            "expanded padding retains the logical-to-window round trip");
        PetViewport expandedNext = new PetViewport(416, 600);
        Point expandedResized = expanded.ResizeKeepingFeet(expandedOrigin.X, expandedOrigin.Y, expandedNext);
        Rect expandedResizedImage = expandedNext.ImageRect(expandedResized.X, expandedResized.Y);
        Check(Near(expandedResizedImage.Bottom, logical.Bottom)
            && Near(expandedResizedImage.Left + expandedResizedImage.Width / 2,
                logical.Left + logical.Width / 2),
            "changing image size and minimum margin together preserves the foot anchor");
        bool safeMinimum = true;
        foreach (double minimum in new double[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, -500, 0, 1 })
        {
            PetViewport safe = new PetViewport(224, minimum);
            safeMinimum &= safe.Padding == view.Padding
                && safe.WindowWidth == view.WindowWidth && safe.WindowHeight == view.WindowHeight;
        }
        Check(safeMinimum, "invalid or sub-baseline minimum margins safely retain the default margin");

        bool anchored = true;
        foreach (double height in new double[] { 80.0 * 208 / 192, 121.333333333, 224, 416 })
        {
            PetViewport next = new PetViewport(height);
            Point resized = view.ResizeKeepingFeet(windowOrigin.X, windowOrigin.Y, next);
            Rect image = next.ImageRect(resized.X, resized.Y);
            anchored &= Near(image.Bottom, logical.Bottom)
                && Near(image.Left + image.Width / 2, logical.Left + logical.Width / 2);
        }
        Check(anchored, "resizing preserves the logical foot baseline and horizontal center");

        Rect desktop = new Rect(-1920, -1080, 4480, 2520);
        Point clamped = view.ClampWindowOrigin(-10000, -10000, desktop);
        Rect clampedImage = view.ImageRect(clamped.X, clamped.Y);
        Check(Near(clampedImage.Left, desktop.Left) && Near(clampedImage.Top, desktop.Top),
            "negative multi-monitor edges clamp the image rather than its padding");
        Check(clamped.X < desktop.Left && clamped.Y < desktop.Top,
            "transparent padding may extend outside the desktop without pushing the image inward");

        clamped = view.ClampWindowOrigin(10000, 10000, desktop);
        clampedImage = view.ImageRect(clamped.X, clamped.Y);
        Check(Near(clampedImage.Right, desktop.Right) && Near(clampedImage.Bottom, desktop.Bottom),
            "far desktop edges preserve the image's usable boundary");
        Point inside = view.WindowOrigin(-100, -50);
        Check(SamePoint(view.ClampWindowOrigin(inside.X, inside.Y, desktop), inside),
            "an in-bounds character stays at the same logical position");

        clamped = view.ClampWindowOrigin(100, 100, new Rect(-10, -20, 20, 30));
        clampedImage = view.ImageRect(clamped.X, clamped.Y);
        Check(Near(clampedImage.Left, -10) && Near(clampedImage.Top, -20),
            "an unusually small desktop uses a stable top-left fallback");

        Check(SamePoint(view.ClampWindowOrigin(inside.X, inside.Y, Rect.Empty), inside),
            "an unavailable desktop bound does not shift the character");
        bool rejected = false;
        try { view.ResizeKeepingFeet(0, 0, null); }
        catch (ArgumentNullException) { rejected = true; }
        Check(rejected, "a missing resize target is rejected explicitly");

        // The norm bound covers every interior pivot and every intermediate
        // angle, not only the finite sample sweep performed below.
        bool bound = true, samples = true;
        foreach (double height in new double[] { 80.0 * 208 / 192, 208, 416 })
        {
            PetViewport v = new PetViewport(height);
            double diagonal = Math.Sqrt(v.ImageWidth * v.ImageWidth + v.ImageHeight * v.ImageHeight);
            bound &= 2 * diagonal * Math.Sin(12 * Math.PI / 180) < v.Padding;
            Point[] corners = {
                new Point(0, 0), new Point(v.ImageWidth, 0),
                new Point(0, v.ImageHeight), new Point(v.ImageWidth, v.ImageHeight)
            };
            for (int px = 0; px <= 8; px++)
                for (int py = 0; py <= 8; py++)
                    for (int angle = -96; angle <= 96; angle++)
                    {
                        Point pivot = new Point(v.ImageWidth * px / 8, v.ImageHeight * py / 8);
                        double radians = angle * .25 * Math.PI / 180;
                        foreach (Point corner in corners)
                        {
                            Point turned = Turn(corner, pivot, radians);
                            samples &= turned.X >= -v.Padding && turned.Y >= -v.Padding
                                && turned.X <= v.ImageWidth + v.Padding
                                && turned.Y <= v.ImageHeight + v.Padding;
                        }
                    }
        }
        Check(bound, "the continuous rotation-distance bound fits every pivot at up to 24 degrees");
        Check(samples, "all sampled rotated corners remain inside the padded window at every supported size");
        Console.WriteLine("ALL " + checks + " VIEWPORT CHECKS PASSED");
    }
}
