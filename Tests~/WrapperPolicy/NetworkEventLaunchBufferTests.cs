using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class NetworkEventLaunchBufferTests
    {
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
