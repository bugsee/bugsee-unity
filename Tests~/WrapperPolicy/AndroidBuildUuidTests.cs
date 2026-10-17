using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidBuildUuidTests
    {
        // Expected values come from the SDK's own algorithm: java.util.UUID.nameUUIDFromBytes(
        // (token + "\u001f" + versionName + "\u001f" + versionCode).getBytes(UTF_8)).
        [TestCase("sampletoken", "1.2.3", "45", "0f5c0792-affb-3962-af95-d6e66c5828e2")]
        [TestCase("tok", "0.1.0", "1", "5d8da6e2-8821-3d5b-88f9-b76fd271ed4e")]
        [TestCase("tökén", "1.0-é", "2147483647", "62fe7fa8-3bfd-3ff4-b6d5-141c321f240c")]
        public void Matches_the_android_sdks_synthesized_build_id(string token, string versionName, string versionCode, string expected)
        {
            Assert.That(AndroidBuildUuid.Synthesize(token, versionName, versionCode), Is.EqualTo(expected));
        }

        [Test]
        public void Whitespace_around_the_token_and_code_does_not_change_the_id()
        {
            Assert.That(AndroidBuildUuid.Synthesize(" tok\n", "0.1.0", " 1 "), Is.EqualTo("5d8da6e2-8821-3d5b-88f9-b76fd271ed4e"));
        }

        [Test]
        public void Missing_token_or_code_yields_none_and_a_missing_name_counts_as_empty()
        {
            Assert.That(AndroidBuildUuid.Synthesize(null, "1", "1"), Is.Null);
            Assert.That(AndroidBuildUuid.Synthesize("", "1", "1"), Is.Null);
            Assert.That(AndroidBuildUuid.Synthesize("t", "1", null), Is.Null);
            Assert.That(AndroidBuildUuid.Synthesize("t", null, "1"), Is.EqualTo(AndroidBuildUuid.Synthesize("t", "", "1")));
        }
    }
}
