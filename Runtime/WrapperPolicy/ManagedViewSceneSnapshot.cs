using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What kind of renderer drew a scene object (the <c>kind</c> of the scene spec). Code identifiers only.</summary>
    public enum SceneObjectKind : byte
    {
        Other = 0,
        Mesh = 1,
        Skinned = 2,
        Sprite = 3,
        Particles = 4,
        Line = 5,
        Tilemap = 6,
        Ui = 7,
    }

    /// <summary>
    /// What the main thread reads of the 3D scene for one <c>vh</c> request, as plain values in Unity's own space (left-handed, Y up,
    /// cameras look along +Z): the cameras, and one row per renderer or world-space canvas with its local-to-world matrix and local bounds.
    /// The main thread only appends (a call per camera or object, no allocation per object); everything else (the handedness conversion,
    /// the view-volume selection, the priority order, the JSON) is <see cref="ManagedViewSceneBuilder"/> and
    /// <see cref="ManagedViewSceneJson"/>, which run on the worker once the snapshot is handed over.
    /// </summary>
    public sealed class ManagedViewSceneSnapshot
    {
        /// <summary>Why a scene carries no objects on purpose (the <c>skipped</c> key of the scene spec).</summary>
        public const string SkippedNoCamera = "no_camera";
        public const string SkippedTimeBudget = "time_budget";
        public const string SkippedSizeBudget = "size_budget";
        public const string SkippedError = "error";

        // ---- cameras ----
        int _cameraCount;
        int[] _cameraId = new int[8];
        float[] _cameraPose = new float[8 * 7];        // position xyz, rotation xyzw (Unity world)
        float[] _cameraView = new float[8 * 16];       // worldToCameraMatrix, row-major
        float[] _cameraProjection = new float[8 * 16]; // projectionMatrix, row-major
        float[] _cameraScalars = new float[8 * 8];     // near, far, depth, rect x y w h (bottom-left origin), unused
        int[] _cameraMask = new int[8];
        byte[] _cameraFlags = new byte[8];             // 1 main, 2 renders into a texture, 4 orthographic

        public int CameraCount => _cameraCount;

        /// <summary>Forgets the cameras read so far (they are read again when the objects are done, so the selection uses the cameras of the end of the read).</summary>
        public void ClearCameras() => _cameraCount = 0;

        /// <param name="id">The id of the camera's GameObject (the id of its tree node).</param>
        /// <param name="pose">position x, y, z then rotation x, y, z, w (Unity world space).</param>
        /// <param name="view">The camera's worldToCameraMatrix, row-major (Unity's m00, m01, ...).</param>
        /// <param name="projection">The camera's projectionMatrix, row-major.</param>
        /// <param name="rect">The camera's normalized rect, origin bottom left: x, y, width, height.</param>
        public void AddCamera(
            int id, float[] pose, float[] view, float[] projection, float nearClip, float farClip, float depth, float[] rect,
            int cullingMask, bool isMain, bool rendersToTexture, bool orthographic)
        {
            if (_cameraCount == _cameraId.Length)
            {
                var n = _cameraCount * 2;
                Array.Resize(ref _cameraId, n);
                Array.Resize(ref _cameraPose, n * 7);
                Array.Resize(ref _cameraView, n * 16);
                Array.Resize(ref _cameraProjection, n * 16);
                Array.Resize(ref _cameraScalars, n * 8);
                Array.Resize(ref _cameraMask, n);
                Array.Resize(ref _cameraFlags, n);
            }

            var i = _cameraCount++;
            _cameraId[i] = id;
            Array.Copy(pose, 0, _cameraPose, i * 7, 7);
            Array.Copy(view, 0, _cameraView, i * 16, 16);
            Array.Copy(projection, 0, _cameraProjection, i * 16, 16);
            var s = i * 8;
            _cameraScalars[s] = nearClip; _cameraScalars[s + 1] = farClip; _cameraScalars[s + 2] = depth;
            _cameraScalars[s + 3] = rect[0]; _cameraScalars[s + 4] = rect[1]; _cameraScalars[s + 5] = rect[2]; _cameraScalars[s + 6] = rect[3];
            _cameraMask[i] = cullingMask;
            _cameraFlags[i] = (byte)((isMain ? 1 : 0) | (rendersToTexture ? 2 : 0) | (orthographic ? 4 : 0));
        }

        internal int CameraId(int i) => _cameraId[i];
        internal float CameraPose(int i, int k) => _cameraPose[i * 7 + k];
        internal float CameraView(int i, int k) => _cameraView[i * 16 + k];
        internal float CameraProjection(int i, int k) => _cameraProjection[i * 16 + k];
        internal float CameraScalar(int i, int k) => _cameraScalars[i * 8 + k];
        internal int CameraMask(int i) => _cameraMask[i];
        internal bool CameraIsMain(int i) => (_cameraFlags[i] & 1) != 0;
        internal bool CameraRendersToTexture(int i) => (_cameraFlags[i] & 2) != 0;
        internal bool CameraIsOrthographic(int i) => (_cameraFlags[i] & 4) != 0;

        // ---- objects ----
        int _objectCount;
        int[] _objectId = new int[256];
        int[] _objectLayer = new int[256];
        int[] _objectCamera = new int[256];
        byte[] _objectKind = new byte[256];
        float[] _objectMatrix = new float[256 * 12];  // rows 0..2 of the local-to-world matrix, row-major (the last row is 0 0 0 1)
        float[] _objectBox = new float[256 * 6];      // local bounds: center xyz, size xyz (unscaled)

        public int ObjectCount => _objectCount;

        /// <param name="id">The id of the object's GameObject (the id of its tree node).</param>
        /// <param name="drawnBy">The id of the camera a screen-space-camera canvas is drawn by, 0 for none.</param>
        /// <param name="localToWorld">Row-major 4x4 (only the first three rows are kept).</param>
        public void AddObject(
            int id, SceneObjectKind kind, int layer, int drawnBy, float[] localToWorld,
            float centerX, float centerY, float centerZ, float sizeX, float sizeY, float sizeZ)
        {
            if (_objectCount == _objectId.Length)
            {
                var n = _objectCount * 2;
                Array.Resize(ref _objectId, n);
                Array.Resize(ref _objectLayer, n);
                Array.Resize(ref _objectCamera, n);
                Array.Resize(ref _objectKind, n);
                Array.Resize(ref _objectMatrix, n * 12);
                Array.Resize(ref _objectBox, n * 6);
            }

            var i = _objectCount++;
            _objectId[i] = id;
            _objectLayer[i] = layer;
            _objectCamera[i] = drawnBy;
            _objectKind[i] = (byte)kind;
            Array.Copy(localToWorld, 0, _objectMatrix, i * 12, 12);
            var b = i * 6;
            _objectBox[b] = centerX; _objectBox[b + 1] = centerY; _objectBox[b + 2] = centerZ;
            _objectBox[b + 3] = sizeX; _objectBox[b + 4] = sizeY; _objectBox[b + 5] = sizeZ;
        }

        internal int ObjectId(int i) => _objectId[i];
        internal int ObjectLayer(int i) => _objectLayer[i];
        internal int ObjectCamera(int i) => _objectCamera[i];
        internal SceneObjectKind ObjectKind(int i) => (SceneObjectKind)_objectKind[i];
        internal float ObjectMatrix(int i, int k) => _objectMatrix[i * 12 + k];
        internal float ObjectBox(int i, int k) => _objectBox[i * 6 + k];

        // ---- document-level values ----
        /// <summary>Renderers and canvases the walk enumerated before it looked at any of them.</summary>
        public int Seen;
        /// <summary>
        /// True when the read stopped before every object was looked at (the time budget or the read limit): the objects that are here are
        /// a part of the scene and the number of qualified ones is not known.
        /// </summary>
        public bool Incomplete;
        /// <summary>The main-thread time the scene read took in total, in ms (diagnostics).</summary>
        public double MainMs;
        /// <summary>Diagnostics: units (sub-trees) read and renderers looked at so far.</summary>
        public int ReadUnits, ReadRenderers;
        /// <summary>Set when the main thread knows the scene carries no objects on purpose; one of the <c>Skipped*</c> constants.</summary>
        public string Skipped;
        /// <summary>The frame counter when the first object was read; negative when unknown.</summary>
        public int Frame = -1;
        /// <summary>The simulation speed at the capture (0 paused, 1 normal); negative when unknown.</summary>
        public float TimeScale = -1f;
        /// <summary>Project layer names by layer number (0..31), null or empty for a layer without a name.</summary>
        public string[] LayerNames;
    }
}
