using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Traces captured on an HONOR WOD-LX1 (Android 14, Unity 2022.3.62f3 development build, sample scenario sigthreads, #217):
    /// the same throw site caught in Observe, reported from the main thread (coroutine) and from a worker thread. The text is
    /// Exception.StackTrace (Trace) and the live stack at the catch (Live, StackTrace.ToString()); one frame per element, as logged.
    /// </summary>
    public class ManagedSignatureRecipeFixtureTests
    {
        static readonly string[] MainTrace =
        {
            "at Bugsee.Sample.SignatureThreadsE2E.ThrowAtTheSite (System.String marker) [0x0000c] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:154",
            "at Bugsee.Sample.SignatureThreadsE2E.Observe (System.String thread, System.String marker, System.String sharedJson) [0x0000b] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:86",
            "at Bugsee.Sample.SignatureThreadsE2E+<Routine>d__1.MoveNext () [0x000bd] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:44",
            "at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress) [0x00027] in /home/bokken/build/output/unity/unity/Runtime/Export/Scripting/Coroutines.cs:17",
            "at UnityEngine.MonoBehaviour.StartCoroutine (System.Collections.IEnumerator routine) [0x0002e] in /home/bokken/build/output/unity/unity/Runtime/Export/Scripting/MonoBehaviour.bindings.cs:114",
            "at Bugsee.Sample.SignatureThreadsE2E.Run (UnityEngine.MonoBehaviour host, System.String marker) [0x00008] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:28",
            "at Bugsee.Sample.BugseeSampleBootstrap.PumpPendingE2EFire (System.String pending) [0x002c8] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/BugseeSampleBootstrap.cs:795",
            "at Bugsee.Sample.BugseeSampleBootstrap.Update () [0x00031] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/BugseeSampleBootstrap.cs:294"
        };
        static readonly string MainLive = string.Join("\n", new[]
        {
            "at Bugsee.Sample.SignatureThreadsE2E.Observe (System.String thread, System.String marker, System.String sharedJson) [0x0008b] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:96",
            "at Bugsee.Sample.SignatureThreadsE2E+<Routine>d__1.MoveNext () [0x000bd] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:44",
            "at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress) [0x00027] in /home/bokken/build/output/unity/unity/Runtime/Export/Scripting/Coroutines.cs:17",
            "at UnityEngine.MonoBehaviour.StartCoroutine (System.Collections.IEnumerator routine) [0x0002e] in /home/bokken/build/output/unity/unity/Runtime/Export/Scripting/MonoBehaviour.bindings.cs:114",
            "at Bugsee.Sample.SignatureThreadsE2E.Run (UnityEngine.MonoBehaviour host, System.String marker) [0x00008] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:28",
            "at Bugsee.Sample.BugseeSampleBootstrap.PumpPendingE2EFire (System.String pending) [0x002c8] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/BugseeSampleBootstrap.cs:795",
            "at Bugsee.Sample.BugseeSampleBootstrap.Update () [0x00031] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/BugseeSampleBootstrap.cs:294"
        });
        static readonly string[] WorkerTrace =
        {
            "at Bugsee.Sample.SignatureThreadsE2E.ThrowAtTheSite (System.String marker) [0x0000c] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:154",
            "at Bugsee.Sample.SignatureThreadsE2E.Observe (System.String thread, System.String marker, System.String sharedJson) [0x0000b] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:86",
            "at Bugsee.Sample.SignatureThreadsE2E+<>c__DisplayClass1_0.<Routine>b__0 () [0x00017] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:46",
            "at System.Threading.ThreadHelper.ThreadStart_Context (System.Object state) [0x00000] in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.RunInternal (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state, System.Boolean preserveSyncCtx) [0x00000] in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.Run (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state, System.Boolean preserveSyncCtx) [0x00000] in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.Run (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state) [0x00000] in <00000000000000000000000000000000>:0",
            "at System.Threading.ThreadHelper.ThreadStart () [0x00000] in <00000000000000000000000000000000>:0"
        };
        static readonly string WorkerLive = string.Join("\n", new[]
        {
            "at Bugsee.Sample.SignatureThreadsE2E.Observe (System.String thread, System.String marker, System.String sharedJson) [0x0008b] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:96",
            "at Bugsee.Sample.SignatureThreadsE2E+<>c__DisplayClass1_0.<Routine>b__0 () [0x00017] in /Volumes/External2TB/Projects/Bugsee/cross/s217/Assets/Scripts/BugseeHarness/SignatureThreadsE2E.cs:46",
            "at System.Threading.ThreadHelper.ThreadStart_Context (System.Object state) <0x00000 + 0xffffffff> 0 in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.RunInternal (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state, System.Boolean preserveSyncCtx) <0x00000 + 0xffffffff> 0 in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.Run (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state, System.Boolean preserveSyncCtx) <0x00000 + 0xffffffff> 0 in <00000000000000000000000000000000>:0",
            "at System.Threading.ExecutionContext.Run (System.Threading.ExecutionContext executionContext, System.Threading.ContextCallback callback, System.Object state) <0x00000 + 0xffffffff> 0 in <00000000000000000000000000000000>:0",
            "at System.Threading.ThreadHelper.ThreadStart () <0x00000 + 0xffffffff> 0 in <00000000000000000000000000000000>:0"
        });

        [Test]
        public void Captured_main_and_worker_traces_differ_but_the_trimmed_signature_is_equal()
        {
            Assert.That(MainTrace[2], Is.Not.EqualTo(WorkerTrace[2]));
            var main = ManagedSignatureRecipe.Compute("System.InvalidOperationException", MainTrace, null, "", MainLive);
            var worker = ManagedSignatureRecipe.Compute("System.InvalidOperationException", WorkerTrace, null, "", WorkerLive);
            Assert.That(main, Is.EqualTo(worker));
            // without the trim the caller frames (user frames of the sample) split them
            Assert.That(ManagedSignatureRecipe.Compute("System.InvalidOperationException", MainTrace, null, "", null),
                Is.Not.EqualTo(ManagedSignatureRecipe.Compute("System.InvalidOperationException", WorkerTrace, null, "", null)));
        }

        [Test]
        public void Captured_traces_keep_the_throw_site_and_the_catch_frame()
        {
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(MainTrace, MainLive), Is.EqualTo(2));
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(WorkerTrace, WorkerLive), Is.EqualTo(2));
        }

        [Test]
        public void Captured_frames_parse_to_file_name_and_line()
        {
            string file;
            int line;
            Assert.That(ManagedSignatureRecipe.TryFileLine(MainTrace[0], out file, out line), Is.True);
            Assert.That(file, Is.EqualTo("SignatureThreadsE2E.cs"));
            Assert.That(line, Is.EqualTo(154));
            Assert.That(ManagedSignatureRecipe.IsEngineFrame(MainTrace[3]), Is.True);
            Assert.That(ManagedSignatureRecipe.IsEngineFrame(MainTrace[0]), Is.False);
        }
    }
}
