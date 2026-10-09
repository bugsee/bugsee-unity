using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class MarkedGradleBlockTests
    {
        const string Anchor = "plugins {";
        const string Marked = "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin";

        [Test]
        public void Second_apply_is_a_no_op_and_a_commented_anchor_is_refused()
        {
            string once = MarkedGradleBlock.Apply(Anchor + "\n}\n", Anchor, Marked);
            string twice = MarkedGradleBlock.Apply(once, Anchor, Marked);
            Assert.That(twice, Is.EqualTo(once));

            Assert.Throws<InvalidOperationException>(() =>
                MarkedGradleBlock.Apply("plugins { /*\n}\n", Anchor, Marked));
        }

        [Test]
        public void Legacy_bugsee_marker_and_plugin_line_is_not_duplicated()
        {
            const string legacy =
                Anchor + "\n    // Bugsee Gradle plugin\n    id 'com.bugsee.android.gradle' version '4.0.8' apply false\n}\n";
            string once = MarkedGradleBlock.Apply(legacy, Anchor, Marked);
            Assert.That(once, Is.EqualTo(legacy));
        }
    }
}
