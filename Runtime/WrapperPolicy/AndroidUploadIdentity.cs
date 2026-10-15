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
        /// <summary>The id the mapping is keyed by.</summary>
        public string Uuid;
        /// <summary>The id the native (ELF) upload is keyed by: BUGSEE_BUILD_UUID when set, else <see cref="Uuid"/>.</summary>
        public string ElfUuid;
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
        /// The mapping is keyed by the id the app reports: the id the built app carries, else (no asset) a valid
        /// BUGSEE_BUILD_UUID the user set as the id they know the app reports, else the id the SDK synthesizes. An asset
        /// always wins over the environment: a value that differs from what the app reports could never match.
        /// BUGSEE_BUILD_UUID keeps its older meaning for the native upload (<see cref="UploadIdentity.ElfUuid"/>): any
        /// non-empty value is that upload's correlation key; without it the mapping's id is used.
        /// </summary>
        public static UploadIdentity Decide(string env, string appAssetId, string synthesized, string appAssetConflict = null)
        {
            var result = DecideMapping(env, appAssetId, synthesized);
            string envValue = string.IsNullOrWhiteSpace(env) ? null : env.Trim();
            result.ElfUuid = envValue ?? result.Uuid;
            if (appAssetConflict != null)
            {
                result.Warning = result.Warning == null ? appAssetConflict : appAssetConflict + " " + result.Warning;
            }

            return result;
        }

        static UploadIdentity DecideMapping(string env, string appAssetId, string synthesized)
        {
            string app = IsUuid(appAssetId) ? appAssetId.Trim().ToLowerInvariant() : null;
            string envValue = string.IsNullOrWhiteSpace(env) ? null : env.Trim();
            if (app != null)
            {
                string note = envValue != null && !string.Equals(app, envValue, StringComparison.OrdinalIgnoreCase)
                    ? "BUGSEE_BUILD_UUID (" + envValue + ") is not the id the app reports (" + app + "); the mapping is uploaded under the app's id and BUGSEE_BUILD_UUID is used for the native upload only."
                    : null;
                return new UploadIdentity { Uuid = app, Source = BuildIdSource.AppAsset, Warning = note };
            }

            string envNote = null;
            if (envValue != null)
            {
                if (IsUuid(envValue))
                {
                    return new UploadIdentity { Uuid = envValue.ToLowerInvariant(), Source = BuildIdSource.Environment };
                }

                envNote = "BUGSEE_BUILD_UUID is not a UUID, so it is not used as the mapping's id (it still keys the native upload).";
            }

            if (synthesized != null)
            {
                return new UploadIdentity
                {
                    Uuid = synthesized,
                    Source = BuildIdSource.Synthesized,
                    Warning = (envNote == null ? "" : envNote + " ") +
                              "The built app carries no bugsee_build_id.properties, so the SDK reports the id it derives from the app token, " +
                              "version name and version code; uploading under that id. If the app is built with a different token, version " +
                              "name or code than Player Settings, the mapping will not match it.",
                };
            }

            return new UploadIdentity { Uuid = null, Source = BuildIdSource.None, Warning = envNote };
        }

        /// <summary>Whether an APK file name is a sibling of the player "Game.apk": "Game.arm64-v8a.apk", not "GameLite.apk".</summary>
        public static bool IsSplitApkOf(string fileName, string stem)
        {
            return !string.IsNullOrEmpty(stem) && fileName != null &&
                   fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
                   fileName.Length > stem.Length + ".apk".Length + 1;
        }

        /// <summary>
        /// The id of an app delivered as several APKs (Split APKs by target architecture): the one id they all carry. Null
        /// when none carries one, and also when they differ or only some carry it (<paramref name="conflict"/> says
        /// so): no single id matches them all, so none is picked from the ones that happen to have it.
        /// </summary>
        public static string SelectAppAssetId(IList<string> idsPerApk, out string conflict)
        {
            conflict = null;
            string first = null;
            bool anyMissing = false;
            bool differ = false;
            foreach (string raw in idsPerApk)
            {
                string id = IsUuid(raw) ? raw.Trim().ToLowerInvariant() : null;
                if (id == null)
                {
                    anyMissing = true;
                }
                else if (first == null)
                {
                    first = id;
                }
                else if (first != id)
                {
                    differ = true;
                }
            }

            if (first == null)
            {
                return null;
            }

            if (differ || anyMissing)
            {
                conflict = "The APKs of this build do not all carry the same build id (" +
                           (differ ? "different ids" : "some have none") + "): crashes from some of them will not match the uploaded mapping.";
                return null;
            }

            return first;
        }

        /// <summary>Arguments of an <c>elf</c> upload of a symbols zip or a directory: <c>--uuid</c> is required by the CLI for this type.</summary>
        public static List<string> ElfArguments(string path, string version, string build, string uuid)
        {
            var args = new List<string>
            {
                "debug-files", "upload", path,
                "--type", "elf",
                "--version", version,
                "--build", build,
            };
            args.Add("--uuid");
            args.Add(uuid);
            return args;
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
