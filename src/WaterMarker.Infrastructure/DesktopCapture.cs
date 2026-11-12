using System.ComponentModel;
using System.Runtime.InteropServices;
using WaterMarker.Core;

namespace WaterMarker.Infrastructure;

public sealed record DesktopSnapshot(ScreenPixels Pixels, IReadOnlyList<PixelRect> Monitors, IReadOnlyList<int?> ScalePercents);

public static class DesktopCapture
{
    public static DesktopSnapshot Capture()
    {
        var previous = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new IOException("Physical screen coordinates are unavailable.");
        try
        {
            var monitors = new List<PixelRect>();
            var scales = new List<int?>();
            Native.MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) =>
            {
                monitors.Add(rect.ToPixels());
                scales.Add(Native.GetScaleFactorForMonitor(monitor, out var scale) == 0 ? scale : null);
                return true;
            };
            if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var left = monitors.Min(m => m.Left);
            var top = monitors.Min(m => m.Top);
            var bounds = new PixelRect(left, top, monitors.Max(m => m.Right) - left, monitors.Max(m => m.Bottom) - top);
            if ((long)bounds.Width * bounds.Height > 100_000_000)
                throw new NotSupportedException("The desktop is too large to capture (maximum 100 megapixels).");
            var distinct = Enumerable.Range(0, monitors.Count).DistinctBy(i => monitors[i]).ToArray();
            return new(CapturePixels(bounds), distinct.Select(i => monitors[i]).ToArray(), distinct.Select(i => scales[i]).ToArray());
        }
        catch (Win32Exception ex) { throw new IOException("Screen capture failed. Try again.", ex); }
        finally { if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous); }
    }

    private static ScreenPixels CapturePixels(PixelRect bounds)
    {
        var desktop = Native.GetDC(IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(desktop);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new Native.BitmapInfo { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
            bitmap = Native.CreateDIBSection(desktop, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            old = Native.SelectObject(memory, bitmap);
            if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // One transfer freezes all monitors before any selection window exists.
            if (!Native.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, desktop, bounds.Left, bounds.Top, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.GdiFlush();
            var bytes = new byte[checked(bounds.Width * bounds.Height * 4)];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            // GDI's fourth channel is reserved, not an alpha channel.
            for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new(bounds, bytes);
        }
        finally
        {
            if (old != IntPtr.Zero && old != new IntPtr(-1)) Native.SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    public static PixelPoint CursorPosition()
    {
        if (!Native.GetPhysicalCursorPos(out var point)) throw new IOException("Could not read the screen pointer position.");
        return new(point.X, point.Y);
    }

    public static void WaitForComposition()
    {
        if (Native.DwmFlush() < 0) throw new IOException("Screen capture failed. Try again.");
    }
    public static void PositionOverlay(IntPtr window, PixelRect bounds)
    {
        if (!Native.SetWindowPos(window, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010))
            throw new IOException("Could not position the screen selector.");
    }

    public static PixelRect WorkArea(IntPtr window)
    {
        var monitor = Native.MonitorFromWindow(window, 2);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) throw new IOException("Could not read monitor bounds.");
        return info.Work.ToPixels();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal readonly PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { internal int Size; internal Rect Monitor, Work; internal uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, SizeImage;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ClrUsed, ClrImportant, Color;
        }
        internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("shcore.dll")] internal static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    }
}
