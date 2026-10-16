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
    }
}
