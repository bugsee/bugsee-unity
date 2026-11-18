using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// <c>Camera.WorldToScreenPoint</c> as plain arithmetic over matrices copied once per capture. Each call of the Unity method is a
    /// native call, and a node read 8 of them (one per box corner), which was the larger part of the main-thread cost of a node; with the
    /// matrices copied the projection is a few dozen multiplications. The result is the same pixel position (Unity screen pixels, origin
    /// bottom left) and the same "in front of the camera" test (<c>z</c> = distance along the view direction, positive in front).
    /// </summary>
    public sealed class ManagedViewProjection
    {
        const float Half = 0.5f;
        readonly float[] _viewProjection = new float[16]; // row-major: clip = M * (x, y, z, 1)
        readonly float[] _viewZ = new float[4];            // row 2 of the view matrix: view z = dot(row, (x, y, z, 1)); the camera looks down -z
        readonly float _pixelX, _pixelY, _pixelWidth, _pixelHeight;

        /// <param name="view">The camera's world-to-camera matrix, row-major (m00, m01, m02, m03, m10, ...).</param>
        /// <param name="projection">The camera's projection matrix, row-major.</param>
        /// <param name="pixelX">The camera's pixelRect: origin and size in screen pixels.</param>
        public ManagedViewProjection(float[] view, float[] projection, float pixelX, float pixelY, float pixelWidth, float pixelHeight)
        {
            if (view == null || view.Length != 16) throw new ArgumentException("a 4x4 matrix", nameof(view));
            if (projection == null || projection.Length != 16) throw new ArgumentException("a 4x4 matrix", nameof(projection));
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++) sum += projection[r * 4 + k] * view[k * 4 + c];
                _viewProjection[r * 4 + c] = sum;
            }

            for (var c = 0; c < 4; c++) _viewZ[c] = view[2 * 4 + c];
            _pixelX = pixelX; _pixelY = pixelY; _pixelWidth = pixelWidth; _pixelHeight = pixelHeight;
        }

        /// <summary>
        /// False when the point is not in front of the camera, or the result is not a number (a degenerate matrix): the caller leaves
        /// the node without geometry, as with <c>!(z &gt; 0)</c> and NaN before.
        /// </summary>
        public bool TryProject(float x, float y, float z, out float screenX, out float screenY)
        {
            screenX = screenY = 0f;
            var depth = -(_viewZ[0] * x + _viewZ[1] * y + _viewZ[2] * z + _viewZ[3]);
            if (!(depth > 0f)) return false;
            var m = _viewProjection;
            var cx = m[0] * x + m[1] * y + m[2] * z + m[3];
            var cy = m[4] * x + m[5] * y + m[6] * z + m[7];
            var cw = m[12] * x + m[13] * y + m[14] * z + m[15];
            if (!(cw != 0f)) return false;
            var nx = cx / cw;
            var ny = cy / cw;
            if (float.IsNaN(nx) || float.IsNaN(ny)) return false;
            screenX = _pixelX + Half * (nx + 1f) * _pixelWidth;
            screenY = _pixelY + Half * (ny + 1f) * _pixelHeight;
            return true;
        }
    }
}
