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
