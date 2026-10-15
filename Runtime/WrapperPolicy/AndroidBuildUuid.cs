using System;
using System.Security.Cryptography;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The build identity the Bugsee Android SDK derives on its own when the app is not built by the Bugsee Gradle
    /// plugin (no <c>bugsee_build_id.properties</c> asset, no <c>com.bugsee.android.BUILD_UUID</c> meta-data): the
    /// third channel of <c>BugseeEnvironment.getApplicationBuildId</c> (SDK 7.0.0-beta13+), the MD5 name-based UUID
    /// of <c>appToken + 0x1F + versionName + 0x1F + versionCode</c> (Java's <c>UUID.nameUUIDFromBytes</c>).
    ///
    /// Unity 2021.3 and 2022.3 cannot run the Gradle plugin (it needs AGP 8.6+), so a mapping or native symbol
    /// upload from those editors has to be keyed by this value, or the server can never match it to a crash.
    /// </summary>
    public static class AndroidBuildUuid
    {
        const char Separator = '\u001f';

        /// <summary>The SDK's synthesized build id, or null when the token or version code is missing.</summary>
        public static string Synthesize(string appToken, string versionName, string versionCode)
        {
            if (string.IsNullOrEmpty(appToken) || string.IsNullOrEmpty(versionCode))
            {
                return null;
            }

            string input = appToken.Trim() + Separator + (versionName ?? "") + Separator + versionCode.Trim();
            byte[] hash;
            using (var md5 = MD5.Create())
            {
                hash = md5.ComputeHash(new UTF8Encoding(false).GetBytes(input));
            }

            // Java's nameUUIDFromBytes: version 3, IETF variant, rendered big-endian.
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            var hex = new StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                {
                    hex.Append('-');
                }

                hex.Append(hash[i].ToString("x2"));
            }

            return hex.ToString();
        }
    }
}
