using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WinTubeRelay.Tray;

internal static class Branding
{
    public const string AppName = "WinTubeRelay";
    public const string StartupEntryName = "WinTubeRelay";

    public static string BuildHeaderText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? AppName
            : $"{AppName} v{version.Major}.{version.Minor}.{version.Build}";
    }

    public static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var backgroundBrush = new LinearGradientBrush(
            new Rectangle(0, 0, 64, 64),
            Color.FromArgb(255, 20, 18, 26),
            Color.FromArgb(255, 46, 24, 22),
            135f);
        FillRoundedRectangle(graphics, backgroundBrush, new RectangleF(4, 4, 56, 56), 16);

        using var emberBrush = new LinearGradientBrush(
            new PointF(20, 12),
            new PointF(44, 52),
            Color.FromArgb(255, 255, 214, 102),
            Color.FromArgb(255, 240, 76, 36));
        using var outerFlame = new GraphicsPath();
        outerFlame.AddBezier(new PointF(31, 10), new PointF(17, 21), new PointF(15, 36), new PointF(24, 48));
        outerFlame.AddBezier(new PointF(24, 48), new PointF(32, 58), new PointF(47, 52), new PointF(49, 39));
        outerFlame.AddBezier(new PointF(49, 39), new PointF(51, 27), new PointF(43, 16), new PointF(31, 10));
        graphics.FillPath(emberBrush, outerFlame);

        using var coreBrush = new SolidBrush(Color.FromArgb(255, 255, 246, 217));
        using var innerFlame = new GraphicsPath();
        innerFlame.AddBezier(new PointF(32, 19), new PointF(24, 26), new PointF(24, 36), new PointF(29, 43));
        innerFlame.AddBezier(new PointF(29, 43), new PointF(33, 47), new PointF(41, 44), new PointF(42, 37));
        innerFlame.AddBezier(new PointF(42, 37), new PointF(43, 30), new PointF(38, 23), new PointF(32, 19));
        graphics.FillPath(coreBrush, innerFlame);

        using var strokePen = new Pen(Color.FromArgb(140, 255, 255, 255), 2);
        graphics.DrawPath(strokePen, outerFlame);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Icon.FromHandle(handle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
