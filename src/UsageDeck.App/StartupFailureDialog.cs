using System.Runtime.InteropServices;

namespace UsageDeck.App;

internal static class StartupFailureDialog
{
    private const uint ErrorIcon = 0x00000010;
    private const uint OkButton = 0x00000000;

    public static void Show(int errorCode, string? diagnosticPath)
    {
        string diagnosticText = diagnosticPath is null
            ? "UsageDeck could not save a diagnostic report. Include the error code when reporting this problem."
            : "A diagnostic report was saved to:\n\n"
                + diagnosticPath
                + "\n\nThe report contains startup details only. It does not include saved credentials or provider responses.";
        string message =
            "UsageDeck could not start its Windows interface.\n\n"
            + $"Error: 0x{errorCode:X8}\n\n"
            + diagnosticText;

        _ = MessageBox(nint.Zero, message, "UsageDeck couldn't start", OkButton | ErrorIcon);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
