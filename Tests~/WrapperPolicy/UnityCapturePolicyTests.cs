using System.Collections.Generic;
using System.IO;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class UnityCapturePolicyTests
    {
        static UnityCaptureEnvironment Supported() => new UnityCaptureEnvironment
        {
            IsAndroidPlayer = true,
            UnityVersion = "6000.0.84f1",
            ScreenCaptureModule = true,
            AsyncReadbackSupported = true,
            GraphicsApi = "Vulkan"
        };

        static Dictionary<string, object> Mode(object value) =>
            new Dictionary<string, object> { { Options.CaptureVideoMode, value }, { "other", 1 } };

        [Test]
        public void Supported_environment_keeps_direct_buffers_and_starts_the_pusher()
        {
            var d = UnityCapturePolicy.Decide(VideoMode.DirectBuffers, Supported());
            Assert.That(d.Effective, Is.EqualTo(VideoMode.DirectBuffers));
            Assert.That(d.UseUnityCapture, Is.True);
            Assert.That(d.FallbackReason, Is.Null);
        }

        [TestCase("2021.3.45f1")]
        [TestCase("2022.1.24f1")]
        [TestCase("garbage")]
        [TestCase(null)]
        public void Old_or_unknown_unity_falls_back_to_the_default_capture(string version)
        {
            var env = Supported();
            env.UnityVersion = version;
            var d = UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env);
            Assert.That(d.Effective, Is.EqualTo(VideoMode.V2));
            Assert.That(d.UseUnityCapture, Is.False);
            Assert.That(d.FallbackReason, Does.Contain("NewDirectByteBuffer"));
            Assert.That(d.FallbackReason, Does.Contain(UnityCapturePolicy.MinimumUnityVersion));
        }

        [TestCase("2022.2.0f1")]
        [TestCase("2022.3.62f3")]
        [TestCase("2022.3.1p1")]
        [TestCase("6000.0.84f1")]
        public void The_minimum_version_and_everything_above_it_is_supported(string version)
        {
            var env = Supported();
            env.UnityVersion = version;
            env.GraphicsApi = "OpenGLES3";
            Assert.That(UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env).UseUnityCapture, Is.True);
        }

        [TestCase("2022.2.0a11")]
        [TestCase("2022.2.0b1")]
        [TestCase("2023.1.0a5")]
        [TestCase("6000.0.1b2")]
        public void Pre_release_builds_are_not_supported(string version)
        {
            var env = Supported();
            env.UnityVersion = version;
            Assert.That(UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env).UseUnityCapture, Is.False);
        }

        [Test]
        public void The_environment_is_not_read_unless_direct_buffers_is_requested()
        {
            // Reading it touches SystemInfo, which only works on the main thread: a launch from another thread that does
            // not use DirectBuffers must never do it.
            int reads = 0;
            UnityCaptureEnvironment Probe() { reads++; return Supported(); }
            foreach (var options in new[] { null, new Dictionary<string, object>(), Mode((int)VideoMode.V2), Mode(VideoMode.Fullscreen) })
            {
                var output = UnityCapturePolicy.Apply(options, Probe, out var decision);
                Assert.That(output, Is.SameAs(options));
                Assert.That(decision.UseUnityCapture, Is.False);
            }
            Assert.That(reads, Is.EqualTo(0));
            UnityCapturePolicy.Apply(Mode((int)VideoMode.DirectBuffers), Probe, out _);
            Assert.That(reads, Is.EqualTo(1));
        }

        [Test]
        public void Missing_screen_capture_module_falls_back()
        {
            var env = Supported();
            env.ScreenCaptureModule = false;
            var d = UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env);
            Assert.That(d.Effective, Is.EqualTo(VideoMode.V2));
            Assert.That(d.FallbackReason, Does.Contain("Screen Capture"));
        }

        [Test]
        public void No_async_readback_falls_back()
        {
            var env = Supported();
            env.AsyncReadbackSupported = false;
            Assert.That(UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env).FallbackReason, Does.Contain("AsyncGPUReadback"));
        }

        [TestCase("OpenGLES2")]
        [TestCase("Direct3D11")]
        [TestCase(null)]
        public void Unmeasured_graphics_api_falls_back_unless_the_row_order_is_declared(string api)
        {
            var env = Supported();
            env.GraphicsApi = api;
            var d = UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env);
            Assert.That(d.Effective, Is.EqualTo(VideoMode.V2));
            Assert.That(d.FallbackReason, Does.Contain("row order"));

            env.RowOrderDeclared = true;
            Assert.That(UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env).UseUnityCapture, Is.True);
        }

        [TestCase(VideoMode.None)]
        [TestCase(VideoMode.V1)]
        [TestCase(VideoMode.V2)]
        [TestCase(VideoMode.Fullscreen)]
        public void Other_modes_are_never_touched(VideoMode requested)
        {
            var env = Supported();
            env.UnityVersion = "2021.3.45f1";
            var d = UnityCapturePolicy.Decide(requested, env);
            Assert.That(d.Effective, Is.EqualTo(requested));
            Assert.That(d.UseUnityCapture, Is.False);
            Assert.That(d.FallbackReason, Is.Null);
        }

        [Test]
        public void Direct_buffers_is_only_acted_on_for_android_players()
        {
            var env = Supported();
            env.IsAndroidPlayer = false;
            env.UnityVersion = "2021.3.45f1";
            var d = UnityCapturePolicy.Decide(VideoMode.DirectBuffers, env);
            Assert.That(d.Effective, Is.EqualTo(VideoMode.DirectBuffers));
            Assert.That(d.UseUnityCapture, Is.False);
            Assert.That(d.FallbackReason, Is.Null);
        }

        [TestCase(21)]
        [TestCase(21L)]
        [TestCase("21")]
        public void The_options_passed_on_have_the_default_mode_when_unity_is_too_old(object raw)
        {
            var env = Supported();
            env.UnityVersion = "2021.3.45f1";
            var input = Mode(raw);
            var output = UnityCapturePolicy.Apply(input, env, out var decision);
            Assert.That(output[Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(output["other"], Is.EqualTo(1));
            Assert.That(decision.FallbackReason, Is.Not.Null);
            Assert.That(input[Options.CaptureVideoMode], Is.EqualTo(raw), "the caller's dictionary is not modified");
        }

        [Test]
        public void The_typed_enum_value_is_recognised_too()
        {
            var env = Supported();
            env.UnityVersion = "2021.3.45f1";
            var output = UnityCapturePolicy.Apply(Mode(VideoMode.DirectBuffers), env, out _);
            Assert.That(output[Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
        }

        [Test]
        public void Supported_environment_passes_the_same_dictionary_through()
        {
            var input = Mode((int)VideoMode.DirectBuffers);
            var output = UnityCapturePolicy.Apply(input, Supported(), out var decision);
            Assert.That(output, Is.SameAs(input));
            Assert.That(decision.UseUnityCapture, Is.True);
        }

        [Test]
        public void Options_without_a_video_mode_or_null_options_pass_through()
        {
            var env = Supported();
            env.UnityVersion = "2021.3.45f1";
            var none = new Dictionary<string, object> { { "x", 1 } };
            Assert.That(UnityCapturePolicy.Apply(none, env, out var d1), Is.SameAs(none));
            Assert.That(d1.UseUnityCapture, Is.False);
            Assert.That(UnityCapturePolicy.Apply(null, env, out var d2), Is.Null);
            Assert.That(d2.FallbackReason, Is.Null);
        }

        [Test]
        public void With_fallback_sets_the_default_mode_on_a_copy()
        {
            var input = Mode((int)VideoMode.DirectBuffers);
            var output = UnityCapturePolicy.WithFallback(input);
            Assert.That(output[Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(input[Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.DirectBuffers));
            Assert.That(UnityCapturePolicy.WithFallback(null)[Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
        }

        // The launch paths must run the policy before the options reach the bridge. Asserts on code, not prose.
        static string Code(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir).FullName;
            var kept = new System.Text.StringBuilder();
            foreach (var line in File.ReadAllLines(Path.Combine(dir, relative)))
                if (!line.TrimStart().StartsWith("//") && !line.TrimStart().StartsWith("///") && !line.TrimStart().StartsWith("*"))
                    kept.AppendLine(line);
            return kept.ToString();
        }

        [TestCase("Bridge.Launch(appToken, options);")]
        [TestCase("Bridge.Relaunch(options);")]
        [TestCase("bridge.Launch(appToken, options, accepted")]
        [TestCase("bridge.Relaunch(options, accepted")]
        public void Every_launch_path_applies_the_policy_before_calling_the_bridge(string bridgeCall)
        {
            var code = Code("Runtime/Bugsee.cs");
            int call = code.IndexOf(bridgeCall, System.StringComparison.Ordinal);
            Assert.That(call, Is.GreaterThan(0), bridgeCall);
            int policy = code.LastIndexOf("options = ApplyCapturePolicy(options, out bool startPusher);", call, System.StringComparison.Ordinal);
            Assert.That(policy, Is.GreaterThan(0), "no policy call before " + bridgeCall);
            Assert.That(call - policy, Is.LessThan(900), "policy call is not in the same method as " + bridgeCall);
        }
    }
}
