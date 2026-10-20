using System.Text.Json;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedViewTreeTests
    {
        static ManagedViewNode Node(long id, string className, string name, float x, float y, float w, float h, params ManagedViewNode[] children)
        {
            var node = new ManagedViewNode
            {
                Id = id, ClassName = className, Name = name, HasBounds = true, X = x, Y = y, Width = w, Height = h,
            };
            node.Children.AddRange(children);
            return node;
        }

        static ManagedViewNode Container(long id, string name, params ManagedViewNode[] children)
        {
            var node = new ManagedViewNode { Id = id, ClassName = "GameObject", Name = name };
            node.Children.AddRange(children);
            return node;
        }

        static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Test]
        public void TheReplyIsOneRootObjectInTheShapeTheViewerReads()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 1080, 2400,
                Node(7, "Image", "Background", 10, 20, 300, 400));
            var json = ManagedViewTreeJson.Write(root, null, 0, 1f, false);
            var doc = Parse(json);
            Assert.That(doc.GetProperty("class_name").GetString(), Is.EqualTo("UnityPlayer"));
            Assert.That(doc.GetProperty("bounds").GetArrayLength(), Is.EqualTo(4));
            var child = doc.GetProperty("subitems")[0];
            Assert.That(child.GetProperty("id").GetString(), Is.EqualTo("7"));
            Assert.That(child.GetProperty("class_name").GetString(), Is.EqualTo("Image"));
            Assert.That(child.GetProperty("bounds")[2].GetDouble(), Is.EqualTo(300));
            Assert.That(child.GetProperty("options").GetProperty("name").GetString(), Is.EqualTo("Background"));
            Assert.That(ManagedViewTreeJson.DataType, Is.EqualTo("vh"));
        }

        [Test]
        public void BoundsAreDividedByTheScaleForNativeUnits()
        {
            // iOS reports points: 3 pixels per point.
            var root = Node(0, "UnityPlayer", null, 0, 0, 1170, 2532, Node(1, "Image", "a", 30, 60, 300, 150));
            var doc = Parse(ManagedViewTreeJson.Write(root, null, 0, 3f, false));
            Assert.That(doc.GetProperty("bounds")[2].GetDouble(), Is.EqualTo(390));
            var child = doc.GetProperty("subitems")[0].GetProperty("bounds");
            Assert.That(new[] { child[0].GetDouble(), child[1].GetDouble(), child[2].GetDouble(), child[3].GetDouble() },
                Is.EqualTo(new[] { 10d, 20d, 100d, 50d }));
        }

        [Test]
        public void ANodeWithoutGeometryTakesTheUnionOfItsChildren()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 100, 100,
                Container(1, "group", Node(2, "Image", "a", 10, 10, 20, 20), Node(3, "Image", "b", 50, 40, 10, 30)));
            var group = Parse(ManagedViewTreeJson.Write(root, null, 0, 1f, false)).GetProperty("subitems")[0].GetProperty("bounds");
            Assert.That(new[] { group[0].GetDouble(), group[1].GetDouble(), group[2].GetDouble(), group[3].GetDouble() },
                Is.EqualTo(new[] { 10d, 10d, 50d, 60d }));
        }

        [Test]
        public void ANodeWithNoGeometryAnywhereReportsAnEmptyBox()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 100, 100, Container(1, "empty"));
            var bounds = Parse(ManagedViewTreeJson.Write(root, null, 0, 1f, false)).GetProperty("subitems")[0].GetProperty("bounds");
            Assert.That(bounds.GetArrayLength(), Is.EqualTo(4));
            Assert.That(bounds[2].GetDouble(), Is.EqualTo(0));
        }

        [Test]
        public void ANodeOverASecureRectangleLosesItsNameAndComponents()
        {
            var field = Node(5, "InputField", "PasswordField", 100, 100, 200, 50);
            field.Components = "Image,InputField";
            var other = Node(6, "Image", "Logo", 500, 500, 50, 50);
            other.Components = "Image";
            var root = Node(0, "UnityPlayer", null, 0, 0, 1000, 1000, field, other);
            var json = ManagedViewTreeJson.Write(root, new[] { 150, 110, 400, 160 }, 1, 1f, false);
            Assert.That(json, Does.Not.Contain("PasswordField"));
            var items = Parse(json).GetProperty("subitems");
            Assert.That(items[0].TryGetProperty("options", out _), Is.False, "nothing but geometry is left of a covered node");
            var clear = items[1].GetProperty("options");
            Assert.That(clear.GetProperty("name").GetString(), Is.EqualTo("Logo"));
            Assert.That(clear.GetProperty("components").GetString(), Is.EqualTo("Image"));
            Assert.That(json, Does.Not.Contain("bugsee_protected_view"));
        }

        [Test]
        public void AContainerOverTheRectangleDoesNotCoverItsFarChildren()
        {
            var near = Node(3, "Text", "Balance", 5, 5, 10, 10);
            var far = Node(4, "Image", "Decoration", 200, 200, 10, 10);
            var panel = Node(2, "Panel", "Account", 0, 0, 300, 300, near, far);
            var root = Node(0, "UnityPlayer", null, 0, 0, 400, 400, panel);
            var json = ManagedViewTreeJson.Write(root, new[] { 0, 0, 20, 20 }, 1, 1f, false);
            Assert.That(json, Does.Not.Contain("Account"));
            Assert.That(json, Does.Not.Contain("Balance"));
            Assert.That(json, Does.Contain("Decoration"), "the full-screen container must not blank the whole tree");
        }

        [Test]
        public void ANodeWithUnknownGeometryIsProtectedWhileASecureRectangleExists()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 100, 100, Container(1, "Unplaced"));
            Assert.That(ManagedViewTreeJson.Write(root, new[] { 0, 0, 1, 1 }, 1, 1f, false), Does.Not.Contain("Unplaced"));
            Assert.That(ManagedViewTreeJson.Write(root, null, 0, 1f, false), Does.Contain("Unplaced"));
        }

        [Test]
        public void ARectangleThatOnlyTouchesAnEdgeDoesNotProtect()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 100, 100, Node(1, "Image", "Edge", 10, 10, 10, 10));
            Assert.That(ManagedViewTreeJson.Write(root, new[] { 20, 10, 30, 20 }, 1, 1f, false), Does.Contain("Edge"));
            Assert.That(ManagedViewTreeJson.Write(root, new[] { 19, 10, 30, 20 }, 1, 1f, false), Does.Not.Contain("Edge"));
        }

        [Test]
        public void NamesAreEscapedSoTheReplyStaysValidJson()
        {
            var hostile = "q\"uote\\ and\nnewline \u0001 \ud800 end";
            var root = Node(0, "UnityPlayer", null, 0, 0, 10, 10, Node(1, "Image", hostile, 0, 0, 1, 1));
            var json = ManagedViewTreeJson.Write(root, null, 0, 1f, false);
            // A lone surrogate cannot be read back as a string, so it must have been written escaped (valid JSON, no U+FFFD guess).
            Assert.That(json, Does.Contain("\\ud800"));
            Assert.That(json, Does.Contain("q\\\"uote\\\\ and\\nnewline \\u0001"));
            Assert.DoesNotThrow(() => JsonDocument.Parse(json));
        }

        [Test]
        public void NumbersIgnoreTheCultureAndNeverOverflowTheirShape()
        {
            var old = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            try
            {
                var root = Node(0, "UnityPlayer", null, 0, 0, 10.5f, float.NaN, Node(1, "Image", "a", -0.001f, 1.234f, float.PositiveInfinity, 1));
                var json = ManagedViewTreeJson.Write(root, null, 0, 1f, false);
                Assert.That(json, Does.Contain("[0,0,10.5,0]"));
                Assert.That(json, Does.Contain("[0,1.23,0,1]"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = old;
            }
        }

        [Test]
        public void ABadScaleFallsBackToOne()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 100, 100);
            foreach (var scale in new[] { 0f, -2f, float.NaN, float.PositiveInfinity })
                Assert.That(Parse(ManagedViewTreeJson.Write(root, null, 0, scale, false)).GetProperty("bounds")[2].GetDouble(), Is.EqualTo(100));
        }

        [Test]
        public void ATruncatedWalkIsMarkedOnTheRoot()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 10, 10);
            Assert.That(Parse(ManagedViewTreeJson.Write(root, null, 0, 1f, true)).GetProperty("options").GetProperty("unity_truncated").GetBoolean(), Is.True);
            Assert.That(ManagedViewTreeJson.Write(root, null, 0, 1f, false), Does.Not.Contain("unity_truncated"));
        }

        [Test]
        public void ANullRootHasNothingToWrite()
        {
            Assert.That(ManagedViewTreeJson.Write(null, null, 0, 1f, false), Is.Null);
        }

        [Test]
        public void ARectangleCountBeyondTheBufferIsClamped()
        {
            var root = Node(0, "UnityPlayer", null, 0, 0, 10, 10, Node(1, "Image", "x", 0, 0, 1, 1));
            Assert.DoesNotThrow(() => ManagedViewTreeJson.Write(root, new[] { 0, 0, 5, 5 }, 99, 1f, false));
        }

        [Test]
        public void ADeepTreeDoesNotOverflowTheWriter()
        {
            var node = Node(1000, "Image", "leaf", 0, 0, 1, 1);
            for (var i = 0; i < 5000; i++) node = Container(i, "n" + i, node);
            string json = null;
            Assert.DoesNotThrow(() => json = ManagedViewTreeJson.Write(node, null, 0, 1f, false));
            Assert.That(json, Does.StartWith("{"));
        }

        [Test]
        public void TheBudgetStopsAtTheNodeCountAndSaysSo()
        {
            var budget = new ManagedViewBudget(maxNodes: 3);
            Assert.That(budget.TryTake(1) && budget.TryTake(2) && budget.TryTake(2), Is.True);
            Assert.That(budget.Truncated, Is.False);
            Assert.That(budget.IsExhausted, Is.True);
            Assert.That(budget.TryTake(2), Is.False);
            Assert.That(budget.Truncated, Is.True);
            Assert.That(budget.Taken, Is.EqualTo(3));
        }

        [Test]
        public void TheBudgetRefusesTooDeepNodesButKeepsTakingShallowOnes()
        {
            var budget = new ManagedViewBudget(maxNodes: 100, maxDepth: 3);
            Assert.That(budget.TryTake(3), Is.True);
            Assert.That(budget.TryTake(4), Is.False);
            Assert.That(budget.Truncated, Is.True);
            Assert.That(budget.IsExhausted, Is.False, "a deep branch must not stop its shallow siblings");
            Assert.That(budget.TryTake(2), Is.True);
        }

        [Test]
        public void TheBudgetStopsOnceTheDeadlinePasses()
        {
            var expired = false;
            var budget = new ManagedViewBudget(expired: () => expired);
            Assert.That(budget.TryTake(1), Is.True);
            expired = true;
            Assert.That(budget.IsExhausted, Is.True);
            Assert.That(budget.TryTake(1), Is.False);
            Assert.That(budget.Truncated, Is.True);
        }
    }
}
