using System.Collections.Generic;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class NetworkEventLaunchBufferTests
    {
        [Test]
        public void Delete_generation_launch_bump_invalidates_pending_delete()
        {
            var captured = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            DeleteCollectedDataLaunchGeneration.BumpForLaunch();
            Assert.That(DeleteCollectedDataLaunchGeneration.ShouldRunDelete(captured), Is.False);
        }

        [Test]
        public void Delete_generation_runs_when_generation_matches_current()
        {
            var captured = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            Assert.That(DeleteCollectedDataLaunchGeneration.ShouldRunDelete(captured), Is.True);
            DeleteCollectedDataLaunchGeneration.BumpForLaunch();
            var afterLaunch = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            Assert.That(DeleteCollectedDataLaunchGeneration.ShouldRunDelete(afterLaunch), Is.True);
        }

        [Test]
        public void Breadcrumb_buffer_flush_preserves_insertion_order_after_launch()
        {
            var delivered = new List<IosPendingBreadcrumb>();
            var buffer = new NetworkEventLaunchBuffer<IosPendingBreadcrumb>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.Enqueue(new IosPendingBreadcrumb { Category = "a", Message = "1", IosLevel = 1 });
            buffer.Enqueue(new IosPendingBreadcrumb { Category = "b", Message = "2", IosLevel = 2 });
            buffer.SetPhase(NetworkLaunchPhase.Launched);

            Assert.That(delivered.Count, Is.EqualTo(2));
            Assert.That(delivered[0].Category, Is.EqualTo("a"));
            Assert.That(delivered[1].Category, Is.EqualTo("b"));
        }

        [Test]
        public void Lifecycle_stopped_after_new_launch_does_not_clear_pending()
        {
            var delivered = new List<int>();
            var buffer = new NetworkEventLaunchBuffer<int>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.SetPhase(NetworkLaunchPhase.Launched);
            buffer.SetPhase(NetworkLaunchPhase.Stopped);
            buffer.BeginNewLaunchCycle();
            buffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            buffer.Enqueue(42);
            buffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Stopped);
            buffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Launched);

            Assert.That(delivered, Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void Breadcrumb_buffer_stop_clears_pending_without_flush()
        {
            var delivered = new List<IosPendingBreadcrumb>();
            var buffer = new NetworkEventLaunchBuffer<IosPendingBreadcrumb>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.Enqueue(new IosPendingBreadcrumb { Category = "a", Message = "1", IosLevel = 1 });
            buffer.SetPhase(NetworkLaunchPhase.Stopped);
            buffer.SetPhase(NetworkLaunchPhase.Launched);

            Assert.That(delivered, Is.Empty);
        }

        [Test]
        public void Flush_preserves_insertion_order_after_launch()
        {
            var delivered = new List<int>();
            var buffer = new NetworkEventLaunchBuffer<int>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.Enqueue(1);
            buffer.Enqueue(2);
            buffer.SetPhase(NetworkLaunchPhase.Launched);

            Assert.That(delivered, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Stop_clears_pending_without_flush()
        {
            var delivered = new List<int>();
            var buffer = new NetworkEventLaunchBuffer<int>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.Enqueue(1);
            buffer.Enqueue(2);
            buffer.SetPhase(NetworkLaunchPhase.Stopped);
            buffer.SetPhase(NetworkLaunchPhase.Launched);

            Assert.That(delivered, Is.Empty);
        }

        [Test]
        public void Events_after_stop_are_not_delivered_until_a_new_launch_flush()
        {
            var delivered = new List<int>();
            var buffer = new NetworkEventLaunchBuffer<int>();
            buffer.SetSubmitHandler(v => delivered.Add(v));

            buffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            buffer.Enqueue(10);
            buffer.SetPhase(NetworkLaunchPhase.Launched);
            Assert.That(delivered, Is.EqualTo(new[] { 10 }));

            delivered.Clear();
            buffer.SetPhase(NetworkLaunchPhase.Stopped);
            buffer.Enqueue(20);
            Assert.That(delivered, Is.Empty);

            buffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            buffer.Enqueue(30);
            buffer.SetPhase(NetworkLaunchPhase.Launched);
            Assert.That(delivered, Is.EqualTo(new[] { 30 }));
        }
    }
}
