namespace BenchBar.Tray.Core;

/// <summary>
/// Where a panel goes on a monitor, in physical pixels. Device independent units cannot place a window across monitors of
/// different scale: the window takes its units from the DPI of the monitor it is on, not the one the maths assumed.
/// </summary>
public static class PanelPlacement
{
    /// <summary>A size in device independent units (96 per inch) as physical pixels at <paramref name="dpi"/>.</summary>
    public static int ToPixels(double dip, uint dpi) => (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0);

    /// <summary>The top left that centres a <paramref name="width"/> x <paramref name="height"/> pixel window in the work area, never above or left of it.</summary>
    public static (int X, int Y) Centre(int workLeft, int workTop, int workRight, int workBottom, int width, int height) =>
        (Math.Max(workLeft, workLeft + (workRight - workLeft - width) / 2),
         Math.Max(workTop, workTop + (workBottom - workTop - height) / 2));
}
