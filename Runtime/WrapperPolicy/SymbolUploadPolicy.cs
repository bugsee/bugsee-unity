namespace Bugsee.WrapperPolicy
{
    /// <summary>Which build-time symbol uploads the environment switches allow.</summary>
    public static class SymbolUploadPolicy
    {
        /// <summary>
        /// Whether the Editor may upload the il2cpp-linemap (or leave the iOS pending pointer, or sweep an Append export):
        /// not when BUGSEE_NO_SYMBOL_UPLOAD (all symbol uploads) or BUGSEE_NO_IL2CPP_MAPPING (the mapping) is set.
        /// </summary>
        public static bool LinemapUploadAllowed(bool noSymbolUploadSet, bool noIl2CppMappingSet) =>
            !noSymbolUploadSet && !noIl2CppMappingSet;
    }
}
