using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// What the tree walk hands to the 3D scene read so the scene read does not walk the transforms a second time: the first renderer of every
    /// object the tree visited, in the tree's order (which is the pre-order the scene read used to walk), with the object's id and layer, and
    /// where the tree stopped when the budget cut it, so the scene read walks only the rest. Pure (the renderer and node types are the
    /// caller's), so the data flow is unit-tested without Unity.
    ///
    /// The tree's visits are a prefix of the pre-order walk of the loaded scenes, except where it skipped something it cannot resume from (a
    /// level below the depth limit, an object whose read threw, a destroyed parent): <see cref="Broken"/>, and the scene read then walks
    /// everything itself, exactly as before. A budget cut (the node count or the deadline, both permanent once reached) is a clean prefix: the
    /// pre-order remainder is recorded as a stack of (node, next child) entries plus the index of the first scene the walk did not start.
    /// </summary>
    public sealed class ManagedViewSceneFeed<TRenderer, TNode>
    {
        public struct Entry
        {
            public TRenderer Renderer;
            public int Id;
            public int Layer;
        }

        readonly List<Entry> _renderers = new List<Entry>(256);
        readonly List<TNode> _resumeNodes = new List<TNode>();
        readonly List<int> _resumeNext = new List<int>();

        /// <summary>The first renderer of each object the tree visited, in visit order.</summary>
        public List<Entry> Renderers => _renderers;

        /// <summary>Objects the tree visited (the scene read's "units" that the tree answered).</summary>
        public int Units { get; private set; }

        /// <summary>True when the tree skipped something the scene read cannot resume from: the feed is not used and the scene is walked in full.</summary>
        public bool Broken { get; private set; }

        /// <summary>True when the budget stopped the tree: <see cref="FillResumeStack"/> and <see cref="ResumeSceneIndex"/> say what is left.</summary>
        public bool Cut { get; private set; }

        /// <summary>The index of the first scene the tree did not start (only meaningful when <see cref="Cut"/>).</summary>
        public int ResumeSceneIndex { get; private set; }

        /// <summary>The tree visited an object: <paramref name="hasRenderer"/> false for one with no renderer.</summary>
        public void Visited(bool hasRenderer, TRenderer renderer, int id, int layer)
        {
            Units++;
            if (hasRenderer) _renderers.Add(new Entry { Renderer = renderer, Id = id, Layer = layer });
        }

        /// <summary>The tree left something out that the scene read must still see and cannot resume from.</summary>
        public void MarkBroken() => Broken = true;

        /// <summary>
        /// The budget stopped the tree. True once, for the first call on a feed that is still exact: the caller then lists what is left with
        /// <see cref="AddRemaining"/> in pre-order (earliest first). False when the feed is broken or already cut.
        /// </summary>
        public bool BeginCut(int firstUnstartedScene)
        {
            if (Broken || Cut) return false;
            Cut = true;
            ResumeSceneIndex = firstUnstartedScene;
            return true;
        }

        /// <summary>One piece of the remainder, in pre-order: <paramref name="next"/> is -1 for a node not read yet (its own renderer and all its children), otherwise the index of the next child (its own renderer is in the feed).</summary>
        public void AddRemaining(TNode node, int next)
        {
            _resumeNodes.Add(node);
            _resumeNext.Add(next);
        }

        /// <summary>Appends the remainder to a walk stack (top = next to read): the reverse of the pre-order list.</summary>
        public void FillResumeStack(List<TNode> nodes, List<int> next)
        {
            for (var i = _resumeNodes.Count - 1; i >= 0; i--)
            {
                nodes.Add(_resumeNodes[i]);
                next.Add(_resumeNext[i]);
            }
        }

        public int RemainingCount => _resumeNodes.Count;
    }
}
