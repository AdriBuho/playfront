using System;
using System.Runtime.InteropServices;

namespace Playfront.App.Input;

/// <summary>
/// Swaps the mouse pointer Windows draws, so that while Playfront is driving an outside program
/// (Spotify) the pointer looks like the console's instead of the ordinary arrow.
///
/// ⚠ THIS IS SYSTEM-WIDE AND IT OUTLIVES THE PROCESS. SetSystemCursor changes the pointer for every
/// application on the desktop, and Windows keeps the change until something restores it or the user
/// signs out. If Playfront dies without restoring, the user is left with our pointer everywhere.
/// That is the same class of damage as leaving a machine without a shell, so it gets the same
/// treatment - three independent ways back:
///
///   1. <see cref="Restore"/> whenever pointer mode ends, by any route.
///   2. <see cref="Restore"/> from a process-exit handler and from the unhandled-exception handler.
///   3. <see cref="Restore"/> unconditionally ON EVERY STARTUP. Costs nothing when there is nothing
///      to repair, and it silently fixes a machine left broken by a previous crash. This is the one
///      that actually saves the user, because the first two cannot run if the process is killed.
///
/// Restoring does NOT put back a copy we saved: SPI_SETCURSORS makes Windows reload the user's own
/// cursor scheme from the registry, which is correct even if they had a custom one.
///
/// The pointer itself comes from the SAME vector the app draws (GamepadCursor), rendered to a
/// bitmap here. One source of truth, and it can be rendered at whatever size the screen needs.
/// </summary>
public static class SystemCursor
{
    // The pointers worth replacing. A normal arrow and the "busy" variants; the text caret and the
    // resize handles are deliberately left alone, because they tell the user something our single
    // drawing cannot.
    private static readonly uint[] Replaced = { OcrNormal, OcrHand, OcrAppStarting };

    private const uint OcrNormal = 32512;
    private const uint OcrHand = 32649;
    private const uint OcrAppStarting = 32650;

    private const uint SpiSetCursors = 0x0057;

    private static bool _applied;

    /// <summary>True while our pointer is installed system-wide.</summary>
    public static bool Applied => _applied;

    // The pointer, in the box it is drawn into. Measured off a recording of a real PS5 browser: the
    // console's cursor is 38 x 54 with a 3 px outline, about three times the Windows arrow. The
    // shape is inset by 2 so the outline has room, which is why the tip - the hot spot - is at (2,2)
    // rather than the corner.
    private const int Width = 36;
    private const int Height = 55;
    private const int HotX = 2;
    private const int HotY = 2;
    private const float Outline = 3f;

    private static readonly (float X, float Y)[] Arrow =
    {
        (2f, 2f),       // tip
        (2f, 45.5f),    // down the left edge
        (12.8f, 35f),   // into the notch
        (19.7f, 51.2f), // out along the tail
        (26.6f, 48.1f),
        (19.7f, 32f),
        (32.5f, 32f),   // the wide corner; closing from here gives the 45-degree right edge
    };

    /// <summary>
    /// Draws the pointer and installs it as the system cursor.
    ///
    /// Drawn with GDI+ rather than by rendering an Avalonia control, and that is not a preference -
    /// the Avalonia route did not work. RenderTargetBitmap returns a fully transparent bitmap for a
    /// control that is hidden, and also for one that is not attached to a window, which between them
    /// covers every way of keeping a drawing-only control around. The symptom was a solid black
    /// rectangle on screen: transparent pixels plus an opaque mask paint the whole box. A cursor is a
    /// Win32 bitmap in the end, so it is simpler to draw it as one.
    /// </summary>
    public static void Apply()
    {
        try
        {
            var pixels = DrawArrow();
            var hCursor = BuildCursor(pixels, Width, Height, HotX, HotY);
            if (hCursor == IntPtr.Zero)
            {
                return;
            }

            foreach (var id in Replaced)
            {
                // SetSystemCursor TAKES OWNERSHIP of the handle and destroys it, so each id needs its
                // own copy. Passing the same handle twice frees it once and leaves the rest dangling.
                var copia = CopyIcon(hCursor);
                if (copia != IntPtr.Zero)
                {
                    SetSystemCursor(copia, id);
                }
            }

            DestroyIcon(hCursor);
            _applied = true;
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not install the gamepad pointer", e);
            Restore();
        }
    }

    /// <summary>
    /// Puts the user's own pointers back. Safe to call at any time, including when nothing was
    /// changed - which is exactly why it runs on every startup.
    /// </summary>
    public static void Restore()
    {
        try
        {
            SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not restore the system cursors", e);
        }
        finally
        {
            _applied = false;
        }
    }

    /// <summary>
    /// Draws the arrow into a premultiplied BGRA buffer: black body, white outline, transparent
    /// elsewhere.
    ///
    /// Rasterised by hand rather than with a drawing library, which keeps this free of any new
    /// dependency for what is, in the end, a seven-sided polygon. Each pixel is decided by its
    /// SIGNED DISTANCE to the outline: inside and far from the edge is body, within half the stroke
    /// either side is outline, beyond that is nothing. Sampled on a 4x4 grid per pixel, which is
    /// where the smooth edges come from - a cursor with stair-stepped edges would look worse than
    /// the plain Windows one.
    /// </summary>
    private static byte[] DrawArrow()
    {
        const int Sub = 4;                    // 4 x 4 samples per pixel
        const float Half = Outline / 2f;
        var pixels = new byte[Width * Height * 4];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                float body = 0, edge = 0;

                for (var sy = 0; sy < Sub; sy++)
                {
                    for (var sx = 0; sx < Sub; sx++)
                    {
                        var px = x + (sx + 0.5f) / Sub;
                        var py = y + (sy + 0.5f) / Sub;

                        var dist = DistanceToOutline(px, py);
                        var inside = Inside(px, py);

                        if (dist <= Half)
                        {
                            edge++;          // straddling the outline, either side of it
                        }
                        else if (inside)
                        {
                            body++;
                        }
                    }
                }

                var total = Sub * Sub;
                var aEdge = edge / total;
                var aBody = body / total;
                var alpha = aEdge + aBody;
                if (alpha <= 0)
                {
                    continue;               // left transparent
                }

                // White where the outline is, black where the body is, mixed on the boundary.
                // PREMULTIPLIED, which is what a 32-bit cursor is blended as: the colour channels
                // are already scaled by their own coverage.
                var white = (byte)Math.Clamp(aEdge * 255f, 0, 255);
                var a = (byte)Math.Clamp(alpha * 255f, 0, 255);

                var i = (y * Width + x) * 4;
                pixels[i + 0] = white;      // B
                pixels[i + 1] = white;      // G
                pixels[i + 2] = white;      // R
                pixels[i + 3] = a;          // A
            }
        }

        return pixels;
    }

    // Whether a point falls inside the polygon (ray casting).
    private static bool Inside(float px, float py)
    {
        var inside = false;
        for (int i = 0, j = Arrow.Length - 1; i < Arrow.Length; j = i++)
        {
            var (xi, yi) = Arrow[i];
            var (xj, yj) = Arrow[j];
            if (yi > py != yj > py &&
                px < (xj - xi) * (py - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    // Distance from a point to the nearest edge of the polygon, sign ignored: the outline is drawn
    // on both sides of the boundary, so only how far away it is matters.
    private static float DistanceToOutline(float px, float py)
    {
        var best = float.MaxValue;
        for (int i = 0, j = Arrow.Length - 1; i < Arrow.Length; j = i++)
        {
            best = Math.Min(best, DistanceToSegment(px, py, Arrow[j], Arrow[i]));
        }

        return best;
    }

    private static float DistanceToSegment(float px, float py, (float X, float Y) a, (float X, float Y) b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var wx = px - a.X;
        var wy = py - a.Y;

        var len2 = vx * vx + vy * vy;
        var t = len2 <= 0 ? 0 : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);

        var dx = px - (a.X + t * vx);
        var dy = py - (a.Y + t * vy);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // Builds an HCURSOR from raw BGRA pixels. CreateIconIndirect is used rather than writing a .cur
    // file: no temporary file, and the size can follow the screen instead of being baked in.
    //
    // THE COLOUR BITMAP MUST BE A DIB SECTION, not a plain CreateBitmap. CreateBitmap makes a
    // DEVICE-DEPENDENT bitmap, and Windows throws the alpha channel away on those - the cursor comes
    // out as a solid black rectangle the full size of the control, which is exactly what happened.
    // A DIB section keeps the 32nd bit per pixel and the shape appears.
    private static IntPtr BuildCursor(byte[] bgra, int w, int h, int hotX, int hotY)
    {
        var color = CreateArgbBitmap(bgra, w, h);
        if (color == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // A monochrome mask is still required even for a 32-bit cursor. All zeros is right: the
        // alpha channel of the colour bitmap does the shaping.
        var mask = CreateBitmap(w, h, 1, 1, new byte[((w + 15) / 16) * 2 * h]);
        if (mask == IntPtr.Zero)
        {
            DeleteObject(color);
            return IntPtr.Zero;
        }

        var info = new IconInfo
        {
            IsIcon = false, // false = cursor, and only then are the hotspot fields read
            HotspotX = hotX,
            HotspotY = hotY,
            Mask = mask,
            Color = color,
        };

        var h1 = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return h1;
    }

    // A top-down 32-bit DIB section, which is the only shape of bitmap whose alpha survives into a
    // cursor. Negative height = top-down, matching the order the renderer hands the pixels over; with
    // a positive height the pointer comes out upside down.
    private static IntPtr CreateArgbBitmap(byte[] bgra, int w, int h)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h,
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            },
        };

        var bmp = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(bgra, 0, bits, bgra.Length);
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pointer, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);
}
