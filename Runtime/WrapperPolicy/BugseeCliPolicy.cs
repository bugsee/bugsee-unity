using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>Where a resolved <c>bugsee-cli</c> came from.</summary>
    public enum CliSource
    {
        None,
        /// <summary>BUGSEE_CLI_PATH.</summary>
        Explicit,
        /// <summary>A directory on the editor's own PATH.</summary>
        Path,
        /// <summary>A directory the installers and package managers use, which the editor's PATH often lacks.</summary>
        KnownDirectory,
        /// <summary>The package's download cache.</summary>
        Cache,
        /// <summary>Downloaded for this build.</summary>
        Downloaded,
    }

    /// <summary>Inputs of <see cref="BugseeCliPolicy.Resolve"/>: the host, the environment and the package's choices.</summary>
    public sealed class CliSettings
    {
        /// <summary>BUGSEE_CLI_PATH: a file, or a bare command name to look up on PATH.</summary>
        public string ExplicitPath;
        public string PathEnv;
        public string Home;
        /// <summary>%LOCALAPPDATA% (Windows installers' target); null elsewhere.</summary>
        public string LocalAppData;
        public bool IsWindows;
        /// <summary>The release to download when none is installed.</summary>
        public string Version = BugseeCliPolicy.DefaultVersion;
        /// <summary>An installed CLI older than this is passed over.</summary>
        public string MinVersion = BugseeCliPolicy.MinVersion;
        /// <summary>Release triple of this host; null when none is published.</summary>
        public string Triple;
        public bool DownloadAllowed;
        /// <summary>Why a download is not attempted, for the message when nothing is found.</summary>
        public string DownloadDisabledNote = "Downloading bugsee-cli is disabled (BUGSEE_NO_CLI_DOWNLOAD is set).";
        /// <summary>Directories a download is cached under, in order of preference.</summary>
        public IList<string> CacheDirectories = new List<string>();
    }

    /// <summary>The file system, the process runner and the downloader, injected so the policy is testable.</summary>
    public sealed class CliProbe
    {
        public Func<string, bool> FileExists;
        /// <summary>Runs <c>path --version</c>: the version it reports ("0.8.1"), or null when it does not run.</summary>
        public Func<string, string> QueryVersion;
        /// <summary>Downloads, verifies and unpacks the release once into a staging location; returns the binary's path. May throw.</summary>
        public Func<string, string> Fetch;
        /// <summary>Installs the staged binary under a cache directory (atomically, keeping a copy another build installed); returns the final path. May throw.</summary>
        public Func<string, string, string, string> Install;
        /// <summary>Removes what <see cref="Fetch"/> staged.</summary>
        public Action<string> Discard;
    }

    public sealed class CliResolution
    {
        public string Path;
        public CliSource Source;
        public string Version;
        /// <summary>What was skipped or failed on the way, for the message when nothing was found.</summary>
        public readonly List<string> Notes = new List<string>();

        public bool Found => Path != null;
    }

    /// <summary>
    /// Finds the <c>bugsee-cli</c> binary the Editor symbol uploads run. The Unity Editor is usually started from the
    /// Hub or the Finder, not from a shell, so the PATH it inherits lacks the places the CLI is installed (Homebrew,
    /// <c>~/.local/bin</c>, ...): a bare <c>bugsee-cli</c> then fails with "Cannot find the specified file". Order:
    /// BUGSEE_CLI_PATH, the PATH, the well-known install directories, the package's cache, and (when allowed) a
    /// checksum-verified download of the pinned release from download.bugsee.com, as the Bugsee Gradle plugin does.
    /// </summary>
    public static class BugseeCliPolicy
    {
        /// <summary>The release downloaded when none is installed. Same default as the Bugsee Gradle plugin 4.0.8.</summary>
        public const string DefaultVersion = "0.8.1";

        /// <summary>
        /// Oldest installed CLI the Editor uses as is (the Android and standalone uploads, which send ELF files).
        /// 0.8.1 upgrades a stored symbol table to DWARF without --force; the uploads never pass --force, so on 0.8.0
        /// a re-upload of richer symbols under the same build id keeps the poorer copy. A too-old CLI is passed over
        /// and the pinned release is downloaded, which costs one cached download.
        /// </summary>
        public const string MinVersion = "0.8.1";

        /// <summary>
        /// Oldest CLI the iOS Run Script accepts (baked into it). It uploads only dSYMs and the il2cpp linemap, never
        /// ELF, so the 0.8.1 upgrade does not matter there; 0.8.0 validates LineNumberMappings.json before packing it.
        /// The script never downloads, so a higher floor would only stop the upload.
        /// </summary>
        public const string IosMinVersion = "0.8.0";

        public const string DefaultBaseUrl = "https://download.bugsee.com/cli";

        public const string CliName = "bugsee-cli";

        /// <summary>
        /// SHA-256 of the default release's archives, pinned so the default download does not rest on the download host
        /// alone (checked against download.bugsee.com's own .sha256 files when pinned). Null for any other version,
        /// which is verified against the published sidecar only.
        /// </summary>
        public static string KnownSha256(string version, string triple)
        {
            if (version != DefaultVersion)
            {
                return null;
            }

            switch (triple)
            {
                case "aarch64-apple-darwin": return "0ef257f16ecd5d4666590a355ab20c1d6b44d94f1c17aea4b357a21ed96a7ecf";
                case "x86_64-apple-darwin": return "35f6630cb1f05e9dd46d2cdc05741d72098f0d828fa8790412301c6d56912617";
                case "x86_64-unknown-linux-gnu": return "4d9d985945f3ec20ce56b0dca0bdf208c0d240ea9855d17ea44c6993619b220b";
                case "aarch64-unknown-linux-gnu": return "7617959bbfb14823a7cb86a1fcf876040b24ff1e5e778b3b557a44f7a375f298";
                case "x86_64-pc-windows-msvc": return "08f97390a73ec3b710eac498bb05ce9dc436482af7f6f053f73f7b70e12988a5";
                default: return null;
            }
        }

        /// <summary>The release triple (as named in the published archives) of a host, or null when none is published.</summary>
        public static string HostTriple(string os, string arch)
        {
            string o = (os ?? "").ToLowerInvariant();
            string a = (arch ?? "").ToLowerInvariant();
            bool arm64 = a == "arm64" || a == "aarch64";
            bool x64 = a == "x64" || a == "x86_64" || a == "amd64";
            bool mac = o.Contains("mac") || o.Contains("darwin") || o.Contains("osx");
            bool linux = o.Contains("linux");
            bool windows = o.Contains("windows") || o.Contains("win32");
            if (mac && arm64) return "aarch64-apple-darwin";
            if (mac && x64) return "x86_64-apple-darwin";
            if (linux && arm64) return "aarch64-unknown-linux-gnu";
            if (linux && x64) return "x86_64-unknown-linux-gnu";
            if (windows && x64) return "x86_64-pc-windows-msvc";
            return null;
        }

        /// <summary>
        /// A release version safe to put in a URL and a cache path: digits and dots with an optional "-"/"+" suffix.
        /// BUGSEE_CLI_VERSION is validated with this so a value like "../.." cannot leave the cache directory.
        /// </summary>
        public static bool IsValidVersion(string version) =>
            version != null && System.Text.RegularExpressions.Regex.IsMatch(
                version, @"^[0-9]+(\.[0-9]+)*([-+][0-9A-Za-z.]+)?$");

        /// <summary>The URL without credentials (<c>https://user:pass@host/...</c>), for logs and messages.</summary>
        public static string RedactUrl(string url)
        {
            Uri uri;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.UserInfo))
            {
                return url;
            }

            return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        }

        public static bool IsWindowsTriple(string triple) => triple != null && triple.Contains("windows");

        public static string BinaryName(bool windows) => windows ? CliName + ".exe" : CliName;

        /// <summary>Archive extension: Windows releases are zips, the others tar.xz.</summary>
        public static string ArchiveName(string triple) =>
            CliName + "-" + triple + (IsWindowsTriple(triple) ? ".zip" : ".tar.xz");

        public static string ArchiveUrl(string baseUrl, string version, string triple) =>
            (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/')) +
            "/v" + version + "/" + ArchiveName(triple);

        public static string ChecksumUrl(string archiveUrl) => archiveUrl + ".sha256";

        /// <summary>The hash in a <c>.sha256</c> sidecar ("&lt;hex&gt; *name" or "&lt;hex&gt;  name"), lower-cased; null if malformed.</summary>
        public static string ParseSha256Sidecar(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string first = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (first.Length != 64)
            {
                return null;
            }

            foreach (char c in first)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return null;
                }
            }

            return first.ToLowerInvariant();
        }

        /// <summary>"0.8.1" from "bugsee-cli 0.8.1" (the last token that starts with a digit), or null.</summary>
        public static string ParseVersionOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            foreach (string token in output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (char.IsDigit(token[0]))
                {
                    return token;
                }
            }

            return null;
        }

        /// <summary>True when <paramref name="version"/> is at least <paramref name="min"/>; compares the numeric parts, ignoring any suffix.</summary>
        public static bool VersionAtLeast(string version, string min)
        {
            int[] a = NumericParts(version);
            int[] b = NumericParts(min);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int ai = i < a.Length ? a[i] : 0;
                int bi = i < b.Length ? b[i] : 0;
                if (ai != bi)
                {
                    return ai > bi;
                }
            }

            return true;
        }

        static int[] NumericParts(string version)
        {
            var parts = new List<int>();
            foreach (string segment in (version ?? "").Split('.', '-', '+'))
            {
                int end = 0;
                while (end < segment.Length && char.IsDigit(segment[end]))
                {
                    end++;
                }

                int value;
                parts.Add(end > 0 && int.TryParse(segment.Substring(0, end), out value) ? value : 0);
            }

            return parts.ToArray();
        }

        /// <summary>
        /// Directories searched for the binary: the PATH, then where the installers and package managers put it
        /// (<c>install.sh</c>: /usr/local/bin or ~/.local/bin; Homebrew: /opt/homebrew/bin; install.ps1:
        /// %LOCALAPPDATA%\Bugsee\bin; ~/.bugsee/bin). The second element tells a PATH entry from a well-known one.
        /// </summary>
        public static List<KeyValuePair<string, CliSource>> SearchDirectories(CliSettings s)
        {
            var result = new List<KeyValuePair<string, CliSource>>();
            var seen = new HashSet<string>(s.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            char separator = s.IsWindows ? ';' : ':';

            void Add(string dir, CliSource source)
            {
                dir = (dir ?? "").Trim().Trim('"');
                if (dir.Length > 1)
                {
                    dir = dir.TrimEnd('/', '\\');
                }

                if (dir.Length > 0 && seen.Add(dir))
                {
                    result.Add(new KeyValuePair<string, CliSource>(dir, source));
                }
            }

            foreach (string dir in (s.PathEnv ?? "").Split(separator))
            {
                Add(dir, CliSource.Path);
            }

            if (s.IsWindows)
            {
                if (!string.IsNullOrEmpty(s.LocalAppData))
                {
                    Add(Combine(s, s.LocalAppData, "Bugsee", "bin"), CliSource.KnownDirectory);
                }
            }
            else
            {
                Add("/opt/homebrew/bin", CliSource.KnownDirectory);
                Add("/usr/local/bin", CliSource.KnownDirectory);
                if (!string.IsNullOrEmpty(s.Home))
                {
                    Add(Combine(s, s.Home, ".local", "bin"), CliSource.KnownDirectory);
                    Add(Combine(s, s.Home, ".cargo", "bin"), CliSource.KnownDirectory);
                }
            }

            if (!string.IsNullOrEmpty(s.Home))
            {
                Add(Combine(s, s.Home, ".bugsee", "bin"), CliSource.KnownDirectory);
            }

            return result;
        }

        static string Combine(CliSettings s, params string[] parts) =>
            string.Join(s.IsWindows ? "\\" : "/", parts);

        /// <summary>Where a download of <paramref name="version"/> for <paramref name="triple"/> lives under a cache directory.</summary>
        public static string CachedBinaryPath(CliSettings s, string cacheDir, string version, string triple) =>
            Combine(s, cacheDir.TrimEnd('/', '\\'), version, triple, BinaryName(IsWindowsTriple(triple)));

        /// <summary>
        /// Whether a missing CLI may be downloaded: the token is set (the upload is already opted in by it) and the
        /// project did not opt out with BUGSEE_NO_CLI_DOWNLOAD.
        /// </summary>
        public static bool DownloadAllowed(bool tokenSet, bool optedOut) => tokenSet && !optedOut;

        public static CliResolution Resolve(CliSettings s, CliProbe probe)
        {
            var resolution = new CliResolution();

            if (!string.IsNullOrWhiteSpace(s.ExplicitPath))
            {
                string explicitPath = s.ExplicitPath.Trim();
                bool bareName = explicitPath.IndexOf('/') < 0 && explicitPath.IndexOf('\\') < 0;
                if (!bareName)
                {
                    if (probe.FileExists(explicitPath))
                    {
                        resolution.Path = explicitPath;
                        resolution.Source = CliSource.Explicit;
                        resolution.Version = probe.QueryVersion(explicitPath);
                        if (resolution.Version != null && !VersionAtLeast(resolution.Version, s.MinVersion))
                        {
                            resolution.Notes.Add(
                                "BUGSEE_CLI_PATH " + explicitPath + " is bugsee-cli " + resolution.Version + ", older than " +
                                s.MinVersion + ": it is used because you set it, but a symbol table already stored for a build id is not upgraded to DWARF.");
                        }

                        return resolution;
                    }

                    resolution.Notes.Add("BUGSEE_CLI_PATH is set to " + explicitPath + ", which does not exist.");
                }
                else
                {
                    // A bare command name (the old default was "bugsee-cli"): look it up like any other.
                    var names = s.IsWindows && !explicitPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? new[] { explicitPath, explicitPath + ".exe" }
                        : new[] { explicitPath };
                    foreach (var entry in SearchDirectories(s))
                    {
                        foreach (string bare in names)
                        {
                            string candidate = Combine(s, entry.Key, bare);
                            if (probe.FileExists(candidate))
                            {
                                resolution.Path = candidate;
                                resolution.Source = CliSource.Explicit;
                                resolution.Version = probe.QueryVersion(candidate);
                                return resolution;
                            }
                        }
                    }

                    resolution.Notes.Add("BUGSEE_CLI_PATH is set to the command " + explicitPath + ", which is not on PATH.");
                }
            }

            string name = BinaryName(s.IsWindows);
            foreach (var entry in SearchDirectories(s))
            {
                string candidate = Combine(s, entry.Key, name);
                if (!probe.FileExists(candidate))
                {
                    continue;
                }

                string version = probe.QueryVersion(candidate);
                if (version == null)
                {
                    resolution.Notes.Add(candidate + " exists but does not run (wrong architecture or not executable).");
                    continue;
                }

                if (!VersionAtLeast(version, s.MinVersion))
                {
                    resolution.Notes.Add(candidate + " is bugsee-cli " + version + ", older than " + s.MinVersion + ".");
                    continue;
                }

                resolution.Path = candidate;
                resolution.Source = entry.Value;
                resolution.Version = version;
                return resolution;
            }

            if (s.Triple == null)
            {
                resolution.Notes.Add("No bugsee-cli release is published for this host, so none can be downloaded.");
                return resolution;
            }

            foreach (string cacheDir in s.CacheDirectories)
            {
                string cached = CachedBinaryPath(s, cacheDir, s.Version, s.Triple);
                if (probe.FileExists(cached) && probe.QueryVersion(cached) != null)
                {
                    resolution.Path = cached;
                    resolution.Source = CliSource.Cache;
                    resolution.Version = s.Version;
                    return resolution;
                }
            }

            if (!s.DownloadAllowed)
            {
                resolution.Notes.Add(s.DownloadDisabledNote);
                return resolution;
            }

            // One download, however many cache directories are tried: a blackholed network must cost one timeout.
            string staged;
            try
            {
                staged = probe.Fetch(s.Version);
            }
            catch (Exception ex)
            {
                resolution.Notes.Add("Downloading bugsee-cli " + s.Version + " failed: " + ex.Message);
                return resolution;
            }

            try
            {
                foreach (string cacheDir in s.CacheDirectories)
                {
                    try
                    {
                        string installed = probe.Install(staged, cacheDir, s.Version);
                        if (installed != null && probe.FileExists(installed))
                        {
                            resolution.Path = installed;
                            resolution.Source = CliSource.Downloaded;
                            resolution.Version = s.Version;
                            return resolution;
                        }
                    }
                    catch (Exception ex)
                    {
                        resolution.Notes.Add("Installing bugsee-cli " + s.Version + " into " + cacheDir + " failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                probe.Discard?.Invoke(staged);
            }

            return resolution;
        }

        /// <summary>The warning shown (once per build) when no CLI could be resolved.</summary>
        public static string NotFoundMessage(CliResolution resolution)
        {
            var text = new StringBuilder("Bugsee: bugsee-cli was not found, so symbols are NOT uploaded (the build continues). ");
            foreach (string note in resolution.Notes)
            {
                text.Append(note).Append(' ');
            }

            text.Append("Install it (curl --proto '=https' --tlsv1.2 -sSfL https://download.bugsee.com/cli/install.sh | sh) ")
                .Append("or point BUGSEE_CLI_PATH at the binary.");
            return text.ToString();
        }
    }
}
