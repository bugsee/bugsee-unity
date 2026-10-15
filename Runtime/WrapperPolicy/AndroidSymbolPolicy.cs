namespace Bugsee.WrapperPolicy
{
    /// <summary>Unity's Android "Create symbols.zip" level (EditorUserBuildSettings.androidCreateSymbols).</summary>
    public enum NativeSymbolLevel
    {
        Disabled,
        Public,
        Debugging,
    }

    /// <summary>What an Android build should do about native symbols, given symbol upload and the opt-in.</summary>
    public readonly struct NativeSymbolDecision
    {
        public NativeSymbolDecision(bool change, string warning)
        {
            Change = change;
            Warning = warning;
        }

        /// <summary>Build with <see cref="NativeSymbolLevel.Debugging"/> for this build (and restore afterwards).</summary>
        public bool Change { get; }

        /// <summary>A build-time warning, or null.</summary>
        public string Warning { get; }
    }

    /// <summary>
    /// Native file:line symbolication needs DWARF in symbols.zip, which only the Debugging level produces (Public gives
    /// symbol tables, Disabled nothing). Debugging is about ten times larger, so it is never forced silently: the build
    /// warns when symbols are uploaded without it, and <paramref name="useFullSymbols"/> (menu or
    /// BUGSEE_ANDROID_FULL_SYMBOLS=1) opts in.
    /// </summary>
    public static class AndroidSymbolPolicy
    {
        public static NativeSymbolDecision Decide(bool useFullSymbols, bool uploadConfigured, NativeSymbolLevel current)
        {
            if (current == NativeSymbolLevel.Debugging)
            {
                return new NativeSymbolDecision(false, null);
            }

            if (useFullSymbols)
            {
                return new NativeSymbolDecision(true, null);
            }

            if (!uploadConfigured)
            {
                return new NativeSymbolDecision(false, null);
            }

            const string tradeOff =
                " Set Player Settings > Publishing Settings > Create symbols.zip to Debugging, or enable the menu item " +
                "Bugsee > Android > Use Full Native Symbols (Debugging) For Symbol Upload (or BUGSEE_ANDROID_FULL_SYMBOLS=1). " +
                "Debugging symbols.zip is about ten times larger than Public and counts against Google Play's size limits for the symbols file.";
            return new NativeSymbolDecision(
                false,
                current == NativeSymbolLevel.Disabled
                    ? "[Bugsee] Create symbols.zip is Disabled, so there is no native symbols file to upload and native/IL2CPP crash frames get no function names or file:line symbolication." + tradeOff
                    : "[Bugsee] Create symbols.zip is Public: it holds symbol tables only, so native/IL2CPP crash frames get function names but no file:line symbolication." + tradeOff);
        }
    }
}
