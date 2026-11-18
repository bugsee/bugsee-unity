using System;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedViewProjectionTests
    {
        static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        // Unity's perspective matrix for a camera looking down -z.
        static float[] Perspective(float fovYDegrees, float aspect, float near, float far)
        {
            var f = 1f / (float)Math.Tan(fovYDegrees * Math.PI / 360.0);
            return new[] { f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, -(far + near) / (far - near), -2 * far * near / (far - near), 0, 0, -1, 0 };
        }

        static float[] Orthographic(float halfHeight, float aspect, float near, float far) =>
            new[] { 1 / (halfHeight * aspect), 0, 0, 0, 0, 1 / halfHeight, 0, 0, 0, 0, -2 / (far - near), -(far + near) / (far - near), 0, 0, 0, 1 };

        [Test]
        public void APerspectivePointOnTheAxisIsAtTheCentreOfTheCamerasPixels()
        {
            var p = new ManagedViewProjection(Identity(), Perspective(60, 1080f / 1920f, 0.3f, 1000), 0, 0, 1080, 1920);
            Assert.That(p.TryProject(0, 0, -10, out var x, out var y), Is.True);
            Assert.That(x, Is.EqualTo(540).Within(1e-3));
            Assert.That(y, Is.EqualTo(960).Within(1e-3));
        }

        [Test]
        public void ThePointAtTheEdgeOfTheFrustumIsAtTheEdgeOfThePixelRect()
        {
            var aspect = 2f;
            var p = new ManagedViewProjection(Identity(), Perspective(90, aspect, 0.1f, 100), 100, 50, 800, 400);
            // fov 90: at depth 10 the half height is 10 and the half width 20.
            Assert.That(p.TryProject(20, 10, -10, out var x, out var y), Is.True);
            Assert.That(x, Is.EqualTo(900).Within(1e-2));
            Assert.That(y, Is.EqualTo(450).Within(1e-2));
            Assert.That(p.TryProject(-20, -10, -10, out x, out y), Is.True);
            Assert.That(x, Is.EqualTo(100).Within(1e-2));
            Assert.That(y, Is.EqualTo(50).Within(1e-2));
        }

        [Test]
        public void APointBehindTheCameraOrOnItsPlaneHasNoPosition()
        {
            var p = new ManagedViewProjection(Identity(), Perspective(60, 1f, 0.3f, 100), 0, 0, 100, 100);
            Assert.That(p.TryProject(0, 0, 5, out _, out _), Is.False);
            Assert.That(p.TryProject(0, 0, 0, out _, out _), Is.False);
            Assert.That(p.TryProject(1, 1, -0.001f, out _, out _), Is.True);
        }

        [Test]
        public void TheViewMatrixMovesTheWorldIntoTheCamera()
        {
            // The camera at (0, 0, 5) looking down -z: the view matrix translates the world by -5 along z.
            var view = Identity();
            view[2 * 4 + 3] = -5;
            var p = new ManagedViewProjection(view, Perspective(60, 1f, 0.3f, 100), 0, 0, 100, 100);
            Assert.That(p.TryProject(0, 0, 0, out var x, out var y), Is.True, "the origin is 5 in front of it");
            Assert.That(x, Is.EqualTo(50).Within(1e-3));
            Assert.That(p.TryProject(0, 0, 6, out _, out _), Is.False, "behind it");
        }

        [Test]
        public void AnOrthographicCameraProjectsLinearly()
        {
            var p = new ManagedViewProjection(Identity(), Orthographic(5, 1f, 0.1f, 100), 0, 0, 200, 200);
            Assert.That(p.TryProject(5, 5, -3, out var x, out var y), Is.True);
            Assert.That(x, Is.EqualTo(200).Within(1e-3));
            Assert.That(y, Is.EqualTo(200).Within(1e-3));
            Assert.That(p.TryProject(0, 0, 1, out _, out _), Is.False);
        }

        [Test]
        public void ADegenerateMatrixGivesNoPositionNotNaN()
        {
            var zero = new float[16];
            var p = new ManagedViewProjection(Identity(), zero, 0, 0, 100, 100);
            Assert.That(p.TryProject(0, 0, -5, out _, out _), Is.False);
            var nan = Identity();
            nan[0] = float.NaN;
            var q = new ManagedViewProjection(Identity(), nan, 0, 0, 100, 100);
            Assert.That(q.TryProject(1, 1, -5, out _, out _), Is.False);
        }

        [Test]
        public void ABadMatrixSizeIsRefused()
        {
            Assert.Throws<ArgumentException>(() => new ManagedViewProjection(new float[3], Identity(), 0, 0, 1, 1));
            Assert.Throws<ArgumentException>(() => new ManagedViewProjection(Identity(), null, 0, 0, 1, 1));
        }
    }
}
