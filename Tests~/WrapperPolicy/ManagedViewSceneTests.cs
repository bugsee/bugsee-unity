using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The scene block of the managed view hierarchy (specs: sdk/viewtree-scene). The scenes are built the way Unity hands them over (left-handed, +Y
    /// up, cameras that look along +Z, OpenGL-style projection matrices), with matrices written by an independent implementation here, so the
    /// conversion to the wire's convention is checked against the numbers the spec's handedness fixture prescribes.
    /// </summary>
    public class ManagedViewSceneTests
    {
        // ---- Unity-side matrices, row-major 4x4 ----

        static double[] Rotation(double x, double y, double z, double w) => new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
        };

        // Unity's Quaternion.Euler(0, degrees, 0): the rotation about +Y, which turns +Z (forward) towards +X (right).
        static double[] AboutY(double degrees) => new[] { 0, Math.Sin(degrees * Math.PI / 360), 0, Math.Cos(degrees * Math.PI / 360) };

        static float[] Trs(double[] q, double tx, double ty, double tz, double sx = 1, double sy = 1, double sz = 1)
        {
            var r = Rotation(q[0], q[1], q[2], q[3]);
            return new[]
            {
                (float)(r[0] * sx), (float)(r[1] * sy), (float)(r[2] * sz), (float)tx,
                (float)(r[3] * sx), (float)(r[4] * sy), (float)(r[5] * sz), (float)ty,
                (float)(r[6] * sx), (float)(r[7] * sy), (float)(r[8] * sz), (float)tz,
                0, 0, 0, 1,
            };
        }

        // worldToCameraMatrix: the inverse of the camera's rigid transform, then Z flipped (the camera space looks along -Z).
        static float[] View(double[] q, double tx, double ty, double tz)
        {
            var r = Rotation(q[0], q[1], q[2], q[3]);
            var inv = new double[12];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                inv[i * 4 + j] = r[j * 3 + i]; // transpose
            for (var i = 0; i < 3; i++) inv[i * 4 + 3] = -(inv[i * 4] * tx + inv[i * 4 + 1] * ty + inv[i * 4 + 2] * tz);
            var m = new float[16];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
                m[i * 4 + j] = (float)(inv[i * 4 + j] * (i == 2 ? -1 : 1));
            m[15] = 1;
            return m;
        }

        // Matrix4x4.Perspective(fov, aspect, near, far)
        static float[] Perspective(double fovDegrees, double aspect, double near, double far)
        {
            var t = Math.Tan(fovDegrees * Math.PI / 360);
            var m = new float[16];
            m[0] = (float)(1 / (aspect * t));
            m[5] = (float)(1 / t);
            m[10] = (float)(-(far + near) / (far - near));
            m[11] = (float)(-2 * far * near / (far - near));
            m[14] = -1;
            return m;
        }

        // Matrix4x4.Ortho for a camera whose orthographicSize (half the height) is `half`.
        static float[] Ortho(double half, double aspect, double near, double far)
        {
            var m = new float[16];
            m[0] = (float)(1 / (half * aspect));
            m[5] = (float)(1 / half);
            m[10] = (float)(-2 / (far - near));
            m[11] = (float)(-(far + near) / (far - near));
            m[15] = 1;
            return m;
        }

        static readonly double[] Identity = { 0, 0, 0, 1 };

        static void AddCamera(
            ManagedViewSceneSnapshot s, int id, double[] q, double px, double py, double pz, float[] projection, bool ortho,
            float near, float far, float depth, float[] rect, int mask = -1, bool main = false, bool texture = false)
        {
            var pose = new[] { (float)px, (float)py, (float)pz, (float)q[0], (float)q[1], (float)q[2], (float)q[3] };
            s.AddCamera(id, pose, View(q, px, py, pz), projection, near, far, depth, rect, mask, main, texture, ortho);
        }

        static void AddBox(ManagedViewSceneSnapshot s, int id, float[] matrix, float lx, float ly, float lz, int layer = 0, SceneObjectKind kind = SceneObjectKind.Mesh) =>
            s.AddObject(id, kind, layer, 0, matrix, 0, 0, 0, lx, ly, lz);

        // The handedness fixture of the spec, in Unity's own space: camera 1 at the origin looking along +Z, camera 2 ten metres to the
        // left of box A and five ahead, turned 90 degrees about Y so it looks along +X; box A 1x2x3 five metres ahead and turned 30 degrees
        // about Y (far end to the right); box B a 1 m cube five ahead and two to the right.
        static ManagedViewSceneSnapshot Fixture()
        {
            var s = new ManagedViewSceneSnapshot { Seen = 2, Frame = 77, TimeScale = 1f };
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f }, main: true);
            AddCamera(s, 2, AboutY(90), -10, 0, 5, Ortho(2, 1.0, 0.3, 100), true, 0.3f, 100f, 1f, new[] { 0f, 0f, 0.5f, 0.5f });
            AddBox(s, 10, Trs(AboutY(30), 0, 0, 5), 1, 2, 3);
            AddBox(s, 11, Trs(Identity, 2, 0, 5), 1, 1, 1);
            return s;
        }

        static JsonElement SceneOf(ManagedViewSceneSnapshot snapshot, int maxObjects = ManagedViewSceneJson.DefaultMaxObjects, Func<bool> expired = null, int maxChars = ManagedViewSceneJson.DefaultMaxChars)
        {
            var doc = ManagedViewSceneBuilder.Build(snapshot, expired);
            var sb = new StringBuilder("{");
            ManagedViewSceneJson.Write(sb, doc, maxObjects, maxChars, expired, out _, out _);
            sb.Append('}');
            return JsonDocument.Parse(sb.ToString().Replace("{,", "{")).RootElement.GetProperty("scene");
        }

        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

        static JsonElement ById(JsonElement array, string id) => array.EnumerateArray().First(x => x.GetProperty("id").GetString() == id);

        // q and -q are one rotation.
        static void AssertRotation(JsonElement owner, params double[] expected)
        {
            Assert.That(owner.TryGetProperty("rotation", out var r), Is.True, "a rotation is written");
            var q = Numbers(r);
            var sign = Math.Sign(q.Zip(expected, (a, b) => a * b).Sum());
            for (var i = 0; i < 4; i++) Assert.That(q[i] * sign, Is.EqualTo(expected[i]).Within(1e-4), "component " + i);
        }

        static void AssertVec(JsonElement e, params double[] expected)
        {
            var v = Numbers(e);
            for (var i = 0; i < expected.Length; i++) Assert.That(v[i], Is.EqualTo(expected[i]).Within(1e-3), "component " + i);
        }

        // ---- the handedness fixture ----

        [Test]
        public void TheHandednessFixtureEmitsTheValuesTheSpecPrescribes()
        {
            var scene = SceneOf(Fixture());
            Assert.That(scene.GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(scene.GetProperty("selection").GetString(), Is.EqualTo("visible"));
            var cameras = scene.GetProperty("cameras");
            var objects = scene.GetProperty("objects");

            var cam1 = ById(cameras, "1");
            AssertVec(cam1.GetProperty("position"), 0, 0, 0);
            Assert.That(cam1.TryGetProperty("rotation", out _), Is.False, "no rotation for the identity");
            Assert.That(cam1.GetProperty("projection").GetString(), Is.EqualTo("perspective"));
            Assert.That(cam1.GetProperty("fov_y").GetDouble(), Is.EqualTo(60).Within(1e-3));
            Assert.That(cam1.GetProperty("near").GetDouble(), Is.EqualTo(0.3).Within(1e-4));
            Assert.That(cam1.GetProperty("far").GetDouble(), Is.EqualTo(1000).Within(1e-3));
            Assert.That(cam1.TryGetProperty("viewport", out _), Is.False, "full screen: no viewport");
            Assert.That(cam1.GetProperty("main").GetBoolean(), Is.True);
            Assert.That(cam1.GetProperty("target").GetString(), Is.EqualTo("screen"));

            var cam2 = ById(cameras, "2");
            AssertVec(cam2.GetProperty("position"), -10, 0, -5);
            AssertRotation(cam2, 0, -0.7071, 0, 0.7071);
            Assert.That(cam2.GetProperty("projection").GetString(), Is.EqualTo("orthographic"));
            Assert.That(cam2.GetProperty("ortho_height").GetDouble(), Is.EqualTo(4).Within(1e-3));
            AssertVec(cam2.GetProperty("viewport"), 0, 0.5, 0.5, 0.5);
            Assert.That(cam2.TryGetProperty("main", out _), Is.False);

            var a = ById(objects, "10");
            AssertVec(a.GetProperty("center"), 0, 0, -5);
            AssertRotation(a, 0, -0.2588, 0, 0.9659);
            AssertVec(a.GetProperty("size"), 1, 2, 3);
            var b = ById(objects, "11");
            AssertVec(b.GetProperty("center"), 2, 0, -5);
            Assert.That(b.TryGetProperty("rotation", out _), Is.False);
            AssertVec(b.GetProperty("size"), 1, 1, 1);

            // The two square ends of box A, rebuilt from the wire values alone (right-handed: a rotation about +Y by theta takes +Z to (sin, 0, cos)).
            var q = Numbers(a.GetProperty("rotation"));
            var theta = 2 * Math.Atan2(q[1], q[3]);
            double endX = Math.Sin(theta) * 1.5, endZ = Math.Cos(theta) * 1.5;
            Assert.That(endX, Is.EqualTo(-0.75).Within(1e-3), "the near end is on the left");
            Assert.That(-5 + endZ, Is.EqualTo(-3.701).Within(1e-3), "the near end is the one closer to camera 1");
        }

        [Test]
        public void ACameraThatLooksAlongPlusZInUnityLooksAlongMinusZOnTheWire()
        {
            // Unity: a camera turned 90 degrees about Y looks along +X. On the wire (glTF) its rotation takes -Z to +X.
            var scene = SceneOf(Fixture());
            var q = Numbers(ById(scene.GetProperty("cameras"), "2").GetProperty("rotation"));
            var r = Rotation(q[0], q[1], q[2], q[3]);
            // forward = R * (0, 0, -1)
            Assert.That(-r[2], Is.EqualTo(1).Within(1e-3), "x of the forward direction");
            Assert.That(-r[5], Is.EqualTo(0).Within(1e-3));
            Assert.That(-r[8], Is.EqualTo(0).Within(1e-3));
        }

        [Test]
        public void ARotatedAndNonUniformlyScaledParentGivesAnOrientedBoxWithMeasuredSizes()
        {
            var s = new ManagedViewSceneSnapshot();
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            // scale (2, 1, 0.5), turned 45 degrees about Y, at (1, 1, 6); local bounds centered at (0.5, 0, 0), 1 x 1 x 1.
            s.AddObject(5, SceneObjectKind.Mesh, 0, 0, Trs(AboutY(45), 1, 1, 6, 2, 1, 0.5), 0.5f, 0, 0, 1, 1, 1);
            var o = ById(SceneOf(s).GetProperty("objects"), "5");
            AssertVec(o.GetProperty("size"), 2, 1, 0.5);
            // local center (0.5, 0, 0) is 1 unit along the scaled local X, which Unity turned 45 degrees towards +Z.
            var c = Math.Sqrt(0.5);
            AssertVec(o.GetProperty("center"), 1 + c, 1, -(6 - c));
        }

        [Test]
        public void AMirroredObjectIsWrittenWithAbsoluteSizesAndAProperRotation()
        {
            var s = new ManagedViewSceneSnapshot();
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            AddBox(s, 5, Trs(AboutY(20), 0, 0, 6, -2, 1, 1), 1, 1, 1);
            var o = ById(SceneOf(s).GetProperty("objects"), "5");
            AssertVec(o.GetProperty("size"), 2, 1, 1);
            var q = Numbers(o.GetProperty("rotation"));
            Assert.That(Math.Sqrt(q.Sum(v => v * v)), Is.EqualTo(1).Within(1e-3), "a unit quaternion");
            // The proper rotation has a positive determinant and no scale: rebuilding the matrix from the wire values must be orthonormal.
            var r = Rotation(q[0], q[1], q[2], q[3]);
            var det = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6]) + r[2] * (r[3] * r[7] - r[4] * r[6]);
            Assert.That(det, Is.EqualTo(1).Within(1e-4));
        }

        // ---- selection, order, attribution ----

        [Test]
        public void OnlyObjectsInsideTheViewVolumeOfACameraThatDrawsTheirLayerAreSent()
        {
            var s = new ManagedViewSceneSnapshot { Seen = 6 };
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 100), false, 0.3f, 100f, 0f, new[] { 0f, 0f, 1f, 1f }, mask: 1 << 3);
            AddBox(s, 1, Trs(Identity, 0, 0, 10), 1, 1, 1, layer: 3);          // in front, on the drawn layer
            AddBox(s, 2, Trs(Identity, 0, 0, -10), 1, 1, 1, layer: 3);         // behind the camera
            AddBox(s, 3, Trs(Identity, 0, 0, 10), 1, 1, 1, layer: 4);          // in front, a layer the camera does not draw
            AddBox(s, 4, Trs(Identity, 500, 0, 10), 1, 1, 1, layer: 3);        // far to the side
            AddBox(s, 5, Trs(Identity, 0, 0, 500), 1, 1, 1, layer: 3);         // beyond the far plane
            AddBox(s, 6, Trs(Identity, 5.9, 0, 10), 4, 1, 1, layer: 3);        // its center is outside, its extent reaches in
            var scene = SceneOf(s);
            var ids = scene.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()).OrderBy(x => x).ToArray();
            Assert.That(ids, Is.EqualTo(new[] { "1", "6" }));
            Assert.That(scene.GetProperty("objects_seen").GetInt32(), Is.EqualTo(6));
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(2));
            Assert.That(scene.GetProperty("objects_sent").GetInt32(), Is.EqualTo(2));
            Assert.That(scene.TryGetProperty("truncated", out _), Is.False);
        }

        [Test]
        public void ObjectsAreSentLargestOnScreenFirstAndOneCameraIsNamedOnlyWhenItAloneDrewIt()
        {
            var s = Fixture();
            AddBox(s, 12, Trs(Identity, 0, 0, 200), 1, 1, 1); // small on screen: far away
            AddBox(s, 13, Trs(Identity, 0, 0, 3), 1, 1, 1);   // big on screen: close
            var scene = SceneOf(s);
            var ids = scene.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
            Assert.That(ids.IndexOf("13"), Is.LessThan(ids.IndexOf("12")));
            Assert.That(ids.IndexOf("10"), Is.LessThan(ids.IndexOf("12")));
            var far = ById(scene.GetProperty("objects"), "12");
            Assert.That(far.GetProperty("camera").GetString(), Is.EqualTo("1"), "only camera 1 sees an object 200 m ahead of it");
            Assert.That(ById(scene.GetProperty("objects"), "10").TryGetProperty("camera", out _), Is.False, "two cameras drew box A");
        }

        [Test]
        public void ASecondRendererOnOneGameObjectDoesNotRepeatTheId()
        {
            var s = Fixture();
            AddBox(s, 10, Trs(Identity, 0, 0, 5), 4, 4, 4);
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("objects").EnumerateArray().Count(o => o.GetProperty("id").GetString() == "10"), Is.EqualTo(1));
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(2));
        }

        [Test]
        public void ASecondRendererQualifiesWhenTheFirstOneOfTheGameObjectIsOutsideEveryView()
        {
            var s = Fixture();
            AddBox(s, 40, Trs(Identity, 0, 0, -50), 1, 1, 1); // behind the camera
            AddBox(s, 40, Trs(Identity, 0, 0, 8), 1, 1, 1);   // the same GameObject's other renderer, in view
            var scene = SceneOf(s);
            Assert.That(ById(scene.GetProperty("objects"), "40").GetProperty("center")[2].GetDouble(), Is.EqualTo(-8).Within(1e-3));
        }

        [Test]
        public void ACanvasDrawnByACameraThatIsNotInTheSceneNamesNoCamera()
        {
            var s = Fixture();
            s.AddObject(41, SceneObjectKind.Ui, 5, 9999, Trs(Identity, 0, 0, 4), 0, 0, 0, 2, 1, 0);
            var o = ById(SceneOf(s).GetProperty("objects"), "41");
            Assert.That(o.TryGetProperty("camera", out var c) ? c.GetString() : null, Is.Not.EqualTo("9999"));
        }

        [Test]
        public void AFinishedTreeIsNotMarkedTruncatedWhenOnlyTheSceneIsCutOff()
        {
            var snapshot = TreeSnapshot();
            snapshot.Scene = new ManagedViewSceneSnapshot();
            snapshot.TreeComplete = true;
            double now = 1000;
            var src = new SlowSource(snapshot);
            var worker = new List<Action>();
            var engine = new ManagedViewCaptureEngine(
                () => now, worker.Add, (d, a) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite),
                b => src, x => { }, () => 1.0, _ => { });
            var replies = new List<string>();
            engine.Submit(new ManagedViewRequests.Once(replies.Add), 1000.0, () => 1f, false, SceneGraphCapture.All);
            now = 1100; engine.Pump();
            now = 1300; engine.Pump(); // past the snapshot deadline, the scene still unread
            Assert.That(worker.Count, Is.EqualTo(1));
            worker[0]();
            Assert.That(JsonDocument.Parse(replies[0]).RootElement.TryGetProperty("truncated", out _), Is.False);
        }

        sealed class SlowSource : IManagedViewSource
        {
            readonly ManagedViewSnapshot _s;
            public SlowSource(ManagedViewSnapshot s) { _s = s; }
            public ManagedViewSnapshot Snapshot => _s;
            public bool Step(Func<bool> sliceExpired) => false;
            public void Abandon() { }
        }

        [Test]
        public void NonFiniteObjectsAndCamerasAreLeftOutAndNotCounted()
        {
            var s = Fixture();
            AddBox(s, 20, Trs(Identity, float.NaN, 0, 5), 1, 1, 1);
            AddBox(s, 21, Trs(Identity, 0, 0, 5), -1, 1, 1);
            AddBox(s, 22, Trs(Identity, 0, 0, 5, 0, 1, 1), 1, 1, 1); // collapsed to a plane by a zero scale
            AddCamera(s, 3, Identity, float.PositiveInfinity, 0, 0, Perspective(60, 1.0, 0.3, 100), false, 0.3f, 100f, 2f, new[] { 0f, 0f, 1f, 1f });
            AddCamera(s, 4, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 100), false, 0f, 100f, 3f, new[] { 0f, 0f, 1f, 1f }); // near 0 for a perspective camera
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("cameras").GetArrayLength(), Is.EqualTo(2));
            Assert.That(scene.GetProperty("objects").GetArrayLength(), Is.EqualTo(2));
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(2));
        }

        [Test]
        public void AFlatObjectKeepsAZeroExtent()
        {
            var s = Fixture();
            s.AddObject(30, SceneObjectKind.Ui, 5, 0, Trs(Identity, 0, 0, 4), 0, 0, 0, 2, 1, 0);
            var o = ById(SceneOf(s).GetProperty("objects"), "30");
            AssertVec(o.GetProperty("size"), 2, 1, 0);
            Assert.That(o.GetProperty("kind").GetString(), Is.EqualTo("ui"));
            Assert.That(o.GetProperty("layer").GetInt32(), Is.EqualTo(5));
        }

        [Test]
        public void ACameraThatRendersIntoATextureIsEmittedAsOneAndNeverTheMainView()
        {
            var s = Fixture();
            AddCamera(s, 9, Identity, 0, 50, 0, Perspective(40, 1.0, 0.5, 100), false, 0.5f, 100f, -5f, new[] { 0f, 0f, 1f, 1f }, texture: true);
            var scene = SceneOf(s);
            var cam = ById(scene.GetProperty("cameras"), "9");
            Assert.That(cam.GetProperty("target").GetString(), Is.EqualTo("texture"));
            Assert.That(cam.GetProperty("order").GetInt32(), Is.LessThan(ById(scene.GetProperty("cameras"), "1").GetProperty("order").GetInt32()));
            Assert.That(scene.GetProperty("cameras").EnumerateArray().Count(c => c.TryGetProperty("main", out _)), Is.EqualTo(1));
        }

        [Test]
        public void AnOffCenterProjectionIsSentAsItsMatrix()
        {
            var s = new ManagedViewSceneSnapshot();
            var p = Perspective(60, 1.0, 0.3, 1000);
            p[2] = 0.2f; // lens shift
            AddCamera(s, 1, Identity, 0, 0, 0, p, false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            var cam = SceneOf(s).GetProperty("cameras")[0];
            var m = Numbers(cam.GetProperty("projection_matrix"));
            Assert.That(m.Length, Is.EqualTo(16));
            Assert.That(m[2 * 4 + 0], Is.EqualTo(0.2).Within(1e-5), "column-major: the element m02 is entry 8");
            Assert.That(m[0], Is.EqualTo(p[0]).Within(1e-5));
        }

        // ---- limits ----

        [Test]
        public void ALimitBelowTheQualifiedCountTruncatesAndKeepsTheLargest()
        {
            var s = new ManagedViewSceneSnapshot { Seen = 50 };
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            for (var i = 0; i < 50; i++) AddBox(s, 100 + i, Trs(Identity, 0, 0, 5 + i), 1, 1, 1);
            var scene = SceneOf(s, maxObjects: 10);
            Assert.That(scene.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(50));
            Assert.That(scene.GetProperty("objects_sent").GetInt32(), Is.EqualTo(10));
            Assert.That(scene.GetProperty("objects").GetArrayLength(), Is.EqualTo(10));
            var ids = scene.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToArray();
            Assert.That(ids, Is.EqualTo(Enumerable.Range(100, 10).Select(i => i.ToString()).ToArray()), "the nearest ten");
        }

        [Test]
        public void ACharacterLimitStopsTheObjectsAndSaysSo()
        {
            var s = new ManagedViewSceneSnapshot();
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            for (var i = 0; i < 200; i++) AddBox(s, 100 + i, Trs(Identity, 0, 0, 5 + i), 1, 1, 1);
            var scene = SceneOf(s, maxChars: 3000);
            Assert.That(scene.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(scene.GetProperty("objects_sent").GetInt32(), Is.GreaterThan(0).And.LessThan(200));
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(200));
        }

        [Test]
        public void ADeadlineOnTheWorkerStopsTheObjectsAndSaysTheCountIsNotKnown()
        {
            var s = new ManagedViewSceneSnapshot();
            AddCamera(s, 1, Identity, 0, 0, 0, Perspective(60, 1.0, 0.3, 1000), false, 0.3f, 1000f, 0f, new[] { 0f, 0f, 1f, 1f });
            for (var i = 0; i < 1000; i++) AddBox(s, 100 + i, Trs(Identity, 0, 0, 5 + i * 0.5), 1, 1, 1);
            var scene = SceneOf(s, expired: () => true);
            Assert.That(scene.GetProperty("truncated").GetBoolean() || scene.TryGetProperty("skipped", out _), Is.True);
            Assert.That(scene.GetProperty("objects_sent").GetInt32(), Is.LessThan(1000));
        }

        [Test]
        public void ASceneTheMainThreadCouldNotFinishIsTruncatedWithoutAQualifiedCount()
        {
            var s = Fixture();
            s.Incomplete = true;
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(scene.TryGetProperty("objects_qualified", out _), Is.False);
            Assert.That(scene.TryGetProperty("objects_seen", out _), Is.False);
            Assert.That(scene.GetProperty("objects_sent").GetInt32(), Is.EqualTo(2));
        }

        [Test]
        public void AnIncompleteSceneWithNothingReadSaysTimeBudget()
        {
            var s = new ManagedViewSceneSnapshot { Incomplete = true, Skipped = ManagedViewSceneSnapshot.SkippedTimeBudget };
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("skipped").GetString(), Is.EqualTo("time_budget"));
            Assert.That(scene.GetProperty("objects").GetArrayLength(), Is.EqualTo(0));
            Assert.That(scene.GetProperty("cameras").GetArrayLength(), Is.EqualTo(0));
        }

        [Test]
        public void WithNoCameraTheSceneIsEmptyAndCompleteWithTheReasonNoCamera()
        {
            var s = new ManagedViewSceneSnapshot { Seen = 4 };
            AddBox(s, 1, Trs(Identity, 0, 0, 5), 1, 1, 1);
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("skipped").GetString(), Is.EqualTo("no_camera"));
            Assert.That(scene.GetProperty("objects_qualified").GetInt32(), Is.EqualTo(0));
            Assert.That(scene.GetProperty("objects").GetArrayLength(), Is.EqualTo(0));
            Assert.That(scene.TryGetProperty("truncated", out _), Is.False);
        }

        [Test]
        public void LayerNamesAndFrameAreWrittenWhenKnown()
        {
            var s = Fixture();
            s.LayerNames = new string[32];
            s.LayerNames[0] = "Default";
            s.LayerNames[5] = "UI";
            var scene = SceneOf(s);
            Assert.That(scene.GetProperty("layer_names").GetProperty("5").GetString(), Is.EqualTo("UI"));
            Assert.That(scene.GetProperty("layer_names").EnumerateObject().Count(), Is.EqualTo(2));
            Assert.That(scene.GetProperty("frame").GetInt32(), Is.EqualTo(77));
            Assert.That(scene.GetProperty("time_scale").GetDouble(), Is.EqualTo(1));
        }

        // ---- the wire ----

        [Test]
        public void NumbersAreWrittenWithADotWhateverTheLocale()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var sb = new StringBuilder();
                var doc = ManagedViewSceneBuilder.Build(Fixture());
                ManagedViewSceneJson.Write(sb, doc, 100, 100000, null, out _, out _);
                var text = sb.ToString();
                Assert.That(text, Does.Contain("\"near\":0.3,").And.Contain("\"center\":[2,0,-5]").And.Contain("\"rotation\":[0,-0.2588"));
                Assert.That(text, Does.Not.Contain("0,3,").And.Not.Contain("0,2588"));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [TestCase(1.23456, 3, true, "1.235")]
        [TestCase(0.0123456, 3, true, "0.012346")]
        [TestCase(0.00001234567, 3, true, "0.000012346")]
        [TestCase(1234567.8912, 3, true, "1234567.891")]
        [TestCase(-0.0001, 3, true, "-0.0001")]
        [TestCase(0.0, 3, true, "0")]
        [TestCase(0.70711, 5, false, "0.70711")]
        public void NumbersKeepTheSpecsPrecision(double v, int decimals, bool significant, string expected) =>
            Assert.That(ManagedViewSceneJson.Num(v, decimals, significant), Is.EqualTo(expected));

        // ---- inside the reply ----

        static ManagedViewSnapshot TreeSnapshot()
        {
            var t = new ManagedViewSnapshot(4);
            var root = t.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            t.SetBounds(root, 0, 0, 1080, 2400);
            var a = t.Add(root, 10, null, "MeshRenderer", "Secret crate");
            t.AddComponent(a, "MeshRenderer");
            t.SetBounds(a, 100, 100, 200, 200);          // under the secure rectangle
            var b = t.Add(root, 11, null, "MeshRenderer", "Public crate");
            t.AddComponent(b, "MeshRenderer");
            t.SetBounds(b, 800, 1800, 100, 100);
            return t;
        }

        static readonly int[] Secure = { 0, 0, 500, 500 };

        [Test]
        public void TheSceneSitsOnTheRootNextToTheTree()
        {
            var t = TreeSnapshot();
            var opts = new ManagedViewWriteOptions { Scene = ManagedViewSceneBuilder.Build(Fixture()) };
            var json = ManagedViewTreeJson.Write(t.ToTree(), Secure, 1, 1f, null, out _, out _, opts);
            var root = JsonDocument.Parse(json).RootElement;
            Assert.That(root.GetProperty("scene").GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("subitems").GetArrayLength(), Is.EqualTo(2));
            Assert.That(opts.SceneWritten, Is.True);
            Assert.That(opts.SceneSent, Is.EqualTo(2));
            Assert.That(root.GetProperty("subitems")[0].TryGetProperty("scene", out _), Is.False, "the key is on the root only");
        }

        [Test]
        public void TheTreeIsByteIdenticalWithoutAScene()
        {
            var plain = ManagedViewTreeJson.Write(TreeSnapshot().ToTree(), Secure, 1, 1f);
            var withOptions = ManagedViewTreeJson.Write(TreeSnapshot().ToTree(), Secure, 1, 1f, null, out _, out _, new ManagedViewWriteOptions());
            Assert.That(withOptions, Is.EqualTo(plain));
        }

        [Test]
        public void ASceneIsNotWrittenWhenTheRootHasNoBounds()
        {
            var t = new ManagedViewSnapshot(4);
            t.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            var json = ManagedViewTreeJson.Write(t.ToTree(), null, 0, 1f, null, out _, out _, new ManagedViewWriteOptions { Scene = ManagedViewSceneBuilder.Build(Fixture()) });
            Assert.That(json, Does.Not.Contain("\"scene\""));
        }

        [Test]
        public void ADeadlineThatCutTheTreeLeavesTheSceneOut()
        {
            var calls = 0;
            var big = new ManagedViewSnapshot(4);
            var root = big.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            big.SetBounds(root, 0, 0, 100, 100);
            for (var i = 0; i < 100; i++) big.Add(root, 10 + i, null, "Node", "n");
            var opts = new ManagedViewWriteOptions { Scene = ManagedViewSceneBuilder.Build(Fixture()) };
            var json = ManagedViewTreeJson.Write(big.ToTree(), null, 0, 1f, () => ++calls > 1, out _, out var cut, opts);
            Assert.That(cut, Is.True);
            Assert.That(json, Does.Not.Contain("\"scene\""));
            Assert.That(opts.SceneWritten, Is.False);
            JsonDocument.Parse(json);
        }

        [Test]
        public void NamesOnlyWritesNoBoundsAnywhereAndWithholdsTheSameNamesAsAll()
        {
            var all = ManagedViewTreeJson.Write(TreeSnapshot().ToTree(), Secure, 1, 1f, null, out var securedAll, out _, new ManagedViewWriteOptions());
            var names = ManagedViewTreeJson.Write(TreeSnapshot().ToTree(), Secure, 1, 1f, null, out var securedNames, out _, new ManagedViewWriteOptions { EmitBounds = false });
            Assert.That(names, Does.Not.Contain("bounds"));
            Assert.That(names, Does.Not.Contain("\"scene\""));
            Assert.That(names, Does.Not.Contain("[0,0,0,0]"));
            Assert.That(names, Does.Not.Contain("Secret"), "a name the secure rectangle withholds stays withheld without the geometry");
            Assert.That(names, Does.Contain("Public crate"));
            Assert.That(all, Does.Not.Contain("Secret"));
            Assert.That(securedNames, Is.EqualTo(securedAll));
            Assert.That(NameSet(names), Is.EqualTo(NameSet(all)));
        }

        [Test]
        public void ANodeWhoseGeometryIsUnknownWritesNoBoundsRatherThanZeros()
        {
            var t = new ManagedViewSnapshot(4);
            var root = t.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            t.SetBounds(root, 0, 0, 1080, 2400);
            t.Add(root, 10, null, "Empty", null); // no geometry anywhere below it
            var json = ManagedViewTreeJson.Write(t.ToTree(), null, 0, 1f);
            var kid = JsonDocument.Parse(json).RootElement.GetProperty("subitems")[0];
            Assert.That(kid.TryGetProperty("bounds", out _), Is.False);
            Assert.That(json, Does.Not.Contain("[0,0,0,0]"));
        }

        static string[] NameSet(string json)
        {
            var names = new List<string>();
            void Walk(JsonElement e)
            {
                if (e.TryGetProperty("options", out var o) && o.TryGetProperty("name", out var n)) names.Add(n.GetString());
                if (e.TryGetProperty("subitems", out var kids)) foreach (var k in kids.EnumerateArray()) Walk(k);
            }

            Walk(JsonDocument.Parse(json).RootElement);
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        [Test]
        public void ASecureRectangleCoveringTheWholeScreenLeavesTheSceneGeometryAndWritesNoLabelsInIt()
        {
            var t = TreeSnapshot();
            var opts = new ManagedViewWriteOptions { Scene = ManagedViewSceneBuilder.Build(Fixture()) };
            var json = ManagedViewTreeJson.Write(t.ToTree(), new[] { -10, -10, 5000, 5000 }, 1, 1f, null, out _, out _, opts);
            var root = JsonDocument.Parse(json).RootElement;
            var plain = ManagedViewTreeJson.Write(TreeSnapshot().ToTree(), new[] { -10, -10, 5000, 5000 }, 1, 1f, null, out _, out _, new ManagedViewWriteOptions { Scene = ManagedViewSceneBuilder.Build(Fixture()) });
            Assert.That(json, Is.EqualTo(plain));
            foreach (var o in root.GetProperty("scene").GetProperty("objects").EnumerateArray())
            {
                Assert.That(o.TryGetProperty("name", out _), Is.False);
                Assert.That(o.TryGetProperty("options", out _), Is.False);
            }

            Assert.That(root.GetProperty("scene").GetProperty("objects")[0].GetProperty("size").GetArrayLength(), Is.EqualTo(3), "the geometry is untouched");
            Assert.That(json, Does.Not.Contain("crate"));
        }

        // ---- the engine ----

        sealed class SceneSource : IManagedViewSource
        {
            readonly ManagedViewSnapshot _snapshot;
            public SceneSource(ManagedViewSnapshot s) { _snapshot = s; }
            public ManagedViewSnapshot Snapshot => _snapshot;
            public bool Step(Func<bool> sliceExpired) => true;
            public void Abandon() { }
        }

        [Test]
        public void TheEngineWritesTheSceneOfTheSnapshotAndReportsItsNumbers()
        {
            var snapshot = TreeSnapshot();
            snapshot.Scene = Fixture();
            var worker = new List<Action>();
            var stats = new List<ManagedViewCaptureStats>();
            SceneGraphCapture seen = SceneGraphCapture.None;
            var engine = new ManagedViewCaptureEngine(
                () => 1000.0, worker.Add, (d, a) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite),
                budget => { seen = budget.Level; return new SceneSource(snapshot); }, x => { }, () => 1.0, stats.Add);
            var replies = new List<string>();
            engine.Submit(new ManagedViewRequests.Once(replies.Add), 1000.0, () => 1f, false, SceneGraphCapture.All);
            engine.Pump();
            Assert.That(worker.Count, Is.EqualTo(1));
            worker[0]();
            Assert.That(seen, Is.EqualTo(SceneGraphCapture.All));
            Assert.That(replies.Count, Is.EqualTo(1));
            Assert.That(JsonDocument.Parse(replies[0]).RootElement.GetProperty("scene").GetProperty("objects").GetArrayLength(), Is.EqualTo(2));
            Assert.That(stats[0].Scene, Is.True);
            Assert.That(stats[0].SceneObjects, Is.EqualTo(2));
            Assert.That(stats[0].SceneCameras, Is.EqualTo(2));
        }

        [Test]
        public void WithASceneTheTreeGetsTheFirstPartOfTheWindowAndTheSceneTheRest()
        {
            double now = 1000;
            ManagedViewBudget budget = null;
            var engine = new ManagedViewCaptureEngine(
                () => now, a => { }, (d, a) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite),
                b => { budget = b; return new SceneSource(TreeSnapshot()); }, x => { }, () => 1.0, _ => { });
            engine.Submit(new ManagedViewRequests.Once(_ => { }), 1000.0, () => 1f, false, SceneGraphCapture.All);
            Assert.That(budget.IsExhausted, Is.False);
            now = 1000 + ManagedViewRequests.TreeDeadlineMs + 1;
            Assert.That(budget.IsExhausted, Is.True, "the tree's share is spent");
            Assert.That(budget.SceneExpired, Is.False, "the scene still has its part");
            now = 1000 + ManagedViewRequests.SnapshotDeadlineMs + 1;
            Assert.That(budget.SceneExpired, Is.True);
            Assert.That(ManagedViewRequests.TreeDeadlineMs, Is.LessThan(ManagedViewRequests.SnapshotDeadlineMs));
        }

        [Test]
        public void WithoutASceneTheTreeKeepsTheWholeWindow()
        {
            double now = 1000;
            ManagedViewBudget budget = null;
            var engine = new ManagedViewCaptureEngine(
                () => now, a => { }, (d, a) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite),
                b => { budget = b; return new SceneSource(TreeSnapshot()); }, x => { }, () => 1.0, _ => { });
            engine.Submit(new ManagedViewRequests.Once(_ => { }), 1000.0, () => 1f, false, SceneGraphCapture.NamesOnly);
            now = 1000 + ManagedViewRequests.TreeDeadlineMs + 1;
            Assert.That(budget.IsExhausted, Is.False);
            Assert.That(budget.Level, Is.EqualTo(SceneGraphCapture.NamesOnly));
        }

        [Test]
        public void ASceneTooBigToWriteInsideTheRequestIsNotWrittenThere()
        {
            var snapshot = TreeSnapshot();
            snapshot.Scene = new ManagedViewSceneSnapshot();
            for (var i = 0; i < ManagedViewRequests.InlineMaxNodes + 1; i++) AddBox(snapshot.Scene, i + 1, Trs(Identity, 0, 0, 5), 1, 1, 1);
            var worker = new List<Action>();
            var engine = new ManagedViewCaptureEngine(
                () => 1000.0, worker.Add, (d, a) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite),
                b => new SceneSource(snapshot), x => { }, () => 1.0, _ => { });
            var replies = new List<string>();
            engine.Submit(new ManagedViewRequests.Once(replies.Add), 1000.0, () => 1f, true, SceneGraphCapture.All);
            Assert.That(replies, Is.Empty, "inline only for a small snapshot, scene included");
            Assert.That(worker.Count, Is.EqualTo(1));
        }

        // ---- the option ----

        [TestCase(null, SceneGraphCapture.All, true)]
        [TestCase("All", SceneGraphCapture.All, true)]
        [TestCase("NamesOnly", SceneGraphCapture.NamesOnly, true)]
        [TestCase("none", SceneGraphCapture.None, true)]
        [TestCase("names_only", SceneGraphCapture.NamesOnly, true)]
        [TestCase("Names-Only", SceneGraphCapture.NamesOnly, true)]
        [TestCase("Everything", SceneGraphCapture.None, false)]
        [TestCase("", SceneGraphCapture.None, false)]
        [TestCase(2, SceneGraphCapture.None, false)]
        public void TheOptionValueIsReadByName(object value, SceneGraphCapture expected, bool recognized)
        {
            Assert.That(SceneGraphCaptureOption.Parse(value, out var ok), Is.EqualTo(expected));
            Assert.That(ok, Is.EqualTo(recognized));
        }

        [Test]
        public void TheEnumValuesAreReadAsTheyAre()
        {
            Assert.That(SceneGraphCaptureOption.Parse(SceneGraphCapture.NamesOnly, out var ok), Is.EqualTo(SceneGraphCapture.NamesOnly));
            Assert.That(ok, Is.True);
            Assert.That(SceneGraphCaptureOption.Parse((SceneGraphCapture)9, out ok), Is.EqualTo(SceneGraphCapture.None));
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TheLaunchOptionIsStoredByConstantNameUnderTheUnityPrefix()
        {
            Assert.That(Options.CaptureSceneGraph, Is.EqualTo("com.bugsee.option.unity.capture-scene-graph"));
            var map = new AndroidLaunchOptions { CaptureSceneGraph = SceneGraphCapture.NamesOnly }.ToDictionary();
            Assert.That(map[Options.CaptureSceneGraph], Is.EqualTo("NamesOnly"));
            var ios = new IOSLaunchOptions { CaptureSceneGraph = SceneGraphCapture.None }.ToDictionary();
            Assert.That(ios[Options.CaptureSceneGraph], Is.EqualTo("None"));
            var built = new OptionsBuilder().CaptureSceneGraph(SceneGraphCapture.All).Build();
            Assert.That(built[Options.CaptureSceneGraph], Is.EqualTo("All"));
            foreach (SceneGraphCapture level in Enum.GetValues(typeof(SceneGraphCapture)))
                Assert.That(SceneGraphCaptureOption.Parse(SceneGraphCaptureOption.ToWire(level), out _), Is.EqualTo(level));
        }

        [Test]
        public void TheOptionReachesNativeUnchangedOnBothPlatforms()
        {
            var options = new Dictionary<string, object> { [Options.CaptureSceneGraph] = "NamesOnly" };
            Assert.That(OptionPlatformGate.ForIos(options)[Options.CaptureSceneGraph], Is.EqualTo("NamesOnly"));
            Assert.That(OptionPlatformGate.ForAndroid(options)[Options.CaptureSceneGraph], Is.EqualTo("NamesOnly"));
        }
    }
}
