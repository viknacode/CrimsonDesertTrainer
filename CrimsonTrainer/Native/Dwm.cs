using System.Runtime.InteropServices;

namespace CrimsonTrainer.Native;

/// <summary>Desktop Window Manager tweaks so the custom-chrome window still gets the native Windows 11 look.</summary>
internal static partial class Dwm
{
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    /// <summary>Dark title-bar colouring for the DWM frame plus rounded corners (Windows 11; ignored on older builds).</summary>
    public static void ApplyDarkRoundedFrame(nint hwnd)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }
}
