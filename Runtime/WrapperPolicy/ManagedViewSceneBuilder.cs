using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>A camera as the wire writes it: glTF space (right-handed, +Y up, looking along -Z), degrees, top-left viewport.</summary>
    public struct SceneCameraDoc
    {
        public int Id;
        public double PX, PY, PZ;
        public double QX, QY, QZ, QW;
        public bool Orthographic;
        public double FovY, OrthoHeight, Aspect, Near, Far;
        public double VX, VY, VW, VH;
        public int Order;
        public bool Main, RendersToTexture;
        /// <summary>The exact projection, column-major, when the fields above do not describe it (lens shift, an off-center AR projection); null otherwise.</summary>
        public double[] ProjectionMatrix;
    }

    /// <summary>An oriented bounding box as the wire writes it.</summary>
    public struct SceneObjectDoc
    {
        public int Id;
        public SceneObjectKind Kind;
        public double CX, CY, CZ;
        public double QX, QY, QZ, QW;
        public double SX, SY, SZ;
        /// <summary>The id of the one camera that drew it, 0 when none or more than one did.</summary>
        public int Camera;
        public int Layer;
        /// <summary>On-screen size, the sort key (largest first); not written.</summary>
        public double Priority;
    }

    /// <summary>The scene after the worker's conversion and selection.</summary>
    public sealed class ManagedViewSceneDocument
    {
        public readonly List<SceneCameraDoc> Cameras = new List<SceneCameraDoc>();
        /// <summary>In priority order: the largest on screen first.</summary>
        public readonly List<SceneObjectDoc> Objects = new List<SceneObjectDoc>();
        /// <summary>Objects the producer enumerated, or -1 when it did not get that far.</summary>
        public int Seen = -1;
        /// <summary>Objects that passed the selection and are valid (plus the unread ones the main thread found drawn). -1: not known.</summary>
        public int Qualified = -1;
        public string Skipped;
        /// <summary>The read stopped early: the objects are a part of the scene, and how many qualified is not known.</summary>
        public bool Incomplete;
        public int Frame = -1;
        public float TimeScale = -1f;
        public string[] LayerNames;
    }

    /// <summary>
    /// The scene of a <see cref="ManagedViewSceneSnapshot"/> in the wire convention of the scene spec (sdk/viewtree-scene), on the worker
    /// thread: Unity is left-handed with +Y up and cameras that look along +Z, the wire is glTF (right-handed, +Y up, cameras look along
    /// -Z), so the producer mirrors Z (<c>S = diag(1, 1, -1)</c>): a position <c>(x, y, z)</c> is written <c>(x, y, -z)</c>, a rotation
    /// matrix <c>R</c> is written <c>S R S</c> (a quaternion <c>(x, y, z, w)</c> becomes <c>(-x, -y, z, w)</c>), sizes are unchanged. It
    /// also picks the objects (inside the view volume of an emitted camera that draws their layer), orders them by on-screen size, and
    /// finds the camera that drew each. Pure arithmetic, no Unity type.
    /// </summary>
    public static class ManagedViewSceneBuilder
    {
        /// <summary>A reflection only changes the sign of a component, so a quaternion within this of the identity is written as none.</summary>
        const double IdentityEpsilon = 1e-6;

        public static ManagedViewSceneDocument Build(ManagedViewSceneSnapshot s, Func<bool> expired = null)
        {
            var doc = new ManagedViewSceneDocument
            {
                Seen = s.Incomplete ? -1 : s.Seen, Skipped = s.Skipped, Incomplete = s.Incomplete, Frame = s.Frame, TimeScale = s.TimeScale,
                LayerNames = s.LayerNames,
            };
            var valid = new List<int>(); // snapshot indices of the cameras that are valid, in snapshot order
            for (var i = 0; i < s.CameraCount; i++)
            {
                if (!TryCamera(s, i, out var cam)) continue;
                valid.Add(i);
                doc.Cameras.Add(cam);
            }

            AssignOrder(s, valid, doc.Cameras);
            if (valid.Count == 0)
            {
                // Nothing can be inside a view volume. A complete, empty scene when the read had nothing left to do; an unfinished read
                // keeps the reason it gave.
                if (doc.Skipped == null) doc.Skipped = ManagedViewSceneSnapshot.SkippedNoCamera;
                if (doc.Skipped == ManagedViewSceneSnapshot.SkippedNoCamera) doc.Qualified = 0;
                return doc;
            }

            var planes = new double[valid.Count][];
            var view2 = new double[valid.Count][];
            var projection11 = new double[valid.Count];
            for (var k = 0; k < valid.Count; k++)
            {
                planes[k] = FrustumPlanes(s, valid[k]);
                view2[k] = new double[] { s.CameraView(valid[k], 8), s.CameraView(valid[k], 9), s.CameraView(valid[k], 10), s.CameraView(valid[k], 11) };
                projection11[k] = s.CameraProjection(valid[k], 5);
            }

            var qualified = 0;
            var ids = new HashSet<int>();
            for (var i = 0; i < s.ObjectCount; i++)
            {
                if ((i & 255) == 255 && expired != null && expired())
                {
                    // The worker is out of time: what was converted so far stands, the rest is left out and the count is not known.
                    doc.Incomplete = true;
                    break;
                }

                if (!TryObject(s, i, out var obj, out var axes, out var halfLocal)) continue;
                if (!ids.Add(obj.Id)) continue; // a second renderer on one GameObject: its box is the first one's, the id is unique
                var seenBy = 0;
                var by = 0;
                var best = 0.0;
                for (var k = 0; k < valid.Count; k++)
                {
                    var mask = s.CameraMask(valid[k]);
                    if (obj.Layer < 0 || obj.Layer > 31 || (mask & (1 << obj.Layer)) == 0) continue;
                    if (!InsideVolume(planes[k], s, i, axes, halfLocal)) continue;
                    seenBy++;
                    by = doc.Cameras[k].Id;
                    var score = Score(s, valid[k], view2[k], projection11[k], i, axes, halfLocal);
                    if (score > best) best = score;
                }

                if (seenBy == 0) continue;
                qualified++;
                // A screen-space-camera canvas is drawn by exactly its own camera; anything else by the one camera that sees it, if one.
                obj.Camera = s.ObjectCamera(i) != 0 ? s.ObjectCamera(i) : (seenBy == 1 ? by : 0);
                obj.Priority = best;
                doc.Objects.Add(obj);
            }

            doc.Qualified = doc.Incomplete ? -1 : qualified;
            doc.Objects.Sort(CompareByPriority);
            return doc;
        }

        static int CompareByPriority(SceneObjectDoc a, SceneObjectDoc b)
        {
            var c = b.Priority.CompareTo(a.Priority);
            if (c != 0) return c;
            return a.Id.CompareTo(b.Id); // a stable, repeatable order for equal sizes
        }

        // ---- cameras ----

        static bool TryCamera(ManagedViewSceneSnapshot s, int i, out SceneCameraDoc cam)
        {
            cam = default;
            for (var k = 0; k < 7; k++) if (!IsFinite(s.CameraPose(i, k))) return false;
            var p00 = (double)s.CameraProjection(i, 0);
            var p11 = (double)s.CameraProjection(i, 5);
            if (!IsFinite(p00) || !IsFinite(p11) || !(p00 > 0) || !(p11 > 0)) return false;
            double near = s.CameraScalar(i, 0), far = s.CameraScalar(i, 1);
            var orthographic = s.CameraIsOrthographic(i);
            if (!IsFinite(near) || !IsFinite(far) || !(far > near)) return false;
            if (orthographic ? near < 0 : !(near > 0)) return false;
            var aspect = p11 / p00;
            if (!(aspect > 0) || double.IsInfinity(aspect)) return false;
            var q = Mirror(s.CameraPose(i, 3), s.CameraPose(i, 4), s.CameraPose(i, 5), s.CameraPose(i, 6));
            if (!TryNormalize(ref q)) return false;
            cam = new SceneCameraDoc
            {
                Id = s.CameraId(i),
                PX = s.CameraPose(i, 0), PY = s.CameraPose(i, 1), PZ = -(double)s.CameraPose(i, 2),
                QX = q[0], QY = q[1], QZ = q[2], QW = q[3],
                Orthographic = orthographic, Aspect = aspect, Near = near, Far = far,
                Main = s.CameraIsMain(i), RendersToTexture = s.CameraRendersToTexture(i),
            };
            if (orthographic) cam.OrthoHeight = 2.0 / p11;
            else
            {
                cam.FovY = 2.0 * Math.Atan(1.0 / p11) * 180.0 / Math.PI;
                if (!(cam.FovY > 0 && cam.FovY < 180)) return false;
            }

            // Unity's rect has its origin at the bottom left; the wire's viewport at the top left.
            double rx = s.CameraScalar(i, 3), ry = s.CameraScalar(i, 4), rw = s.CameraScalar(i, 5), rh = s.CameraScalar(i, 6);
            if (!IsFinite(rx) || !IsFinite(ry) || !IsFinite(rw) || !IsFinite(rh) || !(rw > 0) || !(rh > 0)) return false;
            cam.VX = rx; cam.VY = 1.0 - ry - rh; cam.VW = rw; cam.VH = rh;

            // Lens shift, an oblique plane or the off-center projection an AR session sets are not in fov and aspect: send the matrix.
            // Unity's projection is OpenGL's (camera space looks along -Z with +Y up, depth -1 at the near plane), which is also glTF's, and
            // the camera space is the same on both sides of the mirror, so the matrix is written as it is, column-major.
            if (Math.Abs(s.CameraProjection(i, 2)) > 1e-4 || Math.Abs(s.CameraProjection(i, 6)) > 1e-4)
            {
                var m = new double[16];
                var finite = true;
                for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                {
                    var v = s.CameraProjection(i, r * 4 + c);
                    if (!IsFinite(v)) finite = false;
                    m[c * 4 + r] = v;
                }

                if (finite) cam.ProjectionMatrix = m;
            }

            return true;
        }

        // Composite order: ascending camera depth, ties in the order Unity lists the cameras. Written as the rank, which is dense and
        // keeps the comparison the spec asks for.
        static void AssignOrder(ManagedViewSceneSnapshot s, List<int> valid, List<SceneCameraDoc> cameras)
        {
            var rank = new int[valid.Count];
            for (var a = 0; a < valid.Count; a++)
            {
                var r = 0;
                var da = s.CameraScalar(valid[a], 2);
                for (var b = 0; b < valid.Count; b++)
                {
                    var db = s.CameraScalar(valid[b], 2);
                    if (db < da || (db == da && b < a)) r++;
                }

                rank[a] = r;
            }

            for (var a = 0; a < cameras.Count; a++)
            {
                var c = cameras[a];
                c.Order = rank[a];
                cameras[a] = c;
            }
        }

        // The six planes of the view volume in Unity world space, normals pointing in: a point is inside when a*x + b*y + c*z + d >= 0 for all.
        // clip = projection * view * (x, y, z, 1) is OpenGL's, so -w <= x, y, z <= w.
        static double[] FrustumPlanes(ManagedViewSceneSnapshot s, int cam)
        {
            var m = new double[16];
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += s.CameraProjection(cam, r * 4 + k) * (double)s.CameraView(cam, k * 4 + c);
                m[r * 4 + c] = sum;
            }

            var planes = new double[24];
            var signs = new[] { 1, -1 };
            var n = 0;
            for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side < 2; side++)
            {
                double a = m[12] + signs[side] * m[axis * 4], b = m[13] + signs[side] * m[axis * 4 + 1];
                double c = m[14] + signs[side] * m[axis * 4 + 2], d = m[15] + signs[side] * m[axis * 4 + 3];
                var len = Math.Sqrt(a * a + b * b + c * c);
                if (len > 0) { a /= len; b /= len; c /= len; d /= len; }
                planes[n++] = a; planes[n++] = b; planes[n++] = c; planes[n++] = d;
            }

            return planes;
        }

        // ---- objects ----

        // axes: the three columns of the local-to-world matrix times the half local size (the box's edge vectors from its center, in
        // Unity space, scale and shear included); halfLocal: the local half sizes.
        static bool TryObject(ManagedViewSceneSnapshot s, int i, out SceneObjectDoc obj, out double[] axes, out double[] halfLocal)
        {
            obj = default;
            axes = null;
            halfLocal = null;
            var m = new double[12];
            for (var k = 0; k < 12; k++)
            {
                var v = s.ObjectMatrix(i, k);
                if (!IsFinite(v)) return false;
                m[k] = v;
            }

            double lcx = s.ObjectBox(i, 0), lcy = s.ObjectBox(i, 1), lcz = s.ObjectBox(i, 2);
            double lsx = s.ObjectBox(i, 3), lsy = s.ObjectBox(i, 4), lsz = s.ObjectBox(i, 5);
            if (!IsFinite(lcx) || !IsFinite(lcy) || !IsFinite(lcz) || !IsFinite(lsx) || !IsFinite(lsy) || !IsFinite(lsz)) return false;
            if (lsx < 0 || lsy < 0 || lsz < 0) return false;

            // Column i of the matrix is the world direction of the local axis i, with the scale in its length.
            double[] cx = { m[0], m[4], m[8] }, cy = { m[1], m[5], m[9] }, cz = { m[2], m[6], m[10] };
            var lx = Length(cx); var ly = Length(cy); var lz = Length(cz);
            if (!(lx > 1e-12) || !(ly > 1e-12) || !(lz > 1e-12)) return false; // a collapsed object has no box
            var wcx = m[0] * lcx + m[1] * lcy + m[2] * lcz + m[3];
            var wcy = m[4] * lcx + m[5] * lcy + m[6] * lcz + m[7];
            var wcz = m[8] * lcx + m[9] * lcy + m[10] * lcz + m[11];

            // The nearest rotation to the (possibly sheared) axes: x kept, y made perpendicular to it, z = x cross y. For a reflected
            // matrix (a negative scale) that cross product points the other way, which is the proper rotation the spec asks for (the box is
            // symmetric, so it does not change).
            var ex = Scale(cx, 1.0 / lx);
            var ey = Sub(cy, Scale(ex, Dot(cy, ex)));
            var ley = Length(ey);
            if (!(ley > 1e-9))
            {
                // y is parallel to x (a fully sheared object): any perpendicular will do.
                ey = Math.Abs(ex[1]) < 0.9 ? new[] { 0.0, 1.0, 0.0 } : new[] { 1.0, 0.0, 0.0 };
                ey = Sub(ey, Scale(ex, Dot(ey, ex)));
                ley = Length(ey);
            }

            ey = Scale(ey, 1.0 / ley);
            var ez = Cross(ex, ey);

            // The same rotation in the mirrored space: R' = S R S, S = diag(1, 1, -1).
            var r = new double[9];
            for (var c = 0; c < 3; c++)
            {
                var col = c == 0 ? ex : (c == 1 ? ey : ez);
                for (var row = 0; row < 3; row++) r[row * 3 + c] = col[row] * (row == 2 ? -1 : 1) * (c == 2 ? -1 : 1);
            }

            var q = MatrixToQuaternion(r);
            if (!TryNormalize(ref q)) return false;
            obj = new SceneObjectDoc
            {
                Id = s.ObjectId(i), Kind = s.ObjectKind(i), Layer = s.ObjectLayer(i),
                CX = wcx, CY = wcy, CZ = -wcz,
                QX = q[0], QY = q[1], QZ = q[2], QW = q[3],
                SX = lx * lsx, SY = ly * lsy, SZ = lz * lsz,
            };
            if (!IsFinite(obj.CX) || !IsFinite(obj.CY) || !IsFinite(obj.CZ) || !IsFinite(obj.SX) || !IsFinite(obj.SY) || !IsFinite(obj.SZ)) return false;
            halfLocal = new[] { lsx * 0.5, lsy * 0.5, lsz * 0.5 };
            axes = new[] { cx[0], cx[1], cx[2], cy[0], cy[1], cy[2], cz[0], cz[1], cz[2], wcx, wcy, wcz };
            return true;
        }

        // The oriented box against the six planes: it is outside when it lies wholly behind one of them. The box is a parallelepiped with
        // edge vectors column * halfLocal, so its extent along a normal is the sum of the absolute projections of the three.
        static bool InsideVolume(double[] planes, ManagedViewSceneSnapshot s, int obj, double[] axes, double[] half)
        {
            for (var p = 0; p < 6; p++)
            {
                double a = planes[p * 4], b = planes[p * 4 + 1], c = planes[p * 4 + 2], d = planes[p * 4 + 3];
                var dist = a * axes[9] + b * axes[10] + c * axes[11] + d;
                var radius = Math.Abs(a * axes[0] + b * axes[1] + c * axes[2]) * half[0]
                    + Math.Abs(a * axes[3] + b * axes[4] + c * axes[5]) * half[1]
                    + Math.Abs(a * axes[6] + b * axes[7] + c * axes[8]) * half[2];
                if (dist + radius < 0) return false;
            }

            return true;
        }

        // On-screen size as a fraction of the image height: the bounding radius over the distance along the view direction, times the
        // projection's focal scale (an orthographic camera has no distance term).
        static double Score(ManagedViewSceneSnapshot s, int cam, double[] viewZ, double p11, int obj, double[] axes, double[] half)
        {
            var e0 = Length(new[] { axes[0] * half[0], axes[1] * half[0], axes[2] * half[0] });
            var e1 = Length(new[] { axes[3] * half[1], axes[4] * half[1], axes[5] * half[1] });
            var e2 = Length(new[] { axes[6] * half[2], axes[7] * half[2], axes[8] * half[2] });
            var radius = Math.Sqrt(e0 * e0 + e1 * e1 + e2 * e2);
            if (s.CameraIsOrthographic(cam)) return radius * p11;
            var depth = -(viewZ[0] * axes[9] + viewZ[1] * axes[10] + viewZ[2] * axes[11] + viewZ[3]);
            var near = s.CameraScalar(cam, 0);
            return radius * p11 / Math.Max(depth, near);
        }

        // ---- math ----

        // The rotation (x, y, z, w) seen in the mirrored space: S q S with S = diag(1, 1, -1) is (-x, -y, z, w).
        static double[] Mirror(double x, double y, double z, double w) => new[] { -x, -y, z, w };

        static bool TryNormalize(ref double[] q)
        {
            var len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (!IsFinite(len) || !(len > 1e-9)) return false;
            // q and -q are one rotation: written with w >= 0.
            var sign = (q[3] < 0 || (q[3] == 0 && FirstNonZeroNegative(q))) ? -1.0 : 1.0;
            for (var i = 0; i < 4; i++) q[i] = q[i] / len * sign;
            return true;
        }

        static bool FirstNonZeroNegative(double[] q)
        {
            for (var i = 0; i < 3; i++) if (q[i] != 0) return q[i] < 0;
            return false;
        }

        // Row-major 3x3 rotation to (x, y, z, w).
        static double[] MatrixToQuaternion(double[] r)
        {
            double m00 = r[0], m01 = r[1], m02 = r[2], m10 = r[3], m11 = r[4], m12 = r[5], m20 = r[6], m21 = r[7], m22 = r[8];
            double x, y, z, w;
            var trace = m00 + m11 + m22;
            if (trace > 0)
            {
                var k = Math.Sqrt(trace + 1) * 2.0;
                w = 0.25 * k; x = (m21 - m12) / k; y = (m02 - m20) / k; z = (m10 - m01) / k;
            }
            else if (m00 > m11 && m00 > m22)
            {
                var k = Math.Sqrt(1 + m00 - m11 - m22) * 2.0;
                w = (m21 - m12) / k; x = 0.25 * k; y = (m01 + m10) / k; z = (m02 + m20) / k;
            }
            else if (m11 > m22)
            {
                var k = Math.Sqrt(1 + m11 - m00 - m22) * 2.0;
                w = (m02 - m20) / k; x = (m01 + m10) / k; y = 0.25 * k; z = (m12 + m21) / k;
            }
            else
            {
                var k = Math.Sqrt(1 + m22 - m00 - m11) * 2.0;
                w = (m10 - m01) / k; x = (m02 + m20) / k; y = (m12 + m21) / k; z = 0.25 * k;
            }

            return new[] { x, y, z, w };
        }

        /// <summary>True when the rotation is the identity within the precision the wire keeps (it is then left out).</summary>
        public static bool IsIdentity(double x, double y, double z, double w) =>
            Math.Abs(x) < IdentityEpsilon && Math.Abs(y) < IdentityEpsilon && Math.Abs(z) < IdentityEpsilon;

        static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double Length(double[] a) => Math.Sqrt(Dot(a, a));
        static double[] Scale(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
}
