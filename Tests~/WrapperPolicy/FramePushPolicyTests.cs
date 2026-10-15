using System.IO;
using Bugsee.Contracts.Capture;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class FramePushPolicyTests
    {
        [Test]
        public void First_frame_is_requested_then_throttled_until_the_interval_passes()
        {
            var p = new FramePushPolicy(targetFps: 5); // 0.2 s
            Assert.That(p.TryBegin(10.0, false), Is.EqualTo(FramePushDecision.Request));
            p.Complete(true);
            Assert.That(p.TryBegin(10.1, false), Is.EqualTo(FramePushDecision.Throttled));
            Assert.That(p.IsDue(10.1), Is.False);
            Assert.That(p.IsDue(10.2), Is.True);
            Assert.That(p.TryBegin(10.2, false), Is.EqualTo(FramePushDecision.Request));
        }

        [Test]
        public void A_frame_while_one_is_in_flight_is_dropped_not_queued()
        {
            var p = new FramePushPolicy(targetFps: 10);
            Assert.That(p.TryBegin(0, false), Is.EqualTo(FramePushDecision.Request));
            Assert.That(p.HasInFlight, Is.True);
            Assert.That(p.TryBegin(1.0, false), Is.EqualTo(FramePushDecision.InFlight));
            p.Complete(true);
            Assert.That(p.HasInFlight, Is.False);
            Assert.That(p.TryBegin(1.0, false), Is.EqualTo(FramePushDecision.Request));
            p.Snapshot(out long req, out long pushed, out _, out long dropped, out _);
            Assert.That((req, pushed, dropped), Is.EqualTo((2L, 1L, 1L)));
        }

        [Test]
        public void Throttle_is_checked_before_in_flight_so_slow_readbacks_do_not_inflate_the_drop_count()
        {
            var p = new FramePushPolicy(targetFps: 1);
            p.TryBegin(0, false);
            Assert.That(p.TryBegin(0.5, false), Is.EqualTo(FramePushDecision.Throttled));
            p.Snapshot(out _, out _, out _, out long dropped, out _);
            Assert.That(dropped, Is.EqualTo(0));
        }

        [Test]
        public void Blackout_starts_nothing_and_does_not_consume_the_interval()
        {
            var p = new FramePushPolicy(targetFps: 5);
            Assert.That(p.TryBegin(0, true), Is.EqualTo(FramePushDecision.Blackout));
            Assert.That(p.HasInFlight, Is.False);
            Assert.That(p.TryBegin(0.001, false), Is.EqualTo(FramePushDecision.Request));
        }

        [Test]
        public void Repeated_failures_disable_capture_for_good_and_a_success_resets_the_streak()
        {
            var p = new FramePushPolicy(targetFps: 30, maxConsecutiveFailures: 3);
            double t = 0;
            for (int i = 0; i < 2; i++) { Assert.That(p.TryBegin(t += 1, false), Is.EqualTo(FramePushDecision.Request)); p.Complete(false); }
            Assert.That(p.TryBegin(t += 1, false), Is.EqualTo(FramePushDecision.Request));
            p.Complete(true);
            for (int i = 0; i < 2; i++) { Assert.That(p.TryBegin(t += 1, false), Is.EqualTo(FramePushDecision.Request)); p.Complete(false); }
            Assert.That(p.IsDisabled, Is.False);
            p.TryBegin(t += 1, false);
            p.Complete(false);
            Assert.That(p.IsDisabled, Is.True);
            Assert.That(p.TryBegin(t += 1, false), Is.EqualTo(FramePushDecision.Disabled));
            Assert.That(p.IsDue(t + 100), Is.False);
        }

        [TestCase(0, 1)]
        [TestCase(-5, 1)]
        [TestCase(1000, 30)]
        public void Fps_is_clamped(int fps, int effective)
        {
            var p = new FramePushPolicy(fps);
            p.TryBegin(0, false);
            p.Complete(true);
            double interval = 1.0 / effective;
            Assert.That(p.IsDue(interval - 1e-6), Is.False);
            Assert.That(p.IsDue(interval + 1e-6), Is.True);
        }

        [TestCase(1080, 2400, 640, 288, 640)]
        [TestCase(2400, 1080, 640, 640, 288)]
        [TestCase(300, 200, 640, 300, 200)]   // never scaled up
        [TestCase(1081, 2401, 640, 288, 640)] // even sizes
        [TestCase(1, 1, 640, 2, 2)]
        public void Capture_size_fits_the_cap_keeps_aspect_and_is_even(int sw, int sh, int cap, int w, int h)
        {
            FramePushPolicy.ComputeCaptureSize(sw, sh, cap, out int cw, out int ch);
            Assert.That((cw, ch), Is.EqualTo((w, h)));
            Assert.That(cw % 2 == 0 && ch % 2 == 0, Is.True);
        }

        [Test]
        public void Frame_bytes_do_not_overflow_for_large_frames()
        {
            Assert.That(FramePushPolicy.FrameBytes(640, 288), Is.EqualTo(640L * 288 * 4));
            Assert.That(FramePushPolicy.FrameBytes(30000, 30000), Is.EqualTo(3_600_000_000L));
        }

        [TestCase("2021.3.45f1", 2021, 3, 45)]
        [TestCase("6000.0.1b2", 6000, 0, 1)]
        [TestCase("2022.3.9f1", 2022, 3, 9)]
        public void Unity_version_parses(string v, int major, int minor, int patch)
        {
            Assert.That(FramePushPolicy.TryParseUnityVersion(v, out int a, out int b, out int c), Is.True);
            Assert.That((a, b, c), Is.EqualTo((major, minor, patch)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("2021.3")]
        [TestCase("abc.def.ghi")]
        public void Bad_unity_version_does_not_parse(string v)
        {
            Assert.That(FramePushPolicy.TryParseUnityVersion(v, out _, out _, out _), Is.False);
        }

        [TestCase(false, false, "2022.3.1f1", false)]  // no async readback support
        [TestCase(true, false, "2019.4.1f1", true)]    // GLES: version does not matter
        [TestCase(true, true, "2021.3.17f1", false)]   // Vulkan crash range
        [TestCase(true, true, "2020.3.48f1", false)]
        [TestCase(true, true, "2021.3.18f1", true)]
        [TestCase(true, true, "2021.3.45f1", true)]
        [TestCase(true, true, "2022.1.0b1", true)]
        [TestCase(true, true, "6000.0.1f1", true)]
        [TestCase(true, true, "2021.2.19f1", false)]
        [TestCase(true, true, "garbage", false)]
        public void Readback_gate(bool supported, bool vulkan, string version, bool expected)
        {
            Assert.That(FramePushPolicy.IsReadbackUsable(supported, vulkan, version), Is.EqualTo(expected));
        }

        [TestCase("2021.3.45f1", false)]
        [TestCase("2022.1.24f1", false)]
        [TestCase("2022.2.0f1", true)]
        [TestCase("2022.3.62f3", true)]
        [TestCase("2023.1.1f1", true)]
        [TestCase("6000.0.84f1", true)]
        [TestCase("garbage", false)]
        [TestCase(null, false)]
        public void Native_direct_byte_buffer_gate(string version, bool expected)
        {
            Assert.That(UnityCapturePolicy.IsSupportedUnityVersion(version), Is.EqualTo(expected));
        }

        [Test]
        public void Gles3_measured_on_unity_6000_is_bottom_up_and_verified()
        {
            Assert.That(FramePushPolicy.ResolveRowOrder("OpenGLES3", null, out bool verified), Is.EqualTo(VideoFrameRowOrder.BottomUp));
            Assert.That(verified, Is.True);
        }

        [TestCase("OpenGLES2")]
        [TestCase("")]
        [TestCase(null)]
        public void Unmeasured_apis_are_unverified(string api)
        {
            FramePushPolicy.ResolveRowOrder(api, null, out bool verified);
            Assert.That(verified, Is.False);
        }

        [Test]
        public void Measured_vulkan_is_top_down_and_verified()
        {
            Assert.That(FramePushPolicy.ResolveRowOrder("Vulkan", null, out bool verified), Is.EqualTo(VideoFrameRowOrder.TopDown));
            Assert.That(verified, Is.True);
        }

        [Test]
        public void An_explicit_override_wins_and_counts_as_verified()
        {
            Assert.That(FramePushPolicy.ResolveRowOrder("OpenGLES2", VideoFrameRowOrder.TopDown, out bool verified), Is.EqualTo(VideoFrameRowOrder.TopDown));
            Assert.That(verified, Is.True);
            Assert.That(FramePushPolicy.ResolveRowOrder("OpenGLES3", VideoFrameRowOrder.TopDown, out _), Is.EqualTo(VideoFrameRowOrder.TopDown));
        }
    }

    public class FrameRedactionTests
    {
        const int W = 4, H = 4;

        static byte[] White(int w = W, int h = H) { var b = new byte[w * h * 4]; for (int i = 0; i < b.Length; i++) b[i] = 255; return b; }
        static bool Blank(byte[] b, int x, int row, int w = W) { int o = (row * w + x) * 4; return b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 0 && b[o + 3] == 255; }
        static bool Untouched(byte[] b, int x, int row, int w = W) { int o = (row * w + x) * 4; return b[o] == 255 && b[o + 1] == 255 && b[o + 2] == 255; }
        static int[] One(int l, int t, int r, int bt) => new[] { l, t, r, bt };

        [Test]
        public void Top_left_rect_blanks_the_top_rows_of_a_top_down_frame()
        {
            var b = White();
            Assert.That(FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, One(0, 0, 2, 1), 1, 4, 4), Is.EqualTo(1));
            Assert.That(Blank(b, 0, 0) && Blank(b, 1, 0), Is.True);
            Assert.That(Untouched(b, 2, 0) && Untouched(b, 0, 1), Is.True);
        }

        [Test]
        public void Same_rect_lands_on_the_last_rows_of_a_bottom_up_frame()
        {
            var b = White();
            FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.BottomUp, One(0, 0, 2, 1), 1, 4, 4);
            Assert.That(Blank(b, 0, H - 1) && Blank(b, 1, H - 1), Is.True);
            Assert.That(Untouched(b, 0, 0), Is.True);
        }

        [Test]
        public void Bottom_up_with_scaling_masks_the_rows_that_show_the_screen_region()
        {
            // 100x200 native window, rect in its top-left corner (10,20)-(50,60); frame is 10x20 (scale 0.1),
            // bottom-up, so screen rows 2..5 are buffer rows 19-5..19-2 = 14..17 and columns 1..4.
            const int fw = 10, fh = 20;
            var b = White(fw, fh);
            Assert.That(FrameRedaction.BlankRects(b, fw, fh, VideoFrameRowOrder.BottomUp, One(10, 20, 50, 60), 1, 100, 200), Is.EqualTo(1));
            for (int row = 0; row < fh; row++)
                for (int x = 0; x < fw; x++)
                {
                    bool inside = row >= 14 && row <= 17 && x >= 1 && x <= 4;
                    Assert.That(Blank(b, x, row, fw), Is.EqualTo(inside), "x=" + x + " row=" + row);
                    Assert.That(Untouched(b, x, row, fw), Is.EqualTo(!inside), "x=" + x + " row=" + row);
                }
        }

        [Test]
        public void Rect_is_scaled_from_window_pixels_and_rounded_outward()
        {
            var b = White();
            // 8x8 window, rect (1,1)-(3,3) -> frame 4x4 scale 0.5 -> floor(0.5)=0 .. ceil(1.5)=2
            FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, One(1, 1, 3, 3), 1, 8, 8);
            Assert.That(Blank(b, 0, 0) && Blank(b, 1, 1), Is.True);
            Assert.That(Untouched(b, 2, 2), Is.True);
        }

        [Test]
        public void Out_of_range_and_degenerate_rects_are_clamped_or_ignored()
        {
            var b = White();
            var rects = new[] { -10, -10, 100, 100, 2, 2, 2, 3, 3, 3, 1, 1 };
            Assert.That(FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, rects, 3, 4, 4), Is.EqualTo(1));
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(Blank(b, x, y), Is.True);
        }

        [Test]
        public void Rect_removed_after_the_request_started_is_still_masked()
        {
            var registry = new SecureRectRegistry();
            registry.Set("a", 0, 0, 0, 2, 1);
            int[] atRequest = null, live = null;
            int requestCount = registry.CopyRects(0, ref atRequest);
            registry.RemoveOwner("a");
            int liveCount = registry.CopyRects(0, ref live);
            var b = White();
            FrameRedaction.BlankRequestAndLive(b, W, H, VideoFrameRowOrder.TopDown, atRequest, requestCount, 4, 4, live, liveCount, 4, 4);
            Assert.That(Blank(b, 0, 0) && Blank(b, 1, 0), Is.True);
        }

        [Test]
        public void Rect_added_after_the_request_started_is_masked_too()
        {
            var registry = new SecureRectRegistry();
            int[] atRequest = null, live = null;
            int requestCount = registry.CopyRects(0, ref atRequest);
            registry.Set("late", 0, 0, 2, 4, 3);
            int liveCount = registry.CopyRects(0, ref live);
            var b = White();
            FrameRedaction.BlankRequestAndLive(b, W, H, VideoFrameRowOrder.TopDown, atRequest, requestCount, 4, 4, live, liveCount, 4, 4);
            Assert.That(Blank(b, 3, 2), Is.True);
            Assert.That(Untouched(b, 0, 0), Is.True);
        }

        [Test]
        public void Rect_moved_after_the_request_started_masks_both_places()
        {
            var registry = new SecureRectRegistry();
            registry.Set("m", 0, 0, 0, 1, 1);
            int[] atRequest = null, live = null;
            int requestCount = registry.CopyRects(0, ref atRequest);
            registry.Set("m", 0, 3, 3, 4, 4);
            int liveCount = registry.CopyRects(0, ref live);
            var b = White();
            FrameRedaction.BlankRequestAndLive(b, W, H, VideoFrameRowOrder.TopDown, atRequest, requestCount, 4, 4, live, liveCount, 4, 4);
            Assert.That(Blank(b, 0, 0) && Blank(b, 3, 3), Is.True);
        }

        [Test]
        public void Rect_added_after_a_rotation_is_mapped_with_the_new_window_size()
        {
            // At request time the window was 8x4 (landscape), after rotating it is 4x8; a rect registered in the new
            // space at (0,4)-(4,8) is the bottom half of the 4x4 frame, not the area the old 8x4 space would give.
            var b = White();
            int[] request = null, live = One(0, 4, 4, 8);
            FrameRedaction.BlankRequestAndLive(b, W, H, VideoFrameRowOrder.TopDown, request, 0, 8, 4, live, 1, 4, 8);
            for (int x = 0; x < W; x++)
            {
                Assert.That(Untouched(b, x, 0) && Untouched(b, x, 1), Is.True, "x=" + x);
                Assert.That(Blank(b, x, 2) && Blank(b, x, 3), Is.True, "x=" + x);
            }
        }

        [Test]
        public void Request_rects_keep_the_request_time_window_size_after_a_rotation()
        {
            var b = White();
            int[] request = One(0, 0, 4, 2); // left half of the top half of the old 8x4 window
            FrameRedaction.BlankRequestAndLive(b, W, H, VideoFrameRowOrder.TopDown, request, 1, 8, 4, null, 0, 4, 8);
            Assert.That(Blank(b, 0, 0) && Blank(b, 1, 1), Is.True);
            Assert.That(Untouched(b, 2, 0) && Untouched(b, 0, 2), Is.True);
        }

        [Test]
        public void View_offset_shifts_the_rect_before_scaling()
        {
            // Window 100x220; the Unity view is 100x200 at (0,20) (status bar above it). A rect at window y 20..60 is the
            // view's top 40 px: frame rows 0..4 of a 10x20 frame, not rows 2..6.
            var b = White(10, 20);
            FrameRedaction.BlankRects(b, 10, 20, VideoFrameRowOrder.TopDown, One(0, 20, 100, 60), 1, new SourceRegion(0, 20, 100, 200));
            for (int row = 0; row < 20; row++)
                Assert.That(Blank(b, 3, row, 10), Is.EqualTo(row < 4), "row " + row);
        }

        [Test]
        public void Rect_outside_the_view_masks_nothing()
        {
            var b = White(10, 20);
            Assert.That(FrameRedaction.BlankRects(b, 10, 20, VideoFrameRowOrder.TopDown, One(0, 0, 100, 20), 1, new SourceRegion(0, 20, 100, 200)), Is.EqualTo(0));
        }

        [Test]
        public void Mask_frame_blanks_everything_when_the_view_position_is_unknown_and_a_rect_is_registered()
        {
            var b = White();
            bool all = FrameRedaction.MaskFrame(b, W, H, VideoFrameRowOrder.TopDown, One(0, 0, 1, 1), 1, default(SourceRegion), null, 0, default(SourceRegion));
            Assert.That(all, Is.True);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(Blank(b, x, y), Is.True);

            var b2 = White();
            Assert.That(FrameRedaction.MaskFrame(b2, W, H, VideoFrameRowOrder.TopDown, null, 0, default(SourceRegion), null, 0, default(SourceRegion)), Is.False);
            Assert.That(Untouched(b2, 0, 0), Is.True, "no rectangles: an unknown position is irrelevant");

            var b3 = White();
            Assert.That(FrameRedaction.MaskFrame(b3, W, H, VideoFrameRowOrder.TopDown, null, 0, default(SourceRegion), One(0, 0, 2, 1), 1, default(SourceRegion)), Is.True);
        }

        [Test]
        public void Mask_frame_maps_each_set_with_its_own_region()
        {
            var b = White();
            FrameRedaction.MaskFrame(b, W, H, VideoFrameRowOrder.TopDown,
                One(0, 0, 4, 1), 1, new SourceRegion(0, 0, 4, 4),
                One(0, 4, 4, 5), 1, new SourceRegion(0, 4, 4, 4));
            for (int x = 0; x < W; x++) Assert.That(Blank(b, x, 0), Is.True);
            Assert.That(Untouched(b, 0, 1), Is.True);
        }

        [Test]
        public void Masking_works_in_place_on_memory_that_is_not_a_byte_array()
        {
            // The zero-copy path masks a span over native memory; stackalloc stands in for it (no array, no copy).
            System.Span<byte> native = stackalloc byte[W * H * 4];
            native.Fill(255);
            int n = FrameRedaction.BlankRects(native, W, H, VideoFrameRowOrder.BottomUp, One(0, 0, 2, 1), 1, 4, 4);
            Assert.That(n, Is.EqualTo(1));
            int lastRow = (H - 1) * W * 4;
            Assert.That(native[lastRow], Is.EqualTo(0));
            Assert.That(native[lastRow + 3], Is.EqualTo(255));
            Assert.That(native[0], Is.EqualTo(255));
            FrameRedaction.BlankAll(native, W, H);
            Assert.That(native[0] == 0 && native[native.Length - 4] == 0, Is.True);
        }

        [Test]
        public void A_span_shorter_than_the_frame_is_left_alone()
        {
            System.Span<byte> shortSpan = stackalloc byte[8];
            shortSpan.Fill(255);
            Assert.That(FrameRedaction.BlankRects(shortSpan, W, H, VideoFrameRowOrder.TopDown, One(0, 0, 4, 4), 1, 4, 4), Is.EqualTo(0));
            FrameRedaction.BlankAll(shortSpan, W, H);
            Assert.That(shortSpan[0], Is.EqualTo(255));
        }

        [Test]
        public void Blank_all_blanks_every_pixel_and_ignores_a_short_buffer()
        {
            var b = White();
            FrameRedaction.BlankAll(b, W, H);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(Blank(b, x, y), Is.True);
            var shortBuffer = new byte[3];
            FrameRedaction.BlankAll(shortBuffer, W, H);
            Assert.That(shortBuffer, Is.EqualTo(new byte[3]));
        }

        [Test]
        public void Bad_input_changes_nothing()
        {
            var b = White();
            Assert.That(FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, null, 1, 4, 4), Is.EqualTo(0));
            Assert.That(FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, new[] { 0, 0, 1, 1 }, 5, 4, 4), Is.EqualTo(0));
            Assert.That(FrameRedaction.BlankRects(b, W, H, VideoFrameRowOrder.TopDown, One(0, 0, 4, 4), 1, 0, 4), Is.EqualTo(0));
            Assert.That(FrameRedaction.BlankRects(new byte[3], W, H, VideoFrameRowOrder.TopDown, One(0, 0, 4, 4), 1, 4, 4), Is.EqualTo(0));
            Assert.That(Untouched(b, 0, 0), Is.True);
        }
    }
}
