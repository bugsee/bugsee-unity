using System.Collections.Generic;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// ManagedViewCorners must give the corners RectTransform.GetWorldCorners gives. The expected values below were produced by
    /// Unity 2021.3.45f1 itself (an editor batch run that built each RectTransform, then logged its localToWorldMatrix, rect and the
    /// result of GetWorldCorners with round-trip float formatting): plain, offset pivot with a scale, rotated, mirrored, mirrored and
    /// rotated, tilted in 3D, a rotated and scaled parent with a rotated child, and a zero-size rect.
    /// </summary>
    public class ManagedViewCornersTests
    {
        public sealed class Known { public string Name; public float[] M; public float X, Y, XMax, YMax; public float[] Expected; public override string ToString() { return Name; } }

        static Known Case(string name, float[] m, float x, float y, float xMax, float yMax, float[] expected)
        {
            return new Known { Name = name, M = m, X = x, Y = y, XMax = xMax, YMax = yMax, Expected = expected };
        }

        static IEnumerable<Known> Cases()
        {
            // plain
            yield return Case("plain",
                new[] { 1f, 0f, 0f, 10f, 0f, 1f, 0f, 20f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                -100f, -50f, 100f, 50f,
                new[] { -90f, -30f, 0f, -90f, 70f, 0f, 110f, 70f, 0f, 110f, -30f, 0f, });
            // pivot
            yield return Case("pivot",
                new[] { 2f, 0f, 0f, -15.5f, 0f, 3f, 0f, 7.25f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                0f, -48f, 320f, 0f,
                new[] { -15.5f, -136.75f, 0f, -15.5f, 7.25f, 0f, 624.5f, 7.25f, 0f, 624.5f, -136.75f, 0f, });
            // rotz
            yield return Case("rotz",
                new[] { 0.8660254f, -0.50000006f, 0f, 40f, 0.50000006f, 0.8660254f, 0f, -30f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                -100f, -50f, 100f, 50f,
                new[] { -21.6025352f, -123.301277f, 0f, -71.60254f, -36.69874f, 0f, 101.602539f, 63.3012772f, 0f, 151.602539f, -23.3012619f, 0f, });
            // neg
            yield return Case("neg",
                new[] { -1f, 0f, 0f, 5f, 0f, 1f, 0f, 5f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                -30.0000019f, -42f, 70f, 18f,
                new[] { 35f, -37f, 0f, 35f, 23f, 0f, -65f, 23f, 0f, -65f, -37f, 0f, });
            // negboth
            yield return Case("negboth",
                new[] { -1.91260958f, 0.146185845f, 0f, 0f, -0.5847434f, -0.4781524f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                -50f, -30f, 50f, 30f,
                new[] { 91.2449f, 43.58174f, 0f, 100.016052f, 14.8925972f, 0f, -91.2449f, -43.58174f, 0f, -100.016052f, -14.8925972f, 0f, });
            // tilt
            yield return Case("tilt",
                new[] { 0.661910832f, -0.471692681f, 0.5825634f, 0f, 0.742403865f, 0.5198368f, -0.42261827f, 0f, -0.103491947f, 0.712232947f, 0.694272041f, 0f, 0f, 0f, 0f, 1f, },
                -128f, -64f, 128f, 64f,
                new[] { -54.5362549f, -128.297241f, -32.3359375f, -114.912918f, -61.75814f, 58.82988f, 54.5362549f, 128.297241f, 32.3359375f, 114.912918f, 61.75814f, -58.82988f, });
            // child
            yield return Case("child",
                new[] { 0.587661266f, -0.256018341f, 0.09209085f, 142.055176f, 0.409033328f, 1.7070868f, -0.0920908451f, 78.58479f, -0.0296955854f, 0.3263518f, 0.9848077f, 0f, 0f, 0f, 0f, 1f, },
                -16f, -36f, 64f, 4f,
                new[] { 141.869263f, 10.5851364f, -11.2735357f, 131.628525f, 78.86861f, 1.78053653f, 178.641418f, 111.591278f, -0.5951103f, 188.882156f, 43.3078f, -13.6491823f, });
            // zero
            yield return Case("zero",
                new[] { 1f, 0f, 0f, 3f, 0f, 1f, 0f, 4f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, },
                0f, 0f, 0f, 0f,
                new[] { 3f, 4f, 0f, 3f, 4f, 0f, 3f, 4f, 0f, 3f, 4f, 0f, });
        }

        static float[] Run(Known k)
        {
            var m = k.M;
            var into = new float[12];
            ManagedViewCorners.World(
                m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15],
                k.X, k.Y, k.XMax, k.YMax, into);
            return into;
        }

        [TestCaseSource(nameof(Cases))]
        public void TheCornersAreTheOnesGetWorldCornersGave(Known k)
        {
            var got = Run(k);
            for (var i = 0; i < 12; i++)
                Assert.That(got[i], Is.EqualTo(k.Expected[i]).Within(1).Ulps, k.Name + " value " + i);
        }

        [Test]
        public void TheOrderIsBottomLeftTopLeftTopRightBottomRight()
        {
            var into = new float[12];
            ManagedViewCorners.World(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1, -2, 3, 4, into);
            Assert.That(into, Is.EqualTo(new float[] { -1, -2, 0, -1, 4, 0, 3, 4, 0, 3, -2, 0 }));
        }

        [Test]
        public void ARotationByNinetyDegreesTurnsTheBottomLeftToTheBottomRight()
        {
            var into = new float[12];
            // Rotation about z by 90 degrees: x' = -y, y' = x.
            ManagedViewCorners.World(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1, -2, 3, 4, into);
            Assert.That(into, Is.EqualTo(new float[] { 2, -1, 0, -4, -1, 0, -4, 3, 0, 2, 3, 0 }));
        }
    }
}
