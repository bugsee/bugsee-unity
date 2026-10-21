using System;
using System.Collections;
using System.Runtime.CompilerServices;
using Unity.IL2CPP.CompilerServices;
using UnityEngine;

// IL2CPP reads this attribute by name; Unity ships no managed definition (Il2CppSetOption in the IL2CPP manual).
namespace Unity.IL2CPP.CompilerServices
{
    public enum Option { NullChecks = 1, ArrayBoundsChecks = 2, DivideByZeroChecks = 3 }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
    public sealed class Il2CppSetOptionAttribute : Attribute
    {
        public Il2CppSetOptionAttribute(Option option, object value) { Option = option; Value = value; }
        public Option Option { get; }
        public object Value { get; }
    }
}

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenarios "nullref" and "nullrefnochk" (tagged "[NULLREF]", issue #95): does a .NET null reference reach the native crash
    /// reporter as a fatal signal? "nullref" triggers a caught NullReferenceException, an uncaught one on a coroutine (Unity logs it
    /// and goes on) and one inside a managed thread-pool task, then checks the process is still running 12 s later. "nullrefnochk"
    /// reads a field of a null object in a method that has IL2CPP's explicit null checks switched off, so the access is a real
    /// hardware fault: what the native reporter does with it is what the app's own choice of CrashSignals decides
    /// (BUGSEE_E2E_CRASH_SIGNALS). It may end the process, so it runs once per launch and a crash is judged on the next launch.
    /// </summary>
    static class NullRefE2E
    {
        sealed class Box { public int Value = 1; }

        static Box _null; // stays null; a static the compiler cannot fold away

        static int _failures;

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] nullref " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }

        public static IEnumerator Run(MonoBehaviour host)
        {
            _failures = 0;
            for (var i = 0; i < 120 && !Bugsee.IsLaunched; i++) yield return new WaitForSeconds(0.5f);
            Debug.Log("[NULLREF] launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);

            var caught = false;
            try { Debug.Log("[NULLREF] " + _null.Value); }
            catch (NullReferenceException ex) { caught = true; Debug.Log("[NULLREF] caught " + ex.GetType().Name); }
            Check("caught", caught, "a null field read raised a catchable NullReferenceException");

            Bugsee.LogException(new NullReferenceException("nullref-handled-by-app"));

            yield return new WaitForSeconds(1f);
            Debug.Log("[NULLREF] throwing an uncaught NullReferenceException on a coroutine");
            yield return null;
            host.StartCoroutine(Throwing()); // Unity logs the exception and ends that coroutine
            var task = System.Threading.Tasks.Task.Run(() => _null.Value.ToString());
            yield return new WaitForSeconds(1f);
            Check("task", task.IsFaulted, "a thread-pool task faulted with " + (task.Exception?.InnerException?.GetType().Name ?? "nothing"));

            yield return new WaitForSeconds(12f);
            Check("alive", true, "the process was still running 12 s after the null references");
            Debug.Log("[Sample] nullref e2e done failures=" + _failures);
        }

        static IEnumerator Throwing()
        {
            yield return null;
            Debug.Log("[NULLREF] " + _null.Value);
        }

        public static IEnumerator RunNoChecks()
        {
            for (var i = 0; i < 120 && !Bugsee.IsLaunched; i++) yield return new WaitForSeconds(0.5f);
            Debug.Log("[NULLREF] nochk launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            yield return new WaitForSeconds(3f);
            Debug.Log("[NULLREF] nochk reading a field of null with null checks off");
            var read = ReadUnchecked(_null);
            Debug.Log("[NULLREF] nochk survived the read value=" + read);
            yield return new WaitForSeconds(5f);
            Debug.Log("[Sample] nullref nochk done");
        }

        [Il2CppSetOption(Option.NullChecks, false)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ReadUnchecked(Box b)
        {
            return b.Value;
        }
    }
}
