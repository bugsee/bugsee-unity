namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The four world corners of a RectTransform from its local rect and its localToWorldMatrix, as plain floats so it can be tested
    /// without Unity. It is the arithmetic of <c>RectTransform.GetWorldCorners</c> (managed code in every supported Unity line: the local
    /// corners from the rect's x, y, xMax and yMax, each run through <c>Matrix4x4.MultiplyPoint</c>), minus the icalls that call makes
    /// beyond the two reads the caller still does: on 2021.3 and 2022.3 GetWorldCorners reads <c>this.transform</c> before
    /// <c>localToWorldMatrix</c>, one native call for an object that already is the Transform (6000.0 has dropped it). Corner order is the
    /// one GetWorldCorners writes: bottom left, top left, top right, bottom right.
    /// </summary>
    public static class ManagedViewCorners
    {
        /// <summary>
        /// Writes the corners into <paramref name="into"/> (12 floats: x, y, z of corner 0..3). The matrix is passed field by field
        /// (mRC: row R, column C) because Unity's Matrix4x4 is not available here and an indexer or an array copy would cost more
        /// than the call it replaces. <paramref name="xMax"/> and <paramref name="yMax"/> are <c>rect.xMax</c> / <c>rect.yMax</c>
        /// (x + width, y + height), as GetLocalCorners reads them.
        /// </summary>
        public static void World(
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23,
            float m30, float m31, float m32, float m33,
            float x, float y, float xMax, float yMax, float[] into)
        {
            Point(m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33, x, y, into, 0);
            Point(m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33, x, yMax, into, 3);
            Point(m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33, xMax, yMax, into, 6);
            Point(m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33, xMax, y, into, 9);
        }

        // Matrix4x4.MultiplyPoint with z = 0 (the same operation order, including the division by w), written term for term.
        static void Point(
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23,
            float m30, float m31, float m32, float m33,
            float px, float py, float[] into, int at)
        {
            const float pz = 0f;
            var rx = (m00 * px + m01 * py + m02 * pz) + m03;
            var ry = (m10 * px + m11 * py + m12 * pz) + m13;
            var rz = (m20 * px + m21 * py + m22 * pz) + m23;
            var w = (m30 * px + m31 * py + m32 * pz) + m33;
            w = 1F / w;
            into[at] = rx * w;
            into[at + 1] = ry * w;
            into[at + 2] = rz * w;
        }
    }
}
