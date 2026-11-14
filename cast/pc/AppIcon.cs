// The app's icon, drawn in code (no image files in the repository): a rounded blue-violet
// square with a white TV and cast waves. Used for the exe and window (build.ps1 compiles this
// file alone with /define:ICONTOOL, runs it to write build\app.ico, then embeds that) and for
// the iPad home-screen icon (WebCast.Icon()).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace TVCast
{
    static class AppIcon
    {
        /// <summary>sizes in the .ico: what Windows asks for at 100-200 % scaling</summary>
        internal static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

        static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>the icon at size x size; square = full-bleed background (iOS rounds the
        /// corners itself), otherwise a rounded square with transparent corners</summary>
        internal static Bitmap Render(int size, bool square)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size;
                RectangleF all = new RectangleF(0, 0, s, s);
                using (LinearGradientBrush bg = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                    Color.FromArgb(56, 132, 255), Color.FromArgb(124, 77, 255)))
                {
                    if (square) g.FillRectangle(bg, all);
                    else using (GraphicsPath p = Round(new RectangleF(0, 0, s - 0.01f, s - 0.01f), s * 0.225f)) g.FillPath(bg, p);
                }
                // a faint light from the top, so it does not look flat
                // (over the whole height: a brush shorter than the shape repeats and leaves a seam)
                using (LinearGradientBrush shine = new LinearGradientBrush(new PointF(0, -1), new PointF(0, s + 1),
                    Color.White, Color.White))
                {
                    ColorBlend fade = new ColorBlend();
                    fade.Colors = new[] { Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                    fade.Positions = new[] { 0f, 0.6f, 1f };
                    shine.InterpolationColors = fade;
                    if (square) g.FillRectangle(shine, all);
                    else using (GraphicsPath p = Round(new RectangleF(0, 0, s - 0.01f, s - 0.01f), s * 0.225f)) g.FillPath(shine, p);
                }

                // thin lines vanish below 24 px: thicker strokes and no waves there
                bool small = size < 32;
                float w = small ? Math.Max(1.4f, s * 0.085f) : s * 0.062f;
                using (Pen pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    RectangleF screen = RectangleF.FromLTRB(s * 0.19f, s * 0.24f, s * 0.81f, s * 0.66f);
                    using (GraphicsPath p = Round(screen, s * 0.07f)) g.DrawPath(pen, p);
                    g.DrawLine(pen, s * 0.5f, s * 0.66f + w / 2, s * 0.5f, s * 0.76f);
                    g.DrawLine(pen, s * 0.36f, s * 0.77f, s * 0.64f, s * 0.77f);
                    if (!small)
                    {
                        // the cast mark: a dot and two waves rising from the screen's lower left corner
                        PointF c = new PointF(s * 0.30f, s * 0.555f);
                        pen.Width = s * 0.05f;
                        foreach (float r in new[] { s * 0.11f, s * 0.20f })
                            g.DrawArc(pen, c.X - r, c.Y - r, r * 2, r * 2, 270, 90);
                        float dr = s * 0.035f;
                        using (SolidBrush white = new SolidBrush(Color.White)) g.FillEllipse(white, c.X - dr, c.Y - dr, dr * 2, dr * 2);
                    }
                }
            }
            return bmp;
        }

        internal static byte[] Png(int size, bool square)
        {
            using (Bitmap b = Render(size, square))
            using (MemoryStream m = new MemoryStream())
            {
                b.Save(m, System.Drawing.Imaging.ImageFormat.Png);
                return m.ToArray();
            }
        }

        /// <summary>one icon image as a 32-bit DIB (BITMAPINFOHEADER, bottom-up BGRA, AND mask):
        /// .NET Framework's Icon class cannot read PNG images below 256 px</summary>
        static byte[] Dib(int size)
        {
            using (Bitmap b = Render(size, false))
            using (MemoryStream m = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(m))
            {
                int maskRow = ((size + 31) / 32) * 4;
                w.Write(40); w.Write(size); w.Write(size * 2); // header size, width, height (image + mask)
                w.Write((short)1); w.Write((short)32); w.Write(0); // planes, bpp, BI_RGB
                w.Write(size * size * 4 + maskRow * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskRow * size]); // all 0: the alpha channel decides
                w.Flush();
                return m.ToArray();
            }
        }

        /// <summary>a .ico: DIB images, PNG for 256 px (as Windows' own icons)</summary>
        internal static byte[] Ico(int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (int z in sizes) images.Add(z >= 256 ? Png(z, false) : Dib(z));
            using (MemoryStream m = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(m))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); // ICONDIR: reserved, type icon, count
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // 0 = 256
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);       // colours, reserved, planes, bpp
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] img in images) w.Write(img);
                w.Flush();
                return m.ToArray();
            }
        }

#if ICONTOOL
        static int Main(string[] args)
        {
            File.WriteAllBytes(args[0], Ico(IcoSizes));
            if (args.Length > 1) using (Bitmap b = Render(256, false)) b.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
#endif
    }
}
