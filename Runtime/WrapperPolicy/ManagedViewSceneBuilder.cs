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

                if (!TryObject(s, i, out var obj, out var axes)) continue;
                var seenBy = 0;
                var by = 0;
                var best = 0.0;
                for (var k = 0; k < valid.Count; k++)
                {
                    var mask = s.CameraMask(valid[k]);
                    if (obj.Layer < 0 || obj.Layer > 31 || (mask & (1 << obj.Layer)) == 0) continue;
                    if (!InsideVolume(planes[k], axes)) continue;
                    seenBy++;
                    by = doc.Cameras[k].Id;
                    var score = Score(s, valid[k], view2[k], projection11[k], axes);
                    if (score > best) best = score;
                }

                if (seenBy == 0) continue;
                if (!ids.Add(obj.Id)) continue; // a second renderer on one GameObject (the first that qualified wins): the id is unique
                qualified++;
                // A screen-space-camera canvas is drawn by exactly its own camera; anything else by the one camera that sees it, if one.
                var own = s.ObjectCamera(i);
                var ownEmitted = false;
                for (var k = 0; k < doc.Cameras.Count; k++) if (doc.Cameras[k].Id == own) ownEmitted = true;
                // a canvas drawn by a camera that is not in the document names none (it is not drawn by whichever other camera happens to see it)
                obj.Camera = own != 0 ? (ownEmitted ? own : 0) : (seenBy == 1 ? by : 0);
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
            // The rotation (x, y, z, w) seen in the mirrored space: S q S with S = diag(1, 1, -1) is (-x, -y, z, w).
            var q = new Quat(-(double)s.CameraPose(i, 3), -(double)s.CameraPose(i, 4), s.CameraPose(i, 5), s.CameraPose(i, 6));
            if (!TryNormalize(ref q)) return false;
            cam = new SceneCameraDoc
            {
                Id = s.CameraId(i),
                PX = s.CameraPose(i, 0), PY = s.CameraPose(i, 1), PZ = -(double)s.CameraPose(i, 2),
                QX = q.X, QY = q.Y, QZ = q.Z, QW = q.W,
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
            if (Math.Abs(s.CameraProjection(i, 2)) > 1e-4 || Math.Abs(s.CameraProjection(i, 6)) > 1e-4
                || Math.Abs(s.CameraProjection(i, 8)) > 1e-4 || Math.Abs(s.CameraProjection(i, 9)) > 1e-4
                || Math.Abs(s.CameraProjection(i, 3)) > 1e-4 || Math.Abs(s.CameraProjection(i, 7)) > 1e-4) // an off-center orthographic window
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

        // A box's edge vectors from its center (the three columns of the local-to-world matrix times the half local size: scale and shear
        // included) and its center, in Unity space. Value types throughout the per-object math: up to 10,000 objects per capture would
        // otherwise be hundreds of thousands of small arrays on the worker.
        struct BoxAxes
        {
            public double XX, XY, XZ, YX, YY, YZ, ZX, ZY, ZZ; // the columns (scale in their length)
            public double CX, CY, CZ;                         // the box center
            public double H0, H1, H2;                         // the local half sizes
        }

        struct V3
        {
            public double X, Y, Z;
            public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static V3 Scale(V3 a, double k) => new V3(a.X * k, a.Y * k, a.Z * k);
            public static V3 Sub(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        struct Quat
        {
            public double X, Y, Z, W;
            public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        }

        static bool TryObject(ManagedViewSceneSnapshot s, int i, out SceneObjectDoc obj, out BoxAxes axes)
        {
            obj = default;
            axes = default;
            for (var k = 0; k < 12; k++) if (!IsFinite(s.ObjectMatrix(i, k))) return false;
            double m0 = s.ObjectMatrix(i, 0), m1 = s.ObjectMatrix(i, 1), m2 = s.ObjectMatrix(i, 2), m3 = s.ObjectMatrix(i, 3);
            double m4 = s.ObjectMatrix(i, 4), m5 = s.ObjectMatrix(i, 5), m6 = s.ObjectMatrix(i, 6), m7 = s.ObjectMatrix(i, 7);
            double m8 = s.ObjectMatrix(i, 8), m9 = s.ObjectMatrix(i, 9), m10 = s.ObjectMatrix(i, 10), m11 = s.ObjectMatrix(i, 11);

            double lcx = s.ObjectBox(i, 0), lcy = s.ObjectBox(i, 1), lcz = s.ObjectBox(i, 2);
            double lsx = s.ObjectBox(i, 3), lsy = s.ObjectBox(i, 4), lsz = s.ObjectBox(i, 5);
            if (!IsFinite(lcx) || !IsFinite(lcy) || !IsFinite(lcz) || !IsFinite(lsx) || !IsFinite(lsy) || !IsFinite(lsz)) return false;
            if (lsx < 0 || lsy < 0 || lsz < 0) return false;

            // Column i of the matrix is the world direction of the local axis i, with the scale in its length.
            var cx = new V3(m0, m4, m8);
            var cy = new V3(m1, m5, m9);
            var cz = new V3(m2, m6, m10);
            var lx = cx.Length; var ly = cy.Length; var lz = cz.Length;
            if (!(lx > 1e-12) || !(ly > 1e-12) || !(lz > 1e-12)) return false; // a collapsed object has no box
            var wcx = m0 * lcx + m1 * lcy + m2 * lcz + m3;
            var wcy = m4 * lcx + m5 * lcy + m6 * lcz + m7;
            var wcz = m8 * lcx + m9 * lcy + m10 * lcz + m11;

            // The nearest rotation to the (possibly sheared) axes: x kept, y made perpendicular to it, z = x cross y. For a reflected
            // matrix (a negative scale) that cross product points the other way, which is the proper rotation the spec asks for (the box is
            // symmetric, so it does not change).
            var ex = V3.Scale(cx, 1.0 / lx);
            var ey = V3.Sub(cy, V3.Scale(ex, V3.Dot(cy, ex)));
            var ley = ey.Length;
            if (!(ley > 1e-9))
            {
                // y is parallel to x (a fully sheared object): any perpendicular will do.
                ey = Math.Abs(ex.Y) < 0.9 ? new V3(0, 1, 0) : new V3(1, 0, 0);
                ey = V3.Sub(ey, V3.Scale(ex, V3.Dot(ey, ex)));
                ley = ey.Length;
            }

            ey = V3.Scale(ey, 1.0 / ley);
            var ez = V3.Cross(ex, ey);

            // The same rotation in the mirrored space: R' = S R S, S = diag(1, 1, -1) (the sign of an entry flips when exactly one of its
            // row and column is the z axis).
            var q = MatrixToQuaternion(
                ex.X, ey.X, -ez.X,
                ex.Y, ey.Y, -ez.Y,
                -ex.Z, -ey.Z, ez.Z);
            if (!TryNormalize(ref q)) return false;
            obj = new SceneObjectDoc
            {
                Id = s.ObjectId(i), Kind = s.ObjectKind(i), Layer = s.ObjectLayer(i),
                CX = wcx, CY = wcy, CZ = -wcz,
                QX = q.X, QY = q.Y, QZ = q.Z, QW = q.W,
                // The extent along each emitted axis is the sum of the absolute projections of the three transformed half-edges onto it: equal to
                // scale * size for an unsheared object, and a box that still contains a sheared one.
                SX = (Math.Abs(V3.Dot(cx, ex)) * lsx + Math.Abs(V3.Dot(cy, ex)) * lsy + Math.Abs(V3.Dot(cz, ex)) * lsz),
                SY = (Math.Abs(V3.Dot(cx, ey)) * lsx + Math.Abs(V3.Dot(cy, ey)) * lsy + Math.Abs(V3.Dot(cz, ey)) * lsz),
                SZ = (Math.Abs(V3.Dot(cx, ez)) * lsx + Math.Abs(V3.Dot(cy, ez)) * lsy + Math.Abs(V3.Dot(cz, ez)) * lsz),
            };
            if (!IsFinite(obj.CX) || !IsFinite(obj.CY) || !IsFinite(obj.CZ) || !IsFinite(obj.SX) || !IsFinite(obj.SY) || !IsFinite(obj.SZ)) return false;
            axes = new BoxAxes
            {
                XX = cx.X, XY = cx.Y, XZ = cx.Z, YX = cy.X, YY = cy.Y, YZ = cy.Z, ZX = cz.X, ZY = cz.Y, ZZ = cz.Z,
                CX = wcx, CY = wcy, CZ = wcz, H0 = lsx * 0.5, H1 = lsy * 0.5, H2 = lsz * 0.5,
            };
            return true;
        }

        // The oriented box against the six planes: it is outside when it lies wholly behind one of them. The box is a parallelepiped with
        // edge vectors column * halfLocal, so its extent along a normal is the sum of the absolute projections of the three.
        static bool InsideVolume(double[] planes, BoxAxes b)
        {
            for (var p = 0; p < 6; p++)
            {
                double a = planes[p * 4], pb = planes[p * 4 + 1], c = planes[p * 4 + 2], d = planes[p * 4 + 3];
                var dist = a * b.CX + pb * b.CY + c * b.CZ + d;
                var radius = Math.Abs(a * b.XX + pb * b.XY + c * b.XZ) * b.H0
                    + Math.Abs(a * b.YX + pb * b.YY + c * b.YZ) * b.H1
                    + Math.Abs(a * b.ZX + pb * b.ZY + c * b.ZZ) * b.H2;
                if (dist + radius < 0) return false;
            }

            return true;
        }

        // On-screen size as a fraction of the image height: the bounding radius over the distance along the view direction, times the
        // projection's focal scale (an orthographic camera has no distance term).
        static double Score(ManagedViewSceneSnapshot s, int cam, double[] viewZ, double p11, BoxAxes b)
        {
            var e0 = new V3(b.XX * b.H0, b.XY * b.H0, b.XZ * b.H0).Length;
            var e1 = new V3(b.YX * b.H1, b.YY * b.H1, b.YZ * b.H1).Length;
            var e2 = new V3(b.ZX * b.H2, b.ZY * b.H2, b.ZZ * b.H2).Length;
            var radius = Math.Sqrt(e0 * e0 + e1 * e1 + e2 * e2);
            if (s.CameraIsOrthographic(cam)) return radius * p11;
            var depth = -(viewZ[0] * b.CX + viewZ[1] * b.CY + viewZ[2] * b.CZ + viewZ[3]);
            var near = s.CameraScalar(cam, 0);
            return radius * p11 / Math.Max(depth, near);
        }

        // ---- math ----

        static bool TryNormalize(ref Quat q)
        {
            var len = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (!IsFinite(len) || !(len > 1e-9)) return false;
            // q and -q are one rotation: written with w >= 0.
            var negative = q.W < 0 || (q.W == 0 && (q.X != 0 ? q.X < 0 : (q.Y != 0 ? q.Y < 0 : q.Z < 0)));
            var sign = negative ? -1.0 : 1.0;
            q = new Quat(q.X / len * sign, q.Y / len * sign, q.Z / len * sign, q.W / len * sign);
            return true;
        }

        // Row-major 3x3 rotation to (x, y, z, w).
        static Quat MatrixToQuaternion(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
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

            return new Quat(x, y, z, w);
        }

        /// <summary>True when the rotation is the identity within the precision the wire keeps (it is then left out).</summary>
        public static bool IsIdentity(double x, double y, double z, double w) =>
            Math.Abs(x) < IdentityEpsilon && Math.Abs(y) < IdentityEpsilon && Math.Abs(z) < IdentityEpsilon;

        static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
