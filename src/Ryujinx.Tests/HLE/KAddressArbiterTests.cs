using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.Tests.HLE
{
    public class KAddressArbiterTests
    {
        [Test]
        public void SelectThreadsToSignalUsesCurrentPriorityAndStableFifo()
        {
            Waiter first = new("first", priority: 5);
            Waiter second = new("second", priority: 10);
            Waiter third = new("third", priority: 20);
            Waiter[] queued = [first, second, third];

            // Inheritance can alter a waiting thread's DynamicPriority after
            // it was inserted, so selection must evaluate it at wake time.
            third.Priority = 4;
            second.Priority = 5;

            Waiter[] selected = KAddressArbiter.SelectThreadsToSignal(
                queued,
                count: 2,
                waiter => waiter.Priority);

            Assert.That(selected, Is.EqualTo(new[] { third, first }));
        }

        [Test]
        public void SelectThreadsToSignalZeroCountSelectsAllByCurrentPriority()
        {
            Waiter first = new("first", priority: 2);
            Waiter second = new("second", priority: 1);

            Waiter[] selected = KAddressArbiter.SelectThreadsToSignal(
                [first, second],
                count: 0,
                waiter => waiter.Priority);

            Assert.That(selected, Is.EqualTo(new[] { second, first }));
        }

        private sealed class Waiter(string name, int priority)
        {
            public string Name { get; } = name;
            public int Priority { get; set; } = priority;

            public override string ToString() => Name;
        }
    }
}
