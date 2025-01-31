﻿using NUnit.Framework;

namespace MouseUnSnag.Configuration.Tests
{
    [TestFixture]
    public class OptionsTests
    {
        [Test]
        public void EventTest()
        {
            var options = new Options();
            var events = 0;

            options.ConfigChanged += (sender, args) => { events += 1; };

            options.Jump = !options.Jump;
            Assert.AreEqual(1, events);

            options.Unstick = !options.Unstick;
            Assert.AreEqual(2, events);

            options.Wrap = !options.Wrap;
            Assert.AreEqual(3, events);

        }
    }
}
