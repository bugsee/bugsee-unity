using System;
using Bugsee.Contracts.Capture;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Blanks secure rectangles in a pushed RGBA frame. The SDK does not mask frames pushed through DirectBuffers
    /// (only blackout is honoured), so the pusher has to, using the rectangles registered with
    /// Bugsee.AddSecureRectangle.
    /// </summary>
    /// <summary>Where the captured view sits in the window, in the pixels secure rectangles are registered in.</summary>
    public readonly struct SourceRegion
    {
        public readonly int Left, Top, Width, Height;
        public SourceRegion(int left, int top, int width, int height) { Left = left; Top = top; Width = width; Height = height; }
        public bool IsKnown => Width > 0 && Height > 0;
    }

    public static class FrameRedaction
    {
        /// <summary>
        /// Fills each rectangle with opaque black. <paramref name="rects"/> holds <paramref name="count"/> rectangles as
        /// [l, t, r, b, ...] in the native window pixels they were registered in (top-left origin); they are scaled
        /// from <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> to the frame, and the rows are
        /// mirrored when the buffer is bottom-up. Rounds outward (floor/ceil) so a mask never shrinks. Returns the
        /// number of rectangles that touched the frame. No allocations.
        /// </summary>
        public static int BlankRects(
            Span<byte> rgba, int width, int height, VideoFrameRowOrder order,
            int[] rects, int count, int sourceWidth, int sourceHeight)
        {
            return BlankRects(rgba, width, height, order, rects, count, new SourceRegion(0, 0, sourceWidth, sourceHeight));
        }

        /// <summary>
        /// Same, for a frame that shows only part of the window: <paramref name="region"/> is where the captured view sits
        /// in the window (offset and size, in the pixels the rectangles are registered in). Rectangles are shifted by
        /// the offset before they are scaled.
        /// </summary>
        public static int BlankRects(
            Span<byte> rgba, int width, int height, VideoFrameRowOrder order,
            int[] rects, int count, SourceRegion region)
        {
            int sourceWidth = region.Width, sourceHeight = region.Height;
            if (rects == null || count <= 0 || rects.Length < count * 4) return 0;
            if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return 0;
            if (rgba.Length < (long)width * height * FramePushPolicy.PixelStride) return 0;

            int applied = 0;
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                int x0 = Clamp(FloorDiv(((long)rects[o] - region.Left) * width, sourceWidth), 0, width);
                int y0 = Clamp(FloorDiv(((long)rects[o + 1] - region.Top) * height, sourceHeight), 0, height);
                int x1 = Clamp(CeilDiv(((long)rects[o + 2] - region.Left) * width, sourceWidth), 0, width);
                int y1 = Clamp(CeilDiv(((long)rects[o + 3] - region.Top) * height, sourceHeight), 0, height);
                if (x1 <= x0 || y1 <= y0) continue;

                for (int y = y0; y < y1; y++)
                {
                    int row = order == VideoFrameRowOrder.BottomUp ? height - 1 - y : y;
                    FillBlack(rgba, (row * width + x0) * FramePushPolicy.PixelStride, x1 - x0);
                }
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// Blanks the union of the rectangles registered when the capture started and those registered when the pixels
        /// arrived, so a rectangle removed or moved in between never unmasks something that was secure at capture time. Each set is
        /// scaled with the native window size of its own moment: after a rotation or resize the live rectangles are in the new pixel space.
        /// </summary>
        public static int BlankRequestAndLive(
            Span<byte> rgba, int width, int height, VideoFrameRowOrder order,
            int[] requestRects, int requestCount, int requestSourceWidth, int requestSourceHeight,
            int[] liveRects, int liveCount, int liveSourceWidth, int liveSourceHeight)
        {
            return BlankRects(rgba, width, height, order, requestRects, requestCount, requestSourceWidth, requestSourceHeight)
                + BlankRects(rgba, width, height, order, liveRects, liveCount, liveSourceWidth, liveSourceHeight);
        }

        /// <summary>
        /// Masks a frame: the rectangles registered when the capture started (mapped with the view position of that
        /// moment) and those registered when the pixels arrived (mapped with the position of now). Fails closed: if a
        /// set has rectangles but its view position is unknown, the whole frame is blanked. Returns true when the
        /// whole frame was blanked.
        /// </summary>
        public static bool MaskFrame(
            Span<byte> rgba, int width, int height, VideoFrameRowOrder order,
            int[] requestRects, int requestCount, SourceRegion requestRegion,
            int[] liveRects, int liveCount, SourceRegion liveRegion)
        {
            if ((requestCount > 0 && !requestRegion.IsKnown) || (liveCount > 0 && !liveRegion.IsKnown))
            {
                BlankAll(rgba, width, height);
                return true;
            }
            BlankRects(rgba, width, height, order, requestRects, requestCount, requestRegion);
            BlankRects(rgba, width, height, order, liveRects, liveCount, liveRegion);
            return false;
        }

        /// <summary>Fills the whole frame with opaque black: the fail-closed choice when the row order is unverified and a secure rectangle is registered.</summary>
        public static void BlankAll(Span<byte> rgba, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            long pixels = (long)width * height;
            if (rgba.Length < pixels * FramePushPolicy.PixelStride) return;
            FillBlack(rgba, 0, (int)pixels);
        }

        static void FillBlack(Span<byte> rgba, int start, int pixels)
        {
            int p = start;
            for (int x = 0; x < pixels; x++)
            {
                rgba[p] = 0;
                rgba[p + 1] = 0;
                rgba[p + 2] = 0;
                rgba[p + 3] = 255;
                p += FramePushPolicy.PixelStride;
            }
        }

        // Exact integer scaling: a floating-point product such as 60 * 0.1 = 6.000000000000001 would round up a row.
        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

        static int Clamp(long v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : (int)v);

    }
}
