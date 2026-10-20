using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The IL2CPP arguments that make a build write <c>Symbols/LineNumberMappings.json</c> (C# file:line) and
    /// <c>Symbols/MethodMap.tsv</c> (mangled C++ name to C# signature) next to the generated C++. Neither file exists
    /// unless the arguments are passed, and without them the il2cpp-linemap upload has nothing (or no MethodMap) to send.
    /// </summary>
    public static class Il2CppMappingArgs
    {
        public const string SourceMapping = "--emit-source-mapping";
        public const string MethodMap = "--emit-method-map";

        /// <summary>
        /// Whether a build writes the mapping files: any IL2CPP build, with or without a Bugsee app token (a later CI job, or
        /// a developer, uploads the files; the upload itself still needs the token), unless BUGSEE_NO_IL2CPP_MAPPING is set.
        /// BUGSEE_IL2CPP_MAPPING (an explicit file to upload) does not turn the emission off.
        /// </summary>
        public static bool ShouldEmit(bool il2cppBackend, bool optOutEnvironmentSet) => il2cppBackend && !optOutEnvironmentSet;

        /// <summary>
        /// <paramref name="existing"/> (Player Settings > Additional IL2CPP Arguments) with the mapping arguments it is
        /// missing appended; the same string when it already has both. Existing arguments are never reordered or removed.
        /// </summary>
        public static string Ensure(string existing)
        {
            string result = existing ?? string.Empty;
            foreach (string flag in new[] { SourceMapping, MethodMap })
            {
                if (!Has(result, flag))
                {
                    result = result.Length == 0 || char.IsWhiteSpace(result[result.Length - 1]) ? result + flag : result + " " + flag;
                }
            }

            return result;
        }

        /// <summary>
        /// The value for the IL2CPP_ADDITIONAL_ARGS environment variable (Unity adds it to the il2cpp command line next to
        /// Player Settings > Additional IL2CPP Arguments, so the build does not have to edit the VCS-tracked ProjectSettings.asset):
        /// <paramref name="envArgs"/> (the variable's current value) with the mapping arguments that neither it nor
        /// <paramref name="playerArgs"/> has appended; null when nothing is missing and the variable needs no change.
        /// </summary>
        public static string EnvironmentValue(string playerArgs, string envArgs)
        {
            string seen = (playerArgs ?? string.Empty) + " " + (envArgs ?? string.Empty);
            string appended = Ensure(seen).Substring(seen.Length).Trim();
            if (appended.Length == 0)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(envArgs) ? appended : envArgs.TrimEnd() + " " + appended;
        }

        static bool Has(string args, string flag)
        {
            var separators = new[] { ' ', '\t', '\r', '\n' };
            foreach (string token in args.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == flag || token.StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
