using System;
using System.Security.Cryptography;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Ties the app token symbols were uploaded for (BUGSEE_APP_TOKEN at build time) to the token the player launches with.
    /// Symbols, the Android synthesized build id and the iOS Run Script all follow the build token, the runtime follows the
    /// token given to <c>Bugsee.Launch</c>: when they differ, crashes reach one app and symbols sit in another, with no
    /// error anywhere. The build records a fingerprint of its token in the player, and Launch compares it with its own.
    ///
    /// The fingerprint is the first 8 bytes of SHA-256 of the trimmed token, as 16 hex characters. The token is never
    /// stored or logged, and no prefix or other reversible form of it is. A truncated hash is enough for a yes/no
    /// comparison and cannot be turned back into the token; 64 bits make an accidental equal fingerprint of two different
    /// tokens about 1 in 10^19. The token itself is in the player anyway, as the argument of Launch.
    /// </summary>
    public static class BuildTokenFingerprint
    {
        /// <summary>The Resources asset (TextAsset) the Editor writes for the duration of a build.</summary>
        public const string ResourceName = "BugseeBuildTokenFingerprint";

        public enum Verdict
        {
            /// <summary>No fingerprint was recorded (no build token, symbol upload off, an older build), or no token to compare.</summary>
            NotApplicable,
            Match,
            Mismatch,
        }

        /// <summary>The fingerprint of <paramref name="token"/>, or null for a missing or blank token.</summary>
        public static string Compute(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(token.Trim()));
            }

            var hex = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                hex.Append(hash[i].ToString("x2"));
            }

            return hex.ToString();
        }

        /// <summary>Compares the recorded fingerprint (the asset's text, may be null or padded) with the Launch token.</summary>
        public static Verdict Check(string recorded, string launchToken)
        {
            var build = recorded?.Trim();
            var launch = Compute(launchToken);
            if (string.IsNullOrEmpty(build) || launch == null)
            {
                return Verdict.NotApplicable;
            }

            return string.Equals(build, launch, StringComparison.OrdinalIgnoreCase) ? Verdict.Match : Verdict.Mismatch;
        }

        public static string MismatchMessage(string recorded, string launchToken) =>
            "The app token passed to Launch (fingerprint " + Compute(launchToken) + ") differs from the BUGSEE_APP_TOKEN this build was made with " +
            "(fingerprint " + recorded.Trim() + "), the token its symbols were uploaded for. If they name different apps, crashes of this " +
            "build go to the Launch token's app while the mappings and native symbols sit in the build token's app: they may not match, " +
            "and crashes stay unsymbolicated. Build with the token the app launches with, or set BUGSEE_NO_TOKEN_CHECK for the build " +
            "when the token is chosen at runtime.";
    }
}
