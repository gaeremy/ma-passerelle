// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using MaPasserelle.Capture;

namespace MaPasserelle.App;

/// <summary>Image réduite d'une fenêtre ou d'un écran, en pixels BGRA opaques.</summary>
public sealed record ThumbnailPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// Miniatures pour la grille de choix. Une fenêtre est dessinée par PrintWindow (y compris si elle est
/// recouverte), un écran est copié tel qu'affiché. Une fenêtre réduite n'a pas d'image : null.
/// </summary>
public static class Thumbnails
{
    public static ThumbnailPixels? OfWindow(nint window, int maxWidth, int maxHeight)
    {
        if (IsIconic(window) || !GetWindowRect(window, out Rect r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 8 || height < 8) return null;
        return Render(width, height, maxWidth, maxHeight, dc => PrintWindow(window, dc, 2 /* PW_RENDERFULLCONTENT */));
    }

    public static ThumbnailPixels? OfScreen(ScreenInfo screen, int maxWidth, int maxHeight) =>
        Render(screen.Width, screen.Height, maxWidth, maxHeight, dc =>
        {
            nint desktop = GetDC(0);
            try { return BitBlt(dc, 0, 0, screen.Width, screen.Height, desktop, screen.X, screen.Y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */); }
            finally { ReleaseDC(0, desktop); }
        });

    private static ThumbnailPixels? Render(int width, int height, int maxWidth, int maxHeight, Func<nint, bool> draw)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));

        nint desktop = GetDC(0);
        nint fullDc = CreateCompatibleDC(desktop), fullBitmap = CreateCompatibleBitmap(desktop, width, height);
        nint smallDc = CreateCompatibleDC(desktop), smallBitmap = CreateCompatibleBitmap(desktop, w, h);
        nint oldFull = SelectObject(fullDc, fullBitmap), oldSmall = SelectObject(smallDc, smallBitmap);
        try
        {
            if (!draw(fullDc)) return null;
            SetStretchBltMode(smallDc, 4 /* HALFTONE */);
            SetBrushOrgEx(smallDc, 0, 0, 0);
            StretchBlt(smallDc, 0, 0, w, h, fullDc, 0, 0, width, height, 0x00CC0020);
            SelectObject(smallDc, oldSmall); // GetDIBits exige un bitmap désélectionné

            var header = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[w * h * 4];
            if (GetDIBits(smallDc, smallBitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI laisse l'alpha à 0
            return new ThumbnailPixels(pixels, w, h);
        }
        finally
        {
            SelectObject(fullDc, oldFull);
            SelectObject(smallDc, oldSmall);
            DeleteObject(fullBitmap);
            DeleteObject(smallBitmap);
            DeleteDC(fullDc);
            DeleteDC(smallDc);
            ReleaseDC(0, desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
