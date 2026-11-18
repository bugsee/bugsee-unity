using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Text edits on a serialized Xcode project (project.pbxproj) for the Run Script phase this package injects.
    /// </summary>
    public static class XcodeShellScriptPhase
    {
        const string PhaseIsa = "isa = PBXShellScriptBuildPhase;";
        static readonly Regex ShellScriptValue = new Regex("shellScript = \"(?:[^\"\\\\]|\\\\.)*\";");

        /// <summary>
        /// Sets the script of every shell script phase named <paramref name="phaseName"/> to
        /// <paramref name="shellScript"/>. Unity's "Append" export keeps the phases of an earlier export, and
        /// PBXProject.AddShellScriptBuildPhase reuses a phase only when its script is identical, so a phase from an
        /// earlier package version would otherwise stay in the target, ahead of the new one.
        /// </summary>
        public static string RewriteScript(string pbxproj, string phaseName, string shellScript)
        {
            if (pbxproj == null) throw new ArgumentNullException(nameof(pbxproj));
            if (phaseName == null) throw new ArgumentNullException(nameof(phaseName));
            if (shellScript == null) throw new ArgumentNullException(nameof(shellScript));

            string nameLine = "name = " + Quote(phaseName) + ";";
            string scriptLine = "shellScript = " + Quote(shellScript) + ";";
            var result = new StringBuilder(pbxproj.Length);
            int copied = 0;
            int isa = pbxproj.IndexOf(PhaseIsa, StringComparison.Ordinal);
            while (isa >= 0)
            {
                // An object's properties end where the next object's "isa" starts: both Unity and Xcode write isa first.
                int next = pbxproj.IndexOf("isa = ", isa + PhaseIsa.Length, StringComparison.Ordinal);
                int end = next < 0 ? pbxproj.Length : next;
                string phase = pbxproj.Substring(isa, end - isa);
                if (phase.IndexOf(nameLine, StringComparison.Ordinal) >= 0)
                {
                    Match script = ShellScriptValue.Match(phase);
                    if (script.Success)
                    {
                        result.Append(pbxproj, copied, isa + script.Index - copied);
                        result.Append(scriptLine);
                        copied = isa + script.Index + script.Length;
                    }
                }
                isa = next < 0 ? -1 : pbxproj.IndexOf(PhaseIsa, next, StringComparison.Ordinal);
            }
            result.Append(pbxproj, copied, pbxproj.Length - copied);
            return result.ToString();
        }

        static readonly Regex AlwaysOutOfDateValue = new Regex("[ \\t]*alwaysOutOfDate = [^;]*;\\r?\\n?");
        static readonly Regex InputPathsValue = new Regex("[ \\t]*inputPaths = \\((?:[^)]|\\)(?!;))*\\);\\r?\\n?");

        /// <summary>
        /// Gives every shell script phase named <paramref name="phaseName"/> exactly <paramref name="inputPaths"/> as its
        /// input files and "Based on dependency analysis" off (<c>alwaysOutOfDate = 1</c>), or none of either when
        /// <paramref name="inputPaths"/> is null or empty. Unity's PBXProject has no API for either. An input that is a build
        /// product makes Xcode run the phase after the task that writes it (the dSYM of the app), and an input that no
        /// task writes (Debug without a dSYM) is not an error. Without outputs Xcode runs the phase on every build and
        /// warns about it; alwaysOutOfDate says so and silences the warning.
        /// </summary>
        public static string SetInputs(string pbxproj, string phaseName, string[] inputPaths)
        {
            if (pbxproj == null) throw new ArgumentNullException(nameof(pbxproj));
            if (phaseName == null) throw new ArgumentNullException(nameof(phaseName));

            string nameLine = "name = " + Quote(phaseName) + ";";
            var result = new StringBuilder(pbxproj.Length);
            int copied = 0;
            int isa = pbxproj.IndexOf(PhaseIsa, StringComparison.Ordinal);
            while (isa >= 0)
            {
                int next = pbxproj.IndexOf("isa = ", isa + PhaseIsa.Length, StringComparison.Ordinal);
                int end = next < 0 ? pbxproj.Length : next;
                string phase = pbxproj.Substring(isa, end - isa);
                if (phase.IndexOf(nameLine, StringComparison.Ordinal) >= 0)
                {
                    // Properties start on the line after isa; the indentation is the one Unity and Xcode use.
                    int lineEnd = phase.IndexOf('\n');
                    string head = lineEnd < 0 ? phase : phase.Substring(0, lineEnd + 1);
                    string rest = lineEnd < 0 ? "" : phase.Substring(lineEnd + 1);
                    rest = AlwaysOutOfDateValue.Replace(rest, "");
                    rest = InputPathsValue.Replace(rest, "");
                    if (inputPaths != null && inputPaths.Length > 0)
                    {
                        var added = new StringBuilder();
                        added.Append("\t\t\talwaysOutOfDate = 1;\n");
                        added.Append("\t\t\tinputPaths = (\n");
                        foreach (string path in inputPaths) added.Append("\t\t\t\t").Append(Quote(path)).Append(",\n");
                        added.Append("\t\t\t);\n");
                        rest = added + rest;
                    }
                    string rewritten = head + rest;
                    if (rewritten != phase)
                    {
                        result.Append(pbxproj, copied, isa - copied);
                        result.Append(rewritten);
                        copied = end;
                    }
                }
                isa = next < 0 ? -1 : pbxproj.IndexOf(PhaseIsa, next, StringComparison.Ordinal);
            }
            result.Append(pbxproj, copied, pbxproj.Length - copied);
            return result.ToString();
        }

        static readonly Regex ObjectId = new Regex("^\\s*(?<id>[0-9A-Fa-f]{24})\\b");

        /// <summary>
        /// Moves every shell script phase named <paramref name="phaseName"/> to the end of the <c>buildPhases</c> list it is
        /// in. The "Bugsee Upload Symbols" phase declares the app's dSYM as an input, and Xcode writes that dSYM only after
        /// the app's Info.plist is processed, a step that waits for the app's embedded extensions to be copied in. A copy phase
        /// listed after the script phase (the "Embed ExtensionKit Extensions" one of the Crash Report Extension) therefore waits
        /// for the script, which waits for the dSYM, which waits for the Info.plist, which waits for the copy: xcodebuild fails
        /// with "Cycle inside Unity-iPhone". The script needs every embed phase done before it runs, so it goes last. It still
        /// runs before the app is signed (signing is not a build phase).
        /// </summary>
        public static string MoveToEnd(string pbxproj, string phaseName)
        {
            if (pbxproj == null) throw new ArgumentNullException(nameof(pbxproj));
            if (phaseName == null) throw new ArgumentNullException(nameof(phaseName));

            string nameLine = "name = " + Quote(phaseName) + ";";
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int isa = pbxproj.IndexOf(PhaseIsa, StringComparison.Ordinal);
            while (isa >= 0)
            {
                int next = pbxproj.IndexOf("isa = ", isa + PhaseIsa.Length, StringComparison.Ordinal);
                int end = next < 0 ? pbxproj.Length : next;
                if (pbxproj.IndexOf(nameLine, isa, end - isa, StringComparison.Ordinal) >= 0)
                {
                    // The object's own line (guid, comment, "= {") is the one before the isa line.
                    int isaLine = pbxproj.LastIndexOf('\n', isa);
                    int lineStart = isaLine <= 0 ? 0 : pbxproj.LastIndexOf('\n', isaLine - 1) + 1;
                    int lineEnd = pbxproj.IndexOf('\n', lineStart);
                    Match id = ObjectId.Match(pbxproj.Substring(lineStart, (lineEnd < 0 ? pbxproj.Length : lineEnd) - lineStart));
                    if (id.Success) ids.Add(id.Groups["id"].Value);
                }
                isa = next < 0 ? -1 : pbxproj.IndexOf(PhaseIsa, next, StringComparison.Ordinal);
            }

            if (ids.Count == 0) return pbxproj;

            var lines = pbxproj.Split('\n');
            var result = new StringBuilder(pbxproj.Length);
            var moved = new System.Collections.Generic.List<string>();
            bool inList = false;
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (!inList)
                {
                    inList = trimmed.StartsWith("buildPhases = (", StringComparison.Ordinal);
                }
                else if (trimmed == ");")
                {
                    foreach (string m in moved) result.Append(m).Append('\n');
                    moved.Clear();
                    inList = false;
                }
                else
                {
                    Match e = ObjectId.Match(line);
                    if (e.Success && ids.Contains(e.Groups["id"].Value))
                    {
                        moved.Add(line);
                        changed = true;
                        continue;
                    }
                }

                result.Append(line);
                if (i < lines.Length - 1) result.Append('\n');
            }

            return changed ? result.ToString() : pbxproj;
        }

        /// <summary>A pbxproj quoted string, escaped the way Unity's PBXProject.WriteToFile writes it.</summary>
        static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
