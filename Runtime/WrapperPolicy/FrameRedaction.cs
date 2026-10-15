using System;
using Bugsee.Contracts.Capture;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Blanks secure rectangles in a pushed RGBA frame. The SDK does not mask frames pushed through DirectBuffers
    /// (only blackout is honoured), so the pusher has to, using the rectangles registered with
    /// Bugsee.AddSecureRectangle.
    /// </summary>
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
            if (rects == null || count <= 0 || rects.Length < count * 4) return 0;
            if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return 0;
            if (rgba.Length < (long)width * height * FramePushPolicy.PixelStride) return 0;

            int applied = 0;
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                int x0 = Clamp(FloorDiv((long)rects[o] * width, sourceWidth), 0, width);
                int y0 = Clamp(FloorDiv((long)rects[o + 1] * height, sourceHeight), 0, height);
                int x1 = Clamp(CeilDiv((long)rects[o + 2] * width, sourceWidth), 0, width);
                int y1 = Clamp(CeilDiv((long)rects[o + 3] * height, sourceHeight), 0, height);
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
