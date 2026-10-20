using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class XcodeBuildPhaseListTests
    {
        const string Line = "\t\t\t\tC62A2A42 /* ShellScript */,\n";

        static string Target(string phases) =>
            "\t\t7F4E059F /* GameAssembly */ = {\n" +
            "\t\t\tisa = PBXNativeTarget;\n" +
            "\t\t\tbuildPhases = (\n" +
            "\t\t\t\t7F4E059C /* Sources */,\n" +
            phases +
            "\t\t\t);\n" +
            "\t\t\tname = GameAssembly;\n" +
            "\t\t};\n";

        static int Count(string text, string id) => Regex.Matches(text, id + " /\\*").Count;

        [Test]
        public void SecondMentionOfAPhaseIsDropped()
        {
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(Target(Line + Line), out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(fixedText, Is.EqualTo(Target(Line)));
        }

        [Test]
        public void ListWithoutRepeatsIsLeftAlone()
        {
            string text = Target(Line);
            Assert.That(XcodeBuildPhaseList.RemoveDuplicates(text, out int removed), Is.EqualTo(text));
            Assert.That(removed, Is.EqualTo(0));
        }

        [Test]
        public void SamePhaseInDifferentTargetsStays()
        {
            string text = Target(Line + Line) + Target(Line);
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(Count(fixedText, "C62A2A42"), Is.EqualTo(2));
        }

        [Test]
        public void CrLfEndingsAreKept()
        {
            string text = Target(Line + Line).Replace("\n", "\r\n");
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(fixedText, Is.EqualTo(Target(Line).Replace("\n", "\r\n")));
        }

        [Test]
        public void ThreeCopiesKeepTheFirstPosition()
        {
            string other = "\t\t\t\tAAAA1111 /* CopyFiles */,\n";
            string text = Target(Line + other + Line + Line);
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(fixedText, Is.EqualTo(Target(Line + other)));
        }

        [Test]
        public void ParenthesesInACommentDoNotHideTheList()
        {
            string paren = "\t\t\t\tBBBB2222 /* Run Script (copy) */,\n";
            string text = Target(paren + Line + paren + Line);
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(fixedText, Is.EqualTo(Target(paren + Line)));
        }

        [Test]
        public void OtherListsWithRepeatedIdsAreUntouched()
        {
            string text =
                "\t\t\tfiles = (\n" + Line + Line + "\t\t\t);\n" +
                "\t\t\tchildren = (\n" + Line + Line + "\t\t\t);\n";
            Assert.That(XcodeBuildPhaseList.RemoveDuplicates(text, out int removed), Is.EqualTo(text));
            Assert.That(removed, Is.EqualTo(0));
        }

        [Test]
        public void SecondPassRemovesNothing()
        {
            string once = XcodeBuildPhaseList.RemoveDuplicates(Target(Line + Line), out _);
            Assert.That(XcodeBuildPhaseList.RemoveDuplicates(once, out int removed), Is.EqualTo(once));
            Assert.That(removed, Is.EqualTo(0));
        }

        [Test]
        public void UnityExportFixtureIsRepaired()
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Unity2022.3.62f3-append-duplicate-phase", "GameAssemblyTarget.pbxproj.txt");
            string text = File.ReadAllText(path);
            Assert.That(Count(text, "C62A2A42F32E085EF849CF0B"), Is.EqualTo(2));
            string fixedText = XcodeBuildPhaseList.RemoveDuplicates(text, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(Count(fixedText, "C62A2A42F32E085EF849CF0B"), Is.EqualTo(1));
            Assert.That(fixedText, Does.Contain("7F4E059E2717216D00A2CBE4 /* CopyFiles */,"));
        }

        [Test]
        public void LinkRemovesDuplicatesBeforeItWritesTheProject()
        {
            string dir = TestContext.CurrentContext.TestDirectory;
            string root = dir;
            while (root != null && !File.Exists(Path.Combine(root, "package.json"))) root = Path.GetDirectoryName(root);
            if (root == null) Assert.Ignore("package root not found");
            string source = File.ReadAllText(Path.Combine(root, "Editor", "BugseeIosSpmPostProcess.cs"));
            int remove = source.IndexOf("XcodeBuildPhaseList.RemoveDuplicates(", System.StringComparison.Ordinal);
            int write = source.IndexOf("File.WriteAllText(projectPath", System.StringComparison.Ordinal);
            Assert.That(remove, Is.GreaterThan(0));
            Assert.That(remove, Is.LessThan(write));
        }
    }
}
