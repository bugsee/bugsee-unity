using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class XcodeBuildPhaseListTests
    {
        const string Target =
            "\t\t7F4E059F /* GameAssembly */ = {\n" +
            "\t\t\tisa = PBXNativeTarget;\n" +
            "\t\t\tbuildPhases = (\n" +
            "\t\t\t\t7F4E059C /* Sources */,\n" +
            "\t\t\t\tC62A2A42 /* ShellScript */,\n" +
            "\t\t\t\tC62A2A42 /* ShellScript */,\n" +
            "\t\t\t);\n" +
            "\t\t\tname = GameAssembly;\n" +
            "\t\t};\n";

        [Test]
        public void SecondMentionOfAPhaseIsDropped()
        {
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(Target, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(fixedText, Is.EqualTo(Target.Replace("\t\t\t\tC62A2A42 /* ShellScript */,\n\t\t\t\tC62A2A42 /* ShellScript */,\n", "\t\t\t\tC62A2A42 /* ShellScript */,\n")));
        }

        [Test]
        public void ListWithoutRepeatsIsLeftAlone()
        {
            string text = Target.Replace("\t\t\t\tC62A2A42 /* ShellScript */,\n\t\t\t\tC62A2A42 /* ShellScript */,\n", "\t\t\t\tC62A2A42 /* ShellScript */,\n");
            Assert.That(XcodeBuildPhaseList.RemoveDuplicates(text, out int removed), Is.EqualTo(text));
            Assert.That(removed, Is.EqualTo(0));
        }

        [Test]
        public void SamePhaseInDifferentTargetsStays()
        {
            string text = Target + Target.Replace("GameAssembly", "Other").Replace("\t\t\t\tC62A2A42 /* ShellScript */,\n\t\t\t\tC62A2A42 /* ShellScript */,\n", "\t\t\t\tC62A2A42 /* ShellScript */,\n");
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(fixedText, "C62A2A42 /\\* ShellScript \\*/,").Count, Is.EqualTo(2));
        }
    }
}
