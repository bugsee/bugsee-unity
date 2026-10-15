using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    public enum BuildIdBlockStatus
    {
        /// <summary>The block was added or brought up to date.</summary>
        Applied,
        /// <summary>The block was already there and current.</summary>
        AlreadyCurrent,
        /// <summary>No top-level <c>android {</c> block to put it before: left alone.</summary>
        NoAnchor,
    }

    /// <summary>
    /// The part of the Bugsee Gradle plugin that gives the app its build id, for Android Gradle plugins too old to run
    /// the plugin (Unity 2021.3 / 2022.3 pin AGP 7.4.2; the plugin needs 8.6+). After R8 it writes
    /// <c>assets/bugsee_build_id.properties</c> holding the UUID of the R8 mapping (MD5 name-based, what the plugin
    /// and <c>bugsee-cli --type proguard</c> compute), which is what the SDK reports at runtime. The mapping upload
    /// then needs no <c>--uuid</c>: the CLI derives the same id from the file. With no mapping (R8 off) no asset is
    /// written and the SDK derives its own id (<see cref="AndroidBuildUuid"/>).
    ///
    /// Uses only API present in AGP 7.4 (<c>artifacts.get(OBFUSCATION_MAPPING_FILE)</c> and a generated assets source
    /// directory), not <c>toListenTo</c> (missing in AGP 7.4) and not a transform of the ASSETS artifact (in AGP 7.4.2
    /// that replaced the merged assets and dropped every library asset, Unity's game data among them; checked on
    /// AGP 7.4.2 / Gradle 7.5.1).
    /// </summary>
    public static class GradleBuildIdBlock
    {
        public const string Begin = "// bugsee:build-id begin";
        public const string End = "// bugsee:build-id end";
        public const string AssetName = "bugsee_build_id.properties";

        public static string Snippet() => string.Join("\n", new[]
        {
            Begin,
            "// Written by the Bugsee Unity package: the Bugsee Gradle plugin cannot run on this Android Gradle plugin.",
            "abstract class BugseeResolveBuildIdTask extends DefaultTask {",
            "    @InputFile @Optional @PathSensitive(PathSensitivity.NONE)",
            "    abstract RegularFileProperty getMappingFile()",
            "    @OutputFile",
            "    abstract RegularFileProperty getIdFile()",
            "    @TaskAction",
            "    void run() {",
            "        def mapping = mappingFile.orNull?.asFile",
            "        def out = idFile.get().asFile",
            "        out.parentFile.mkdirs()",
            "        out.text = (mapping != null && mapping.isFile() && mapping.length() > 0) ? java.util.UUID.nameUUIDFromBytes(mapping.bytes).toString() : ''",
            "    }",
            "}",
            "abstract class BugseeBuildIdAssetTask extends DefaultTask {",
            "    @InputFile @PathSensitive(PathSensitivity.NONE)",
            "    abstract RegularFileProperty getIdFile()",
            "    @OutputDirectory",
            "    abstract DirectoryProperty getOutputDir()",
            "    @TaskAction",
            "    void run() {",
            "        def dst = outputDir.get().asFile",
            "        project.delete(dst)",
            "        dst.mkdirs()",
            "        def id = idFile.get().asFile.text.trim()",
            "        if (!id.isEmpty()) {",
            "            new File(dst, '" + AssetName + "').text = \"# Bugsee build identifier - auto-generated, do not edit.\\nbugsee.build_id=${id}\\n\"",
            "        }",
            "    }",
            "}",
            "// A failure here only costs the build id (the SDK then derives its own), never the build.",
            "if (project.extensions.findByName('androidComponents') != null) {",
            "    androidComponents {",
            "        onVariants(selector().all()) { variant ->",
            "            try {",
            "                def cap = variant.name.capitalize()",
            "                def mapping = variant.artifacts.get(com.android.build.api.artifact.SingleArtifact.OBFUSCATION_MAPPING_FILE.INSTANCE)",
            "                def resolve = tasks.register(\"resolveBugsee${cap}BuildId\", BugseeResolveBuildIdTask) {",
            "                    it.idFile.set(layout.buildDirectory.file(\"bugsee/${variant.name}/build-id.txt\"))",
            "                }",
            "                resolve.configure { it.mappingFile.set(mapping) }",
            "                def asset = tasks.register(\"injectBugsee${cap}BuildId\", BugseeBuildIdAssetTask) {",
            "                    it.idFile.set(resolve.flatMap { it.idFile })",
            "                }",
            "                variant.sources.assets.addGeneratedSourceDirectory(asset, { it.outputDir })",
            "            } catch (Throwable t) {",
            "                logger.warn(\"Bugsee: could not add the build id asset to ${variant.name}: ${t.message}\")",
            "            }",
            "        }",
            "    }",
            "}",
            End,
        });

        /// <summary>
        /// Adds the block before the launcher's top-level <c>android {</c> line, or replaces the one a previous run
        /// left (an append export, an upgraded package).
        /// </summary>
        public static string Apply(string launcherGradle, out BuildIdBlockStatus status)
        {
            if (launcherGradle == null)
            {
                throw new ArgumentNullException(nameof(launcherGradle));
            }

            var lines = MarkedGradleBlock.SplitLines(launcherGradle, out string newline);
            var snippet = new List<string>(Snippet().Split('\n'));

            int begin = lines.FindIndex(l => l.Trim() == Begin);
            int end = begin < 0 ? -1 : lines.FindIndex(begin, l => l.Trim() == End);
            if (begin >= 0 && end > begin)
            {
                var current = lines.GetRange(begin, end - begin + 1);
                if (SameLines(current, snippet))
                {
                    status = BuildIdBlockStatus.AlreadyCurrent;
                    return launcherGradle;
                }

                lines.RemoveRange(begin, end - begin + 1);
                lines.InsertRange(begin, snippet);
                status = BuildIdBlockStatus.Applied;
                return MarkedGradleBlock.JoinLines(lines, newline, false);
            }

            int depth = 0;
            bool inBlockComment = false;
            int anchor = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!inBlockComment && depth == 0 && lines[i].TrimStart().StartsWith("android {", StringComparison.Ordinal))
                {
                    anchor = i;
                    break;
                }

                MarkedGradleBlock.ScanLine(lines[i], ref depth, ref inBlockComment);
            }

            if (anchor < 0)
            {
                status = BuildIdBlockStatus.NoAnchor;
                return launcherGradle;
            }

            snippet.Add("");
            lines.InsertRange(anchor, snippet);
            status = BuildIdBlockStatus.Applied;
            return MarkedGradleBlock.JoinLines(lines, newline, false);
        }

        static bool SameLines(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
