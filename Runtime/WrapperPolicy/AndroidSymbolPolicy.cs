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

    public enum SymbolRestoreAction
    {
        /// <summary>Leave everything as it is.</summary>
        Skip,

        /// <summary>Put the settings back to what they were before the opt-in changed them.</summary>
        Restore,

        /// <summary>The settings no longer hold what the opt-in applied (the user changed them): keep them, forget the saved state.</summary>
        Discard,
    }

    /// <summary>
    /// Native file:line symbolication needs DWARF in symbols.zip, which only the Debugging level produces (Public gives
    /// symbol tables, Disabled nothing). Debugging is about ten times larger, so it is never forced silently: the build
    /// warns when symbols are uploaded without it, and <paramref name="useFullSymbols"/> (menu or
    /// BUGSEE_ANDROID_FULL_SYMBOLS=1) opts in.
    /// </summary>
    public static class AndroidSymbolPolicy
    {
        /// <param name="zipEnabled">The symbols zip is produced (Unity 6: the format includes Zip). Unity 2021.3 has no format option.</param>
        public static NativeSymbolDecision Decide(bool useFullSymbols, bool uploadConfigured, NativeSymbolLevel current, bool zipEnabled = true)
        {
            if (current == NativeSymbolLevel.Debugging && zipEnabled)
            {
                return new NativeSymbolDecision(false, null);
            }

            if (useFullSymbols)
            {
                return new NativeSymbolDecision(true, null);
            }

            if (current == NativeSymbolLevel.Debugging)
            {
                return uploadConfigured
                    ? new NativeSymbolDecision(false,
                        "[Bugsee] The symbols zip is not produced (Player Settings > Publishing Settings: the debug symbols format has no Zip), so there is no native symbols file to upload " +
                        "and native/IL2CPP crashes will not be symbolicated. Enable Zip, or the menu item Bugsee > Android > Use Full Native Symbols (Debugging) For Symbol Upload.")
                    : new NativeSymbolDecision(false, null);
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

        /// <summary>
        /// A restore pending from an earlier (possibly interrupted) opt-in. Unrelated builds never touch it; it is
        /// restored only while the settings still hold what the opt-in applied, so a level the user chose since is kept.
        /// </summary>
        public static SymbolRestoreAction Restore(bool androidBuildOrEditorLoad, bool androidBuildInProgress, bool currentEqualsApplied)
        {
            if (!androidBuildOrEditorLoad || androidBuildInProgress)
            {
                return SymbolRestoreAction.Skip;
            }

            return currentEqualsApplied ? SymbolRestoreAction.Restore : SymbolRestoreAction.Discard;
        }
    }
}
