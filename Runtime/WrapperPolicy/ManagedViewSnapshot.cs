using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// What the main thread keeps of the Unity scene for one <c>vh</c> request: parallel arrays of plain values in walk
    /// order (a parent always before its children), with no <see cref="ManagedViewNode"/>, no per-node list and no
    /// concatenated strings. The main thread only appends to it, one node per call; once it is handed to the worker the
    /// main thread never touches it again. <see cref="ToTree"/> and the JSON write run on the worker.
    /// </summary>
    public sealed class ManagedViewSnapshot
    {
        public const int NoParent = -1;
        /// <summary>The component list a node writes is cut to this many type names (the first one still names the node).</summary>
        public const int MaxShownComponents = 8;

        int _count;
        long[] _id;
        string[] _idText;
        int[] _parent;
        string[] _className;
        string[] _name;
        int[] _componentStart;
        byte[] _componentCount;
        bool[] _hasBounds;
        bool[] _truncated;
        float[] _box; // x, y, width, height per node
        readonly List<string> _components;

        public ManagedViewSnapshot(int capacity = 512)
        {
            if (capacity < 4) capacity = 4;
            // About two shown names per node: the list starts at what it always did, and at two per node for a snapshot sized for a big scene.
            _components = new List<string>(capacity > ManagedViewSnapshotSizing.MinCapacity ? capacity * 2 : 256);
            _id = new long[capacity];
            _idText = new string[capacity];
            _parent = new int[capacity];
            _className = new string[capacity];
            _name = new string[capacity];
            _componentStart = new int[capacity];
            _componentCount = new byte[capacity];
            _hasBounds = new bool[capacity];
            _truncated = new bool[capacity];
            _box = new float[capacity * 4];
        }

        public int Count => _count;

        /// <summary>True when any node is marked as cut short.</summary>
        public bool AnyTruncated
        {
            get
            {
                for (var i = 0; i < _count; i++) if (_truncated[i]) return true;
                return false;
            }
        }

        /// <summary>The secure rectangles ([l, t, r, b] each, Unity render pixels, top-left origin, the space of the node boxes: converted from the registry's native pixels before they get here), copied on the main thread when the walk ends.</summary>
        public int[] SecureRects;
        public int SecureCount;
        /// <summary>
        /// Adds the rectangles ([l, t, r, b] each) not already in <see cref="SecureRects"/>. The walk spans several frames, so it is
        /// called at its start, at every slice and when it ends: a node is protected if any rectangle that existed while the walk ran
        /// covers it, also one that was removed or moved before the walk finished.
        /// </summary>
        public void AddSecureRects(int[] rects, int count)
        {
            if (rects == null) return;
            if (count * 4 > rects.Length) count = rects.Length / 4;
            for (var i = 0; i < count; i++)
            {
                var known = false;
                for (var k = 0; k < SecureCount && !known; k++)
                    known = SecureRects[k * 4] == rects[i * 4] && SecureRects[k * 4 + 1] == rects[i * 4 + 1]
                        && SecureRects[k * 4 + 2] == rects[i * 4 + 2] && SecureRects[k * 4 + 3] == rects[i * 4 + 3];
                if (known) continue;
                if (SecureRects == null || SecureRects.Length < (SecureCount + 1) * 4)
                    Array.Resize(ref SecureRects, Math.Max(16, (SecureCount + 1) * 8));
                Array.Copy(rects, i * 4, SecureRects, SecureCount * 4, 4);
                SecureCount++;
            }
        }

        int _renderWidth, _renderHeight;

        /// <summary>
        /// Notes the render size the rectangles were converted for in this slice; true when an earlier slice of the walk used another one.
        /// The rectangles kept from that slice are in the old space, so the caller covers everything (<see cref="AddSecureRects"/> of a
        /// rectangle beyond any screen).
        /// </summary>
        public bool NoteRenderSize(int width, int height)
        {
            var changed = _renderWidth != 0 && (_renderWidth != width || _renderHeight != height);
            _renderWidth = width;
            _renderHeight = height;
            return changed;
        }

        /// <summary>1 on Android, the screen scale on iOS; read on the main thread when the walk starts.</summary>
        public float PixelsPerNativeUnit = 1f;

        /// <summary>
        /// False under <c>SceneGraphCapture.NamesOnly</c>: the boxes are still computed (a node's name is withheld when its box overlaps a secure
        /// rectangle) but no node writes <c>bounds</c>.
        /// </summary>
        public bool EmitBounds = true;

        /// <summary>Set by the walk when the whole tree was read (the scene may still be read after it): a capture cut off later does not mark the tree truncated.</summary>
        public bool TreeComplete;

        /// <summary>The 3D scene read after the tree (<c>SceneGraphCapture.All</c> only); null when none is captured.</summary>
        public ManagedViewSceneSnapshot Scene;

        /// <summary>
        /// Appends a node and returns its index. <paramref name="parent"/> is an index already returned, or
        /// <see cref="NoParent"/> for the root (the first node).
        /// </summary>
        public int Add(int parent, long id, string idText, string className, string name)
        {
            if (parent >= _count || (parent == NoParent) != (_count == 0))
                throw new ArgumentOutOfRangeException(nameof(parent), "a node follows its parent; only the first node has none");
            if (_count == _id.Length) Grow();
            var i = _count++;
            _id[i] = id;
            _idText[i] = idText;
            _parent[i] = parent;
            _className[i] = className;
            _name[i] = name;
            _componentStart[i] = _components.Count;
            _componentCount[i] = 0;
            _hasBounds[i] = false;
            _truncated[i] = false;
            var b = i * 4;
            _box[b] = _box[b + 1] = _box[b + 2] = _box[b + 3] = 0f;
            return i;
        }

        /// <summary>Appends a component type name to the node just added (up to <see cref="MaxShownComponents"/>; the rest are ignored).</summary>
        public void AddComponent(int node, string typeName)
        {
            if (node != _count - 1) throw new ArgumentException("components go to the node just added", nameof(node));
            if (_componentCount[node] >= MaxShownComponents) return;
            _components.Add(typeName);
            _componentCount[node]++;
        }

        public void SetBounds(int node, float x, float y, float width, float height)
        {
            _hasBounds[node] = true;
            var b = node * 4;
            _box[b] = x; _box[b + 1] = y; _box[b + 2] = width; _box[b + 3] = height;
        }

        public void SetTruncated(int node) => _truncated[node] = true;

        void Grow()
        {
            var n = _id.Length * 2;
            Array.Resize(ref _id, n);
            Array.Resize(ref _idText, n);
            Array.Resize(ref _parent, n);
            Array.Resize(ref _className, n);
            Array.Resize(ref _name, n);
            Array.Resize(ref _componentStart, n);
            Array.Resize(ref _componentCount, n);
            Array.Resize(ref _hasBounds, n);
            Array.Resize(ref _truncated, n);
            Array.Resize(ref _box, n * 4);
        }

        /// <summary>
        /// The tree the writer takes. Worker-side: this allocates a node and a child list per node and joins each node's
        /// component names with a comma, exactly as the single-threaded walk used to, so the JSON is the same.
        /// </summary>
        public ManagedViewNode ToTree()
        {
            if (_count == 0) return null;
            var nodes = new ManagedViewNode[_count];
            for (var i = 0; i < _count; i++)
            {
                var node = new ManagedViewNode
                {
                    Id = _id[i], IdText = _idText[i], ClassName = _className[i], Name = _name[i], Truncated = _truncated[i],
                };
                var shown = _componentCount[i];
                if (shown == 1) node.Components = _components[_componentStart[i]];
                else if (shown > 1)
                {
                    var parts = new string[shown];
                    _components.CopyTo(_componentStart[i], parts, 0, shown);
                    node.Components = string.Join(",", parts);
                }
                if (_hasBounds[i])
                {
                    var b = i * 4;
                    node.HasBounds = true;
                    node.X = _box[b]; node.Y = _box[b + 1]; node.Width = _box[b + 2]; node.Height = _box[b + 3];
                }

                nodes[i] = node;
                if (_parent[i] != NoParent) nodes[_parent[i]].Children.Add(node);
            }

            return nodes[0];
        }
    }
}
