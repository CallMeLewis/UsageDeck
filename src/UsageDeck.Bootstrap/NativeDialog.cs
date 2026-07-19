using System.Runtime.InteropServices;

namespace UsageDeck.Bootstrap;

internal static class NativeDialog
{
    private const uint ErrorIcon = 0x00000010;
    private const uint OkButton = 0x00000000;
    private const uint RetryCancelButtons = 0x00000005;
    private const int RetryResult = 4;

    public static bool ShowRuntimeFailure(int errorCode)
    {
        string message =
            "UsageDeck could not prepare a Microsoft component required to start the app and show notifications.\n\n"
            + $"Error: 0x{errorCode:X8}\n\n"
            + "Select Retry to try again, or Cancel to close UsageDeck.";
        return MessageBox(nint.Zero, message, "UsageDeck setup", RetryCancelButtons | ErrorIcon) == RetryResult;
    }

    public static void ShowLaunchFailure(int errorCode, string? diagnosticPath = null)
    {
        string diagnosticText = diagnosticPath is null
            ? "No startup report was created. Include this error code when reporting the problem."
            : "A startup report may be available at:\n\n" + diagnosticPath;
        string message =
            "UsageDeck was prepared, but its Windows interface could not be started.\n\n"
            + $"Error: 0x{errorCode:X8}\n\n"
            + diagnosticText;
        _ = MessageBox(nint.Zero, message, "UsageDeck couldn't start", OkButton | ErrorIcon);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
