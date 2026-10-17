using System;
using System.IO;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// When to put Player Settings > Additional IL2CPP Arguments back after the build added the mapping arguments
    /// (see <see cref="Il2CppMappingArgs"/>). The post-build callback is not called when the pipeline aborts, so a failed or
    /// cancelled build is settled later: while a build is still running nothing is touched; afterwards the arguments are
    /// restored only if they still hold what the build applied, so a string the user edited meanwhile is kept.
    /// </summary>
    public static class Il2CppArgsRestorePolicy
    {
        public static SymbolRestoreAction Decide(bool buildInProgress, bool currentEqualsApplied)
        {
            if (buildInProgress)
            {
                return SymbolRestoreAction.Skip;
            }

            return currentEqualsApplied ? SymbolRestoreAction.Restore : SymbolRestoreAction.Discard;
        }

        /// <summary>
        /// Whether the saved record may be deleted once <see cref="Decide"/> has been acted on. It is the only way back to the
        /// user's arguments in a later editor session, so it is kept when the record parsed but the restore threw (the flags
        /// are still in Player Settings); it goes after a successful restore, a deliberate discard, or when it cannot be parsed.
        /// </summary>
        public static bool DeleteRecord(bool recordParsed, bool restoreThrew)
        {
            return !recordParsed || !restoreThrew;
        }

        /// <summary>
        /// A failure to read the record that says nothing about its content (locked, unreadable, no permission): the record is
        /// kept for the next attempt. Anything else (a parse failure) means it holds nothing to restore from.
        /// </summary>
        public static bool IsTransientReadError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }
    }
}
