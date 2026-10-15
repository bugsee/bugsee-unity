using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    public enum BuildIdSource
    {
        /// <summary>BUGSEE_BUILD_UUID.</summary>
        Environment,
        /// <summary>assets/bugsee_build_id.properties read from the built APK/AAB: what the app reports.</summary>
        AppAsset,
        /// <summary>What the SDK derives itself when the app has no asset: <see cref="AndroidBuildUuid"/>.</summary>
        Synthesized,
        /// <summary>Nothing known (no token): a random id, keyed by nothing the app can report.</summary>
        None,
    }

    public sealed class UploadIdentity
    {
        public string Uuid;
        public BuildIdSource Source;
        /// <summary>Why the id may not match what the app reports; shown when a mapping is uploaded under it.</summary>
        public string Warning;
    }

    /// <summary>
    /// Which build id an Android symbol upload is keyed by. The id that matters is the one the app reports at runtime,
    /// so what Gradle actually produced is read back (the built APK/AAB's asset) instead of being inferred from whether
    /// the Gradle block was added to the build script: that block can degrade at configuration time (a user script
    /// replacing assets), and a mapping uploaded under another id never deobfuscates. The id file the block also writes
    /// under launcher/build/bugsee/ is deliberately not used: Gradle reuses that directory across builds, so it can
    /// belong to an earlier build.
    /// </summary>
    public static class AndroidUploadIdentity
    {
        static readonly Regex Uuid = new Regex(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

        public static bool IsUuid(string text) => text != null && Uuid.IsMatch(text.Trim());

        /// <summary>The id in a <c>bugsee_build_id.properties</c> file (<c>bugsee.build_id=&lt;uuid&gt;</c>), or null.</summary>
        public static string ParseAssetProperties(string text)
        {
            if (text == null)
            {
                return null;
            }

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal) || !line.StartsWith("bugsee.build_id", StringComparison.Ordinal))
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim() == "bugsee.build_id")
                {
                    string value = line.Substring(eq + 1).Trim();
                    return IsUuid(value) ? value.ToLowerInvariant() : null;
                }
            }

            return null;
        }

        /// <summary>
        /// Priority: BUGSEE_BUILD_UUID (an explicit choice), the id the app carries, the id the SDK synthesizes. <paramref name="appAssetId"/> is the one the app reports, so a different BUGSEE_BUILD_UUID is
        /// followed but called out.
        /// </summary>
        public static UploadIdentity Decide(string env, string appAssetId, string synthesized)
        {
            string app = IsUuid(appAssetId) ? appAssetId.Trim().ToLowerInvariant() : null;
            if (!string.IsNullOrWhiteSpace(env))
            {
                string value = env.Trim();
                string warning = app != null && !string.Equals(app, value, StringComparison.OrdinalIgnoreCase)
                    ? "BUGSEE_BUILD_UUID (" + value + ") differs from the id the app reports (" + app + "): crashes will not match this upload."
                    : null;
                return new UploadIdentity { Uuid = value, Source = BuildIdSource.Environment, Warning = warning };
            }

            if (app != null)
            {
                return new UploadIdentity { Uuid = app, Source = BuildIdSource.AppAsset };
            }

            if (synthesized != null)
            {
                return new UploadIdentity
                {
                    Uuid = synthesized,
                    Source = BuildIdSource.Synthesized,
                    Warning = "The built app carries no bugsee_build_id.properties, so the SDK reports the id it derives from the app token, " +
                              "version name and version code; uploading under that id. If the app is built with a different token, version " +
                              "name or code than Player Settings (split APKs, versionCodeOverride), set BUGSEE_BUILD_UUID or rebuild.",
                };
            }

            return new UploadIdentity { Uuid = null, Source = BuildIdSource.None };
        }

        /// <summary>Extra <c>debug-files upload --type proguard</c> arguments: the id (always explicit) and the launcher icon.</summary>
        public static List<string> MappingArguments(string uuid, string icon)
        {
            var args = new List<string>();
            if (!string.IsNullOrEmpty(uuid))
            {
                args.Add("--uuid");
                args.Add(uuid);
            }

            if (!string.IsNullOrEmpty(icon))
            {
                args.Add("--icon");
                args.Add(icon);
            }

            return args;
        }
    }
}
