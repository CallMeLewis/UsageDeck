using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CodexBarWin.App;

internal static class WindowSizing
{
    private const double DefaultDpi = 96;
    private const double WorkAreaMargin = 16;

    public static void Configure(
        Window window,
        double initialWidth,
        double initialHeight,
        double minimumWidth,
        double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(window);

        double scale = GetScale(window);
        (int maximumWidth, int maximumHeight) = GetMaximumSize(window, scale);
        window.AppWindow.Resize(new SizeInt32(
            Math.Min(ToPixels(initialWidth, scale), maximumWidth),
            Math.Min(ToPixels(initialHeight, scale), maximumHeight)));
        UpdateMinimumSize(window, minimumWidth, minimumHeight);
    }

    public static void UpdateMinimumSize(Window window, double minimumWidth, double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        double scale = GetScale(window);
        (int maximumWidth, int maximumHeight) = GetMaximumSize(window, scale);
        presenter.PreferredMinimumWidth = Math.Min(ToPixels(minimumWidth, scale), maximumWidth);
        presenter.PreferredMinimumHeight = Math.Min(ToPixels(minimumHeight, scale), maximumHeight);
    }

    private static (int Width, int Height) GetMaximumSize(Window window, double scale)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(
            window.AppWindow.Id,
            DisplayAreaFallback.Primary);
        int margin = ToPixels(WorkAreaMargin, scale);
        return (
            Math.Max(1, displayArea.WorkArea.Width - (margin * 2)),
            Math.Max(1, displayArea.WorkArea.Height - (margin * 2)));
    }

    private static double GetScale(Window window)
    {
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        uint dpi = GetDpiForWindow(windowHandle);
        return dpi == 0 ? 1 : dpi / DefaultDpi;
    }

    internal static int ToPixels(double value, double scale) =>
        Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
