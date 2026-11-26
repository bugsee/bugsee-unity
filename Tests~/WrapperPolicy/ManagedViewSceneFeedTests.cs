using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The data flow of the 3D scene read: the tree walk records the renderers it meets and where the budget stopped it, and the scene read
    /// reads those and walks only the rest. The walkers here are a small model of ManagedViewWalker (an explicit stack of frames, a node budget,
    /// inactive subtrees skipped, the first renderer of each object); the property checked is that the fed order is the order the scene read used
    /// to produce by walking every transform itself, for every possible place the budget can stop the tree.
    /// </summary>
    public class ManagedViewSceneFeedTests
    {
        sealed class Node
        {
            public string Name;
            public bool Active = true;
            public int Renderer = -1; // -1: none
            public bool Destroyed;
            public readonly List<Node> Children = new List<Node>();
            public Node Add(string name, bool active = true, int renderer = -1)
            {
                var n = new Node { Name = name, Active = active, Renderer = renderer };
                Children.Add(n);
                return n;
            }
        }

        sealed class Scene
        {
            public readonly List<Node> Roots = new List<Node>();
        }

        // ---- the scene read as it was: every transform of every scene, pre-order, inactive subtrees skipped ----
        static List<int> FullWalk(IList<Scene> scenes, int fromScene = 0)
        {
            var result = new List<int>();
            var units = new List<Node>();
            var next = new List<int>();
            for (var i = scenes.Count - 1; i >= fromScene; i--)
                for (var k = scenes[i].Roots.Count - 1; k >= 0; k--) { units.Add(scenes[i].Roots[k]); next.Add(-1); }
            Drain(units, next, result);
            return result;
        }

        static void Drain(List<Node> units, List<int> next, List<int> result)
        {
            while (units.Count > 0)
            {
                var top = units.Count - 1;
                var t = units[top];
                if (next[top] < 0)
                {
                    if (t == null || t.Destroyed || !t.Active) { units.RemoveAt(top); next.RemoveAt(top); continue; }
                    next[top] = 0;
                    if (t.Renderer >= 0) result.Add(t.Renderer);
                    continue;
                }

                if (t == null || t.Destroyed || next[top] >= t.Children.Count) { units.RemoveAt(top); next.RemoveAt(top); continue; }
                units.Add(t.Children[next[top]++]);
                next.Add(-1);
            }
        }

        struct Frame
        {
            public Node Transform;
            public List<Node> Roots;
            public int Next;
            public int Count;
        }

        // ---- the tree walk with the feed: `budget` nodes may be taken (the scene nodes and the root cost one each, like the real walk's) ----
        static ManagedViewSceneFeed<int, Node> TreeWalk(IList<Scene> scenes, int budget, int maxDepth = 24, Action<Node> beforeVisit = null)
        {
            var feed = new ManagedViewSceneFeed<int, Node>();
            var taken = 0;
            var seen = new HashSet<Node>();
            var stack = new List<Frame>();
            var sceneIndex = 0;
            bool Exhausted() => taken >= budget;

            void Cut(Node refused)
            {
                if (!feed.BeginCut(sceneIndex)) return;
                if (refused != null) feed.AddRemaining(refused, -1);
                for (var i = stack.Count - 1; i >= 0; i--)
                {
                    var f = stack[i];
                    if (f.Roots != null)
                    {
                        for (var k = f.Next; k < f.Roots.Count; k++)
                            if (f.Roots[k] != null && !f.Roots[k].Destroyed) feed.AddRemaining(f.Roots[k], -1);
                    }
                    else if (f.Transform != null && !f.Transform.Destroyed)
                    {
                        feed.AddRemaining(f.Transform, f.Next);
                    }
                }
            }

            void Visit(Node go, int depth, int parentDepthBase)
            {
                if (go == null || go.Destroyed || !go.Active) return;
                if (!seen.Add(go)) { feed.MarkBroken(); return; }
                if (depth > maxDepth || Exhausted())
                {
                    if (Exhausted()) Cut(go); else feed.MarkBroken();
                    return;
                }

                taken++;
                feed.Visited(go.Renderer >= 0, go.Renderer, 0, 0);
                if (go.Children.Count > 0) stack.Add(new Frame { Transform = go, Next = 0, Count = go.Children.Count });
                _ = parentDepthBase;
            }

            while (true)
            {
                if (stack.Count == 0)
                {
                    if (sceneIndex >= scenes.Count) break;
                    if (Exhausted()) { Cut(null); break; }
                    taken++;
                    stack.Add(new Frame { Roots = scenes[sceneIndex++].Roots, Next = 0 });
                    continue;
                }

                var f = stack[stack.Count - 1];
                var depthOfFrame = stack.Count + 1;
                if (f.Roots != null)
                {
                    if (f.Next >= f.Roots.Count) { stack.RemoveAt(stack.Count - 1); continue; }
                    if (Exhausted()) { Cut(null); stack.RemoveAt(stack.Count - 1); continue; }
                    var root = f.Roots[f.Next++];
                    stack[stack.Count - 1] = f;
                    beforeVisit?.Invoke(root);
                    Visit(root, 3, 0);
                    continue;
                }

                if (f.Transform == null || f.Transform.Destroyed) { feed.MarkBroken(); stack.RemoveAt(stack.Count - 1); continue; }
                var count = f.Transform.Children.Count;
                if (count < f.Count) feed.MarkBroken();
                f.Count = count;
                stack[stack.Count - 1] = f;
                if (f.Next >= count) { stack.RemoveAt(stack.Count - 1); continue; }
                if (Exhausted()) { Cut(null); stack.RemoveAt(stack.Count - 1); continue; }
                var child = f.Transform.Children[f.Next++];
                stack[stack.Count - 1] = f;
                beforeVisit?.Invoke(child);
                Visit(child, depthOfFrame + 1, 0);
            }

            return feed;
        }

        // ---- the scene read with the feed ----
        static List<int> FedWalk(IList<Scene> scenes, ManagedViewSceneFeed<int, Node> feed)
        {
            var result = new List<int>();
            if (feed.Broken) return FullWalk(scenes);
            foreach (var e in feed.Renderers) result.Add(e.Renderer);
            if (feed.Cut)
            {
                var units = new List<Node>();
                var next = new List<int>();
                for (var i = scenes.Count - 1; i >= feed.ResumeSceneIndex; i--)
                    for (var k = scenes[i].Roots.Count - 1; k >= 0; k--) { units.Add(scenes[i].Roots[k]); next.Add(-1); }
                feed.FillResumeStack(units, next);
                Drain(units, next, result);
            }

            return result;
        }

        static Scene Sample()
        {
            // root A (renderer 0) { B (1) { D (2), E { F (3) } }, C inactive (4) { G (5) } }, root H { I (6), J (7) }, root K (8)
            var s = new Scene();
            var a = new Node { Name = "A", Renderer = 0 }; s.Roots.Add(a);
            var b = a.Add("B", renderer: 1); b.Add("D", renderer: 2); b.Add("E").Add("F", renderer: 3);
            a.Add("C", active: false, renderer: 4).Add("G", renderer: 5);
            var h = new Node { Name = "H" }; s.Roots.Add(h); h.Add("I", renderer: 6); h.Add("J", renderer: 7);
            s.Roots.Add(new Node { Name = "K", Renderer = 8 });
            return s;
        }

        static int CountNodes(IList<Scene> scenes)
        {
            var n = 0;
            void Go(Node x) { n++; foreach (var c in x.Children) Go(c); }
            foreach (var s in scenes) foreach (var r in s.Roots) Go(r);
            return n;
        }

        [Test]
        public void An_uncut_tree_hands_over_every_renderer_in_the_order_the_scene_read_walks()
        {
            var scenes = new List<Scene> { Sample() };
            var feed = TreeWalk(scenes, 1000);
            Assert.That(feed.Broken, Is.False);
            Assert.That(feed.Cut, Is.False);
            Assert.That(feed.Renderers.Select(e => e.Renderer), Is.EqualTo(new[] { 0, 1, 2, 3, 6, 7, 8 }));
            Assert.That(FedWalk(scenes, feed), Is.EqualTo(FullWalk(scenes)));
        }

        [Test]
        public void Units_count_only_the_objects_the_tree_visited()
        {
            var scenes = new List<Scene> { Sample() };
            Assert.That(TreeWalk(scenes, 1000).Units, Is.EqualTo(9)); // C and G are inactive
        }

        [Test]
        public void Wherever_the_budget_cuts_the_tree_the_fed_walk_equals_the_full_walk()
        {
            var scenes = new List<Scene> { Sample() };
            var total = CountNodes(scenes) + scenes.Count + 1;
            for (var budget = 0; budget <= total + 2; budget++)
            {
                var feed = TreeWalk(scenes, budget);
                Assert.That(feed.Broken, Is.False, "budget " + budget);
                Assert.That(FedWalk(scenes, feed), Is.EqualTo(FullWalk(scenes)), "budget " + budget);
            }
        }

        [Test]
        public void Several_scenes_and_a_cut_between_scenes_resume_at_the_first_scene_not_started()
        {
            var scenes = new List<Scene>();
            var id = 0;
            for (var s = 0; s < 4; s++)
            {
                var scene = new Scene();
                for (var r = 0; r < 3; r++)
                {
                    var root = new Node { Name = "r", Renderer = id++ };
                    scene.Roots.Add(root);
                    for (var c = 0; c < 3; c++) root.Add("c", renderer: id++).Add("g", active: c != 1, renderer: id++);
                }

                scenes.Add(scene);
            }

            var all = CountNodes(scenes) + scenes.Count + 1;
            for (var budget = 0; budget <= all + 1; budget++)
            {
                var feed = TreeWalk(scenes, budget);
                Assert.That(FedWalk(scenes, feed), Is.EqualTo(FullWalk(scenes)), "budget " + budget);
            }

            // a cut that stops before the second scene starts leaves the scenes from index 1 to the scene read
            var cutEarly = TreeWalk(scenes, 19); // the first scene's whole cost: its scene node and 6 active nodes in each of 3 roots
            Assert.That(cutEarly.Cut, Is.True);
            Assert.That(cutEarly.ResumeSceneIndex, Is.EqualTo(1));
            Assert.That(cutEarly.RemainingCount, Is.EqualTo(0));
        }

        [Test]
        public void A_cut_lists_the_open_frames_from_the_innermost_out_and_the_scene_roots_last()
        {
            var scenes = new List<Scene> { Sample() };
            // budget 3: the scene node, A and B are taken; B's first child is not
            var feed = TreeWalk(scenes, 3);
            Assert.That(feed.Cut, Is.True);
            var nodes = new List<Node>();
            var next = new List<int>();
            feed.FillResumeStack(nodes, next);
            // the stack's top is the next to read: B's children from 0, then A's from 1, then the scene's roots H and K (K is the bottom)
            Assert.That(nodes.Select(n => n.Name), Is.EqualTo(new[] { "K", "H", "A", "B" }));
            Assert.That(next, Is.EqualTo(new[] { -1, -1, 1, 0 }));
        }

        [Test]
        public void A_node_the_budget_refused_is_read_before_the_open_frames()
        {
            var feed = new ManagedViewSceneFeed<int, string>();
            Assert.That(feed.BeginCut(1), Is.True);
            feed.AddRemaining("refused", -1);
            feed.AddRemaining("parent", 3);
            feed.AddRemaining("root", -1);
            var nodes = new List<string>();
            var next = new List<int>();
            feed.FillResumeStack(nodes, next);
            Assert.That(nodes, Is.EqualTo(new[] { "root", "parent", "refused" }));
            Assert.That(next, Is.EqualTo(new[] { -1, 3, -1 }));
        }

        [Test]
        public void A_second_cut_is_ignored()
        {
            var feed = new ManagedViewSceneFeed<int, string>();
            Assert.That(feed.BeginCut(2), Is.True);
            feed.AddRemaining("a", -1);
            Assert.That(feed.BeginCut(0), Is.False);
            Assert.That(feed.ResumeSceneIndex, Is.EqualTo(2));
            Assert.That(feed.RemainingCount, Is.EqualTo(1));
        }

        [Test]
        public void A_broken_tree_cannot_be_cut_and_the_scene_read_walks_everything()
        {
            var feed = new ManagedViewSceneFeed<int, string>();
            feed.MarkBroken();
            Assert.That(feed.BeginCut(0), Is.False);
            Assert.That(feed.Cut, Is.False);
            Assert.That(feed.Broken, Is.True);
        }

        [Test]
        public void Levels_below_the_depth_limit_break_the_feed_and_the_walk_is_the_full_one()
        {
            var s = new Scene();
            var n = new Node { Name = "r", Renderer = 0 };
            s.Roots.Add(n);
            var cur = n;
            for (var i = 1; i <= 30; i++) cur = cur.Add("d" + i, renderer: i);
            var scenes = new List<Scene> { s };
            var feed = TreeWalk(scenes, 1000);
            Assert.That(feed.Broken, Is.True);
            Assert.That(feed.Renderers.Count, Is.LessThan(31)); // the tree stopped at its depth limit: the feed is a part, so it is not used
            Assert.That(FullWalk(scenes), Has.Count.EqualTo(31)); // the scene read sees the objects below the tree's depth limit
        }

        [Test]
        public void A_parent_destroyed_between_slices_breaks_the_feed()
        {
            var scenes = new List<Scene> { Sample() };
            Node doomed = null;
            var feed = TreeWalk(scenes, 1000, 24, node =>
            {
                if (node.Name == "B") doomed = node;
                if (node.Name == "D") doomed.Destroyed = true; // B is destroyed after the walk entered it, before its next child
            });
            Assert.That(feed.Broken, Is.True);
        }

        [Test]
        public void An_object_met_twice_because_it_moved_between_slices_breaks_the_feed()
        {
            // X was visited under A, then reparented under the later sibling B: the walk meets it again and skips it (the _seen set)
            var s = new Scene();
            var a = new Node { Name = "A" }; s.Roots.Add(a);
            var b = new Node { Name = "B" }; s.Roots.Add(b);
            var x = a.Add("X", renderer: 1);
            var scenes = new List<Scene> { s };
            var feed = TreeWalk(scenes, 1000, 24, node =>
            {
                if (node.Name == "B") b.Children.Add(x); // moved under B before B's children are read
            });
            Assert.That(feed.Broken, Is.True);
        }

        [Test]
        public void A_sibling_removed_before_the_walk_position_breaks_the_feed()
        {
            // E0..E9 under a container; E2 is destroyed (removed from its parent) after E4 was read: the indexes shift and E5 would be skipped
            var s = new Scene();
            var box = new Node { Name = "box" }; s.Roots.Add(box);
            for (var i = 0; i < 10; i++) box.Add("E" + i, renderer: i);
            var scenes = new List<Scene> { s };
            var feed = TreeWalk(scenes, 1000, 24, node =>
            {
                if (node.Name == "E5") box.Children.RemoveAt(2);
            });
            Assert.That(feed.Broken, Is.True);
            // and the walk the scene read falls back to sees every object that is left
            Assert.That(FullWalk(scenes), Has.Count.EqualTo(9));
        }

        [Test]
        public void Objects_without_renderers_and_inactive_objects_add_no_entry()
        {
            var feed = new ManagedViewSceneFeed<string, string>();
            feed.Visited(false, null, 1, 0);
            feed.Visited(true, "r", 2, 5);
            Assert.That(feed.Units, Is.EqualTo(2));
            Assert.That(feed.Renderers, Has.Count.EqualTo(1));
            Assert.That(feed.Renderers[0].Id, Is.EqualTo(2));
            Assert.That(feed.Renderers[0].Layer, Is.EqualTo(5));
        }

        [Test]
        public void A_tree_of_many_thousands_of_objects_cut_at_the_node_limit_hands_over_the_rest_in_order()
        {
            var scene = new Scene();
            var id = 0;
            for (var r = 0; r < 20; r++)
            {
                var root = new Node { Name = "r" + r, Renderer = id++ };
                scene.Roots.Add(root);
                for (var c = 0; c < 20; c++)
                {
                    var child = root.Add("c", renderer: id++);
                    for (var g = 0; g < 20; g++) child.Add("g", renderer: g % 3 == 0 ? -1 : id++);
                }
            }

            var scenes = new List<Scene> { scene };
            Assert.That(CountNodes(scenes), Is.GreaterThan(8000));
            var feed = TreeWalk(scenes, ManagedViewBudget.DefaultMaxNodes);
            Assert.That(feed.Cut, Is.True);
            Assert.That(feed.Units, Is.LessThanOrEqualTo(ManagedViewBudget.DefaultMaxNodes));
            Assert.That(FedWalk(scenes, feed), Is.EqualTo(FullWalk(scenes)));
        }
    }
}
