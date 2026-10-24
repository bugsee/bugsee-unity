using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    [TestFixture]
    public class DirectoryWalkerTests
    {
        // An in-memory tree: folders map to their child folders and files; chosen folders fail to list.
        sealed class FakeFs : IWalkFileSystem
        {
            public readonly Dictionary<string, List<string>> Dirs = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, List<string>> Files_ = new Dictionary<string, List<string>>();
            public readonly HashSet<string> Failing = new HashSet<string>();
            public readonly HashSet<string> FailingMidway = new HashSet<string>();
            public readonly Dictionary<string, string> Links = new Dictionary<string, string>();
            public readonly List<string> Listed = new List<string>();

            public void Dir(string path)
            {
                if (!Dirs.ContainsKey(path)) Dirs[path] = new List<string>();
                var parent = path.Contains("/") ? path.Substring(0, path.LastIndexOf('/')) : null;
                if (parent != null && Dirs.ContainsKey(parent) && !Dirs[parent].Contains(path)) Dirs[parent].Add(path);
            }

            public void File(string path)
            {
                var parent = path.Substring(0, path.LastIndexOf('/'));
                if (!Files_.ContainsKey(parent)) Files_[parent] = new List<string>();
                Files_[parent].Add(path);
            }

            // A link folder lists what it points at, spelled under the link, as the real file systems do.
            string Real(string dir, out string linkPath, out string target)
            {
                linkPath = null; target = null;
                foreach (var l in Links)
                {
                    if ((dir == l.Key || dir.StartsWith(l.Key + "/")) && (linkPath == null || l.Key.Length > linkPath.Length))
                    {
                        linkPath = l.Key; target = l.Value;
                    }
                }
                return linkPath == null ? dir : target + dir.Substring(linkPath.Length);
            }

            public IEnumerable<string> Files(string dir, string pattern)
            {
                var real = Real(dir, out var lp, out var tg);
                foreach (var f in FilesReal(dir, real, pattern)) yield return lp == null ? f : lp + f.Substring(tg.Length);
            }

            public IEnumerable<string> Directories(string dir, string pattern)
            {
                var real = Real(dir, out var lp, out var tg);
                foreach (var f in DirectoriesReal(dir, real, pattern)) yield return lp == null ? f : lp + f.Substring(tg.Length);
            }

            IEnumerable<string> FilesReal(string spelled, string dir, string pattern)
            {
                Listed.Add(spelled);
                if (Failing.Contains(dir)) throw new UnauthorizedAccessException("denied " + dir);
                if (!Files_.TryGetValue(dir, out var list)) yield break;
                foreach (var f in list)
                {
                    if (Matches(Path.GetFileName(f), pattern)) yield return f;
                }
                if (FailingMidway.Contains(dir)) throw new IOException("broke while listing " + dir);
            }

            IEnumerable<string> DirectoriesReal(string spelled, string dir, string pattern)
            {
                if (Failing.Contains(dir)) throw new UnauthorizedAccessException("denied " + dir);
                if (!Dirs.TryGetValue(dir, out var list)) yield break;
                foreach (var d in list)
                {
                    if (Matches(d.Substring(d.LastIndexOf('/') + 1), pattern)) yield return d;
                }
            }

            public readonly HashSet<string> UnresolvableLinks = new HashSet<string>();
            public bool ResolverMissing;

            public bool IsLink(string dir) => Links.ContainsKey(dir) || Links.ContainsKey(Real(dir, out _, out _)) || UnresolvableLinks.Contains(dir);

            public string LinkTarget(string dir)
            {
                if (ResolverMissing) return null;
                if (UnresolvableLinks.Contains(dir)) throw new IOException("link chain loops");
                return Links.TryGetValue(dir, out var t) ? t : null;
            }

            static bool Matches(string name, string pattern)
            {
                if (pattern == "*") return true;
                if (pattern.StartsWith("*.")) return name.EndsWith(pattern.Substring(1), StringComparison.Ordinal);
                return name == pattern;
            }
        }

        static FakeFs Tree(params string[] files)
        {
            var fs = new FakeFs();
            fs.Dir("/r");
            foreach (var f in files)
            {
                var parts = f.Split('/');
                var cur = "/r";
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    cur += "/" + parts[i];
                    fs.Dir(cur);
                }
                fs.File("/r/" + f);
            }
            return fs;
        }

        static List<string> Files(FakeFs fs, string pattern, List<string> warnings, bool skip = true, bool warnLong = true) =>
            DirectoryWalker.EnumerateFiles("/r", pattern, warnings.Add, skip, warnLong, fs).ToList();

        [Test]
        public void Files_come_files_first_then_subfolders_in_ordinal_order_whatever_the_listing_order()
        {
            var fs = Tree("b/x.so", "a/y.so", "z.so", "a/c/w.so", "a.so");
            fs.Dirs["/r"].Reverse();
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[]
            {
                "/r/a.so", "/r/z.so", "/r/a/y.so", "/r/a/c/w.so", "/r/b/x.so",
            }));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void The_same_tree_gives_the_same_order_every_time()
        {
            var fs = Tree("m/1.so", "k/2.so", "k/j/3.so", "n/4.so");
            var first = Files(fs, "*.so", new List<string>());
            fs.Dirs["/r"].Reverse();
            Assert.That(Files(fs, "*.so", new List<string>()), Is.EqualTo(first));
        }

        [Test]
        public void A_folder_that_fails_in_the_middle_is_warned_about_and_its_siblings_are_still_scanned()
        {
            var fs = Tree("a/1.so", "b/2.so", "c/3.so");
            fs.Failing.Add("/r/b");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so", "/r/c/3.so" }));
            Assert.That(w, Has.Count.EqualTo(1));
            Assert.That(w[0], Does.Contain("/r/b").And.Contain("UnauthorizedAccessException").And.Contain("continues"));
        }

        [Test]
        public void A_folder_that_breaks_while_listing_keeps_what_it_gave_before_and_the_walk_goes_on()
        {
            var fs = Tree("a/1.so", "a/sub/9.so", "b/2.so");
            fs.FailingMidway.Add("/r/a");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so", "/r/a/sub/9.so", "/r/b/2.so" }));
            Assert.That(w, Has.Count.EqualTo(1));
        }

        [Test]
        public void An_unreadable_root_is_one_warning_and_no_files_never_an_exception()
        {
            var fs = Tree("a/1.so");
            fs.Failing.Add("/r");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.Empty);
            Assert.That(w, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_file_path_of_260_characters_is_warned_about_once_for_its_branch_and_still_found()
        {
            var longName = new string('d', 250);
            var fs = Tree("ok/1.so", longName + "/a/2.so", longName + "/a/b/3.so");
            var w = new List<string>();
            var found = DirectoryWalker.EnumerateFiles("/r", "*.so", w.Add, true, true, fs).ToList();
            Assert.That(found, Has.Count.EqualTo(3));
            Assert.That(w, Has.Count.EqualTo(1), "the files below are not warned about again");
            Assert.That(w[0], Does.Contain("260").And.Contain("2.so"));
        }

        [Test]
        public void A_long_folder_whose_files_stay_under_260_characters_gives_no_warning()
        {
            var fs = Tree(new string('d', 245) + "/x.so");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Has.Count.EqualTo(1));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void A_file_path_of_259_characters_is_fine_and_260_is_not()
        {
            string File_(int total) => "/r/" + new string('d', total - 3 - 5) + "/x.so";
            var ok = Tree(); ok.Dir(File_(259).Substring(0, File_(259).LastIndexOf('/'))); ok.File(File_(259));
            var w = new List<string>();
            Assert.That(Files(ok, "*.so", w), Has.Count.EqualTo(1));
            Assert.That(w, Is.Empty);
            var bad = Tree(); bad.Dir(File_(260).Substring(0, File_(260).LastIndexOf('/'))); bad.File(File_(260));
            Assert.That(Files(bad, "*.so", w), Has.Count.EqualTo(1));
            Assert.That(w, Has.Count.EqualTo(1));
        }

        [Test]
        public void The_length_warning_can_be_turned_off_for_platforms_without_the_limit()
        {
            var fs = Tree(new string('d', 250) + "/x.so");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w, true, false), Has.Count.EqualTo(1));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void An_unlistable_folder_with_a_long_name_gives_the_failure_and_the_scan_goes_on()
        {
            var longName = new string('d', 250);
            var fs = Tree("ok/1.so", longName + "/x.so");
            fs.Failing.Add("/r/" + longName);
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/ok/1.so" }));
            Assert.That(w, Has.Count.EqualTo(1));
        }

        [Test]
        public void Library_Temp_Bee_and_obj_below_the_root_are_not_entered_but_the_roots_own_name_does_not_count()
        {
            var fs = new FakeFs();
            fs.Dir("/r"); fs.Dir("/r/Library"); fs.Dir("/r/x"); fs.Dir("/r/x/OBJ"); fs.Dir("/r/x/ok"); fs.Dir("/r/Bee"); fs.Dir("/r/temp");
            fs.File("/r/Library/a.so"); fs.File("/r/x/OBJ/b.so"); fs.File("/r/x/ok/c.so"); fs.File("/r/Bee/d.so"); fs.File("/r/temp/e.so");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/x/ok/c.so" }));
            Assert.That(fs.Listed, Does.Not.Contain("/r/Library").And.Not.Contain("/r/Bee").And.Not.Contain("/r/x/OBJ"));

            Assert.That(Files(fs, "*.so", w, skip: false), Has.Count.EqualTo(5));

            // The root itself may be called Library.
            var inLib = new FakeFs();
            inLib.Dir("/Library"); inLib.Dir("/Library/p"); inLib.File("/Library/p/z.so");
            Assert.That(DirectoryWalker.EnumerateFiles("/Library", "*.so", null, true, true, inLib).ToList(), Has.Count.EqualTo(1));
        }

        [Test]
        public void The_results_equal_a_filtered_AllDirectories_scan_of_the_same_tree_wherever_nothing_fails()
        {
            var fs = Tree("a/1.so", "Temp/2.so", "a/obj/3.so", "a/b/c/4.so", "5.so", "a/Bee/x/6.so");
            var viaWalker = Files(fs, "*.so", new List<string>());
            var all = Files(fs, "*.so", new List<string>(), skip: false)
                .Where(p => !BuildPathExclusion.IsExcludedPath(p, "/r")).ToList();
            Assert.That(viaWalker, Is.EqualTo(all));
        }

        [Test]
        public void Folders_matching_a_pattern_are_found_and_their_insides_are_still_walked()
        {
            var fs = new FakeFs();
            fs.Dir("/r"); fs.Dir("/r/a"); fs.Dir("/r/a/UnityFramework.framework"); fs.Dir("/r/a/UnityFramework.framework/Inner.framework"); fs.Dir("/r/b");
            var w = new List<string>();
            var found = DirectoryWalker.EnumerateDirectories("/r", "*.framework", w.Add, true, true, fs).ToList();
            Assert.That(found, Is.EqualTo(new[] { "/r/a/UnityFramework.framework", "/r/a/UnityFramework.framework/Inner.framework" }));
        }

        [Test]
        public void A_link_back_to_an_ancestor_is_skipped_with_a_warning()
        {
            var fs = Tree("a/1.so");
            fs.Dir("/r/a/loop");
            fs.Links["/r/a/loop"] = "/r";
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so" }));
            Assert.That(w, Has.Count.EqualTo(1));
            Assert.That(w[0], Does.Contain("link back"));
        }

        [Test]
        public void A_link_to_a_sibling_folder_is_followed_once_and_a_mutual_pair_does_not_loop()
        {
            var fs = Tree("a/1.so", "b/2.so");
            fs.Dir("/r/a/tob"); fs.Dir("/r/b/toa");
            fs.Links["/r/a/tob"] = "/r/b";
            fs.Links["/r/b/toa"] = "/r/a";
            // The link folders list nothing themselves in this fake (as a real link would list its target's content).
            var w = new List<string>();
            var found = Files(fs, "*.so", w);
            Assert.That(found.Count, Is.LessThan(50));
            Assert.That(found, Does.Contain("/r/a/1.so").And.Contain("/r/b/2.so"));
        }

        [Test]
        public void Two_link_loops_with_no_resolvable_target_end_at_once_and_each_file_is_found_twice_at_most()
        {
            var fs = Tree("a/1.so", "b/2.so");
            // Links that point at the parent, as on the Editor's Mono (no target can be resolved); each link folder
            // lists what it points at, the way a real one does.
            fs.ResolverMissing = true;
            void Link(string at, string pointsAt)
            {
                fs.Dir(at);
                fs.Links[at] = pointsAt;
            }
            Link("/r/a/up", "/r");
            Link("/r/b/up2", "/r");
            var w = new List<string>();
            var sw = Stopwatch.StartNew();
            var found = Files(fs, "*.so", w);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000));
            Assert.That(found.Count, Is.LessThan(40));
            Assert.That(w.Any(m => m.Contains("already entered")), Is.True);
        }

        [Test]
        public void One_legitimate_link_to_a_folder_elsewhere_is_still_walked_when_no_target_can_be_resolved()
        {
            var fs = Tree("a/1.so");
            fs.ResolverMissing = true;
            fs.Dir("/other"); fs.Dir("/other/deep"); fs.File("/other/deep/2.so");
            fs.Dir("/r/lnk"); fs.Links["/r/lnk"] = "/other";
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so", "/r/lnk/deep/2.so" }));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void A_link_whose_name_is_already_entered_on_the_path_is_not_entered_again_but_other_nested_links_are()
        {
            var fs = Tree("a/1.so");
            fs.Dir("/r/l1"); fs.Links["/r/l1"] = "/x";
            fs.Dir("/x"); fs.Dir("/x/l2"); fs.Links["/x/l2"] = "/y"; fs.Dir("/y"); fs.File("/y/n.so");
            fs.Dir("/x/l1"); fs.Links["/x/l1"] = "/z"; fs.Dir("/z"); fs.File("/z/same.so");
            fs.File("/x/m.so");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so", "/r/l1/m.so" }));
            Assert.That(fs.Listed, Does.Contain("/r/l1/l2"), "a differently named link below a link is entered");
            Assert.That(fs.Listed, Does.Not.Contain("/r/l1/l1"), "the same link name again is not");
        }

        [Test]
        public void Folders_that_all_report_a_reparse_point_with_no_target_are_still_walked_to_the_bottom()
        {
            // Cloud placeholders (OneDrive) are reparse points that are not links: nested ones must not be skipped.
            var fs = Tree("p1/p2/p3/p4/p5/p6/deep.so");
            fs.ResolverMissing = true;
            foreach (var d in new[] { "/r/p1", "/r/p1/p2", "/r/p1/p2/p3", "/r/p1/p2/p3/p4", "/r/p1/p2/p3/p4/p5", "/r/p1/p2/p3/p4/p5/p6" }) fs.Links[d] = d;
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/p1/p2/p3/p4/p5/p6/deep.so" }));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void A_link_whose_chain_cannot_be_resolved_is_not_entered()
        {
            var fs = Tree("a/1.so");
            fs.Dir("/r/bad"); fs.UnresolvableLinks.Add("/r/bad");
            fs.File("/r/bad/z.so");
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so" }));
            Assert.That(w.Single(), Does.Contain("cannot be resolved"));
        }

        [Test]
        public void A_link_back_is_recognised_whatever_the_case_of_the_target()
        {
            var fs = Tree("a/1.so");
            fs.Dir("/r/a/loop"); fs.Links["/r/a/loop"] = "/R";
            var w = new List<string>();
            Assert.That(Files(fs, "*.so", w), Is.EqualTo(new[] { "/r/a/1.so" }));
            Assert.That(w.Single(), Does.Contain("link back"));
        }

        [Test]
        public void Many_sibling_links_that_all_loop_end_without_a_hang()
        {
            var fs = Tree("a/1.so", "z/9.so");
            fs.ResolverMissing = true;
            for (var i = 0; i < 10; i++)
            {
                fs.Dir("/r/a/l" + i);
                fs.Links["/r/a/l" + i] = "/r/a";
            }
            var w = new List<string>();
            var sw = Stopwatch.StartNew();
            Files(fs, "*.so", w);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(20000));
        }

        [Test]
        public void The_walk_stops_after_the_folder_budget_with_one_warning()
        {
            var fs = new FakeFs();
            fs.Dir("/r");
            // A wide tree: more folders than the budget, none of them links.
            var n = DirectoryWalker.MaxFolders / 1000 + 5;
            for (var i = 0; i < 1000; i++)
            {
                var d = "/r/d" + i;
                fs.Dir(d);
                for (var j = 0; j < n; j++) fs.Dir(d + "/e" + j);
            }
            var w = new List<string>();
            Files(fs, "*.so", w);
            Assert.That(w.Any(m => m.Contains("stopped after")), Is.True);
        }

        [Test]
        public void A_loop_nothing_can_recognise_stops_at_the_depth_limit_with_one_warning()
        {
            var fs = new FakeFs();
            var path = "/r";
            fs.Dir(path);
            for (var i = 0; i < DirectoryWalker.MaxDepth + 10; i++)
            {
                path += "/d";
                fs.Dir(path);
            }
            fs.File(path + "/deep.so");
            fs.File("/r/top.so");
            var w = new List<string>();
            var found = DirectoryWalker.EnumerateFiles("/r", "*.so", w.Add, true, false, fs).ToList();
            Assert.That(found, Is.EqualTo(new[] { "/r/top.so" }));
            Assert.That(w, Has.Count.EqualTo(1));
            Assert.That(w[0], Does.Contain("levels deep"));
        }

        [Test]
        public void Warnings_are_capped_with_one_summary_line()
        {
            var fs = new FakeFs();
            fs.Dir("/r");
            for (var i = 0; i < 40; i++)
            {
                var d = "/r/f" + i.ToString("D2");
                fs.Dir(d);
                fs.Failing.Add(d);
            }
            var w = new List<string>();
            Files(fs, "*.so", w);
            Assert.That(w, Has.Count.EqualTo(DirectoryWalker.MaxWarnings + 1));
            Assert.That(w.Last(), Does.Contain("20 more"));
        }

        [Test]
        public void A_null_warn_callback_and_an_empty_root_are_fine()
        {
            var fs = Tree("a/1.so");
            fs.Failing.Add("/r/a");
            Assert.That(DirectoryWalker.EnumerateFiles("/r", "*.so", null, true, true, fs).ToList(), Is.Empty);
            Assert.That(DirectoryWalker.EnumerateFiles("", "*.so", null).ToList(), Is.Empty);
            Assert.That(DirectoryWalker.EnumerateFiles(null, "*.so", null).ToList(), Is.Empty);
        }

        [Test]
        public void ChildDirectories_are_sorted_and_a_failure_is_a_warning_and_an_empty_list()
        {
            var fs = Tree("b/x.so", "a/y.so");
            fs.Dirs["/r"].Reverse();
            var w = new List<string>();
            Assert.That(DirectoryWalker.ChildDirectories("/r", w.Add, fs), Is.EqualTo(new[] { "/r/a", "/r/b" }));
            fs.Failing.Add("/r");
            Assert.That(DirectoryWalker.ChildDirectories("/r", w.Add, fs), Is.Empty);
            Assert.That(w, Has.Count.EqualTo(1));
        }

        // ---- against the real disk ----

        string _tmp;

        [SetUp]
        public void SetUp() => _tmp = Path.Combine(Path.GetTempPath(), "bugsee-walk-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { try { Directory.Delete(_tmp, true); } catch { } }

        [Test]
        public void On_disk_the_walker_finds_the_same_set_as_AllDirectories_minus_the_excluded_folders()
        {
            foreach (var f in new[] { "a/libil2cpp.so", "b/c/libil2cpp.so", "Library/x/libil2cpp.so", "b/obj/libil2cpp.so", "libil2cpp.so" })
            {
                var p = Path.Combine(_tmp, f.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, "x");
            }
            var w = new List<string>();
            var viaWalker = DirectoryWalker.EnumerateFiles(_tmp, "libil2cpp.so", w.Add).ToList();
            var expected = Directory.EnumerateFiles(_tmp, "libil2cpp.so", SearchOption.AllDirectories)
                .Where(p => !BuildPathExclusion.IsExcludedPath(p, _tmp)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.That(viaWalker.OrderBy(p => p, StringComparer.Ordinal), Is.EqualTo(expected));
            Assert.That(viaWalker, Has.Count.EqualTo(3));
            Assert.That(w, Is.Empty);
        }

        [Test]
        public void On_disk_an_unreadable_folder_does_not_hide_its_siblings()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT || IsRoot())
            {
                Assert.Ignore("Needs POSIX permissions and a non-root user (the Windows ACL case is checked by hand).");
            }
            var bad = Path.Combine(_tmp, "b");
            Directory.CreateDirectory(Path.Combine(_tmp, "a")); Directory.CreateDirectory(bad); Directory.CreateDirectory(Path.Combine(_tmp, "c"));
            File.WriteAllText(Path.Combine(_tmp, "a", "m.zip"), "x"); File.WriteAllText(Path.Combine(bad, "m.zip"), "x"); File.WriteAllText(Path.Combine(_tmp, "c", "m.zip"), "x");
            File.SetUnixFileMode(bad, UnixFileMode.None);
            try
            {
                var w = new List<string>();
                var found = DirectoryWalker.EnumerateFiles(_tmp, "*.zip", w.Add).Select(Path.GetFileName).ToList();
                Assert.That(found, Has.Count.EqualTo(2));
                Assert.That(w, Has.Count.EqualTo(1));
                Assert.That(w[0], Does.Contain(bad));
                Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFiles(_tmp, "*.zip", SearchOption.AllDirectories).ToList(),
                    "the enumeration the walker replaces ends the whole root");
            }
            finally
            {
                File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        static bool IsRoot() => Environment.UserName == "root";

        [Test]
        public void On_disk_a_symlink_loop_terminates_and_each_file_is_reported_once()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Ignore("Symlink creation needs privileges on Windows.");
            }
            Directory.CreateDirectory(Path.Combine(_tmp, "a", "b"));
            File.WriteAllText(Path.Combine(_tmp, "a", "b", "x.so"), "x");
            Directory.CreateSymbolicLink(Path.Combine(_tmp, "a", "b", "up"), _tmp);
            var w = new List<string>();
            var found = DirectoryWalker.EnumerateFiles(_tmp, "x.so", w.Add).ToList();
            Assert.That(found, Is.EqualTo(new[] { Path.Combine(_tmp, "a", "b", "x.so") }));
            Assert.That(w.Any(m => m.Contains("link back")), Is.True);
        }

        [Test]
        public void On_disk_a_symlink_to_a_folder_elsewhere_is_followed_as_AllDirectories_does()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Ignore("Symlink creation needs privileges on Windows.");
            }
            var other = Path.Combine(_tmp, "other");
            Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "y.so"), "x");
            var root = Path.Combine(_tmp, "root");
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "lnk"), other);
            var found = DirectoryWalker.EnumerateFiles(root, "y.so", null).ToList();
            Assert.That(found, Is.EqualTo(new[] { Path.Combine(root, "lnk", "y.so") }));
        }

        [Test]
        public void On_disk_two_symlink_loops_end_quickly()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Ignore("Symlink creation needs privileges on Windows.");
            }
            Directory.CreateDirectory(Path.Combine(_tmp, "a")); Directory.CreateDirectory(Path.Combine(_tmp, "b"));
            File.WriteAllText(Path.Combine(_tmp, "a", "x.so"), "x");
            Directory.CreateSymbolicLink(Path.Combine(_tmp, "a", "up"), _tmp);
            Directory.CreateSymbolicLink(Path.Combine(_tmp, "b", "up2"), _tmp);
            var sw = Stopwatch.StartNew();
            var found = DirectoryWalker.EnumerateFiles(_tmp, "x.so", null).ToList();
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(3000));
            Assert.That(found, Does.Contain(Path.Combine(_tmp, "a", "x.so")));
            Assert.That(found.Count, Is.LessThanOrEqualTo(3));
        }

        [Test]
        public void A_tree_of_ten_thousand_files_is_walked_in_about_the_time_Directory_EnumerateFiles_takes()
        {
            for (var d = 0; d < 100; d++)
            {
                var dir = Path.Combine(_tmp, "d" + d.ToString("D3"), "sub");
                Directory.CreateDirectory(dir);
                for (var f = 0; f < 100; f++) File.WriteAllBytes(Path.Combine(dir, "f" + f + ".so"), new byte[0]);
            }

            long Time(Func<int> run, out int count)
            {
                var best = long.MaxValue; count = 0;
                for (var i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    count = run();
                    best = Math.Min(best, sw.ElapsedMilliseconds);
                }
                return best;
            }

            var baseline = Time(() => Directory.EnumerateFiles(_tmp, "*.so", SearchOption.AllDirectories).Count(), out var baseCount);
            var walker = Time(() => DirectoryWalker.EnumerateFiles(_tmp, "*.so", null).Count(), out var walkCount);
            Assert.That(baseCount, Is.EqualTo(10000));
            Assert.That(walkCount, Is.EqualTo(10000));
            Assert.That(walker, Is.LessThanOrEqualTo(Math.Max(baseline * 6, 2000)), $"walker {walker} ms vs baseline {baseline} ms");
        }

        [Test]
        public void No_editor_file_scans_a_tree_with_AllDirectories_except_the_two_that_do_not_scan_build_output()
        {
            var repo = AppContext.BaseDirectory;
            while (repo != null && !Directory.Exists(Path.Combine(repo, "Editor"))) repo = Path.GetDirectoryName(repo);
            Assert.That(repo, Is.Not.Null);
            var allowed = new[] { "BugseeCliLocator.cs", "LocalPackageCopy.cs" };
            foreach (var path in Directory.GetFiles(Path.Combine(repo, "Editor"), "*.cs"))
            {
                var name = Path.GetFileName(path);
                if (allowed.Contains(name)) continue;
                var code = string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//")));
                Assert.That(code, Does.Not.Contain("SearchOption.AllDirectories"), name);
                Assert.That(code, Does.Not.Match(@"\(SearchOption\)\s*1"), name);
                Assert.That(code, Does.Not.Match(@"Directory\.(Enumerate|Get)(Files|Directories)\([^;]*AllDirectories"), name);
            }
            foreach (var name in new[] { "BugseeSymbolUpload.cs", "BugseeIl2CppLinemapUpload.cs", "BugseeIl2CppModuleIdentity.cs", "BugseeAndroidNativeLibUpload.cs" })
            {
                Assert.That(File.ReadAllText(Path.Combine(repo, "Editor", name)), Does.Contain("BugseeDirectoryScan."), name);
            }
        }
    }
}
