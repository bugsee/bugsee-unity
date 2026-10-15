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
        /// Fills each rectangle with opaque black. <paramref name="packedSnapshot"/> is the output of
        /// SecureRectRegistry.Snapshot ([version, count, l, t, r, b, ...]) in screen pixels with a top-left origin;
        /// it is scaled from <paramref name="screenWidth"/> x <paramref name="screenHeight"/> to the frame, and the
        /// rows are mirrored when the buffer is bottom-up. Rounds outward (floor/ceil) so a mask never shrinks.
        /// Returns the number of rectangles that touched the frame. No allocations.
        /// </summary>
        public static int BlankRects(
            byte[] rgba, int width, int height, VideoFrameRowOrder order,
            int[] packedSnapshot, int screenWidth, int screenHeight)
        {
            if (rgba == null || packedSnapshot == null || packedSnapshot.Length < 2) return 0;
            if (width <= 0 || height <= 0 || screenWidth <= 0 || screenHeight <= 0) return 0;
            if (rgba.LongLength < (long)width * height * FramePushPolicy.PixelStride) return 0;

            int count = packedSnapshot[1];
            if (count < 0 || packedSnapshot.Length < 2 + count * 4) return 0;

            double sx = (double)width / screenWidth;
            double sy = (double)height / screenHeight;
            int applied = 0;
            for (int i = 0; i < count; i++)
            {
                int o = 2 + i * 4;
                int x0 = Clamp((int)System.Math.Floor(packedSnapshot[o] * sx), 0, width);
                int y0 = Clamp((int)System.Math.Floor(packedSnapshot[o + 1] * sy), 0, height);
                int x1 = Clamp((int)System.Math.Ceiling(packedSnapshot[o + 2] * sx), 0, width);
                int y1 = Clamp((int)System.Math.Ceiling(packedSnapshot[o + 3] * sy), 0, height);
                if (x1 <= x0 || y1 <= y0) continue;

                for (int y = y0; y < y1; y++)
                {
                    int row = order == VideoFrameRowOrder.BottomUp ? height - 1 - y : y;
                    int p = (row * width + x0) * FramePushPolicy.PixelStride;
                    for (int x = x0; x < x1; x++)
                    {
                        rgba[p] = 0;
                        rgba[p + 1] = 0;
                        rgba[p + 2] = 0;
                        rgba[p + 3] = 255;
                        p += FramePushPolicy.PixelStride;
                    }
                }
                applied++;
            }
            return applied;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
