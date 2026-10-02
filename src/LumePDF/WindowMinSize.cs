using System.Runtime.InteropServices;

namespace LumePDF;

/// <summary>
/// Enforces a minimum window size through WM_GETMINMAXINFO. OverlappedPresenter's
/// PreferredMinimumWidth/Height is not honored for unpackaged windows, which let the
/// header controls slide under the caption buttons.
/// </summary>
internal static unsafe partial class WindowMinSize
{
    const uint WM_GETMINMAXINFO = 0x0024;

    static int _minWidth, _minHeight; // in DIPs

    public static void Apply(nint hwnd, int minWidth, int minHeight)
    {
        _minWidth = minWidth;
        _minHeight = minHeight;
        SetWindowSubclass(hwnd, &SubclassProc, 1, 0);
    }

    [UnmanagedCallersOnly]
    static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        nint result = DefSubclassProc(hwnd, msg, wParam, lParam);
        if (msg == WM_GETMINMAXINFO)
        {
            uint dpi = GetDpiForWindow(hwnd);
            // MINMAXINFO: ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize (POINT = 2 x int)
            int* minTrack = (int*)(lParam + 3 * 8);
            minTrack[0] = (int)(_minWidth * dpi / 96);
            minTrack[1] = (int)(_minHeight * dpi / 96);
        }
        return result;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
