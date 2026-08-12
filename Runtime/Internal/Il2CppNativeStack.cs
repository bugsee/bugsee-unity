#if !UNITY_EDITOR && (UNITY_IOS || UNITY_ANDROID) && ENABLE_IL2CPP
#define BUGSEE_IL2CPP_NATIVE_STACK 1
#endif

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Capture of IL2CPP native instruction pointers for a thrown
    /// <see cref="Exception"/> via <c>il2cpp_native_stack_trace</c>.
    /// Enables worker primary LNM apply on managed <c>LogException</c> events.
    /// </summary>
    public static class Il2CppNativeStack
    {
        public sealed class Result
        {
            public IntPtr[] Frames = Array.Empty<IntPtr>();
            public string ImageUuid;
            public string ImageName;
        }

        public static bool IsSupported
        {
            get
            {
#if BUGSEE_IL2CPP_NATIVE_STACK
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Returns native instruction pointers for <paramref name="exception"/>,
        /// or null when unavailable / not IL2CPP / empty.
        /// </summary>
        public static Result TryCapture(Exception exception)
        {
            if (exception == null || !IsSupported)
            {
                return null;
            }

#if BUGSEE_IL2CPP_NATIVE_STACK
            var gch = GCHandle.Alloc(exception);
            var addresses = IntPtr.Zero;
            try
            {
                var gchandle = GCHandle.ToIntPtr(gch);
                var target = Il2CppGcHandleGetTargetShim(gchandle);
                if (target == IntPtr.Zero)
                {
                    return null;
                }

                var uuidBuffer = IntPtr.Zero;
                var imageNameBuffer = IntPtr.Zero;
                int numFrames;
                il2cpp_native_stack_trace(target, out addresses, out numFrames, out uuidBuffer, out imageNameBuffer);
                try
                {
                    if (numFrames <= 0 || addresses == IntPtr.Zero)
                    {
                        return null;
                    }

                    var frames = new IntPtr[numFrames];
                    Marshal.Copy(addresses, frames, 0, numFrames);
                    return new Result
                    {
                        Frames = frames,
                        ImageUuid = SanitizeDebugId(uuidBuffer),
                        ImageName = imageNameBuffer == IntPtr.Zero
                            ? null
                            : Marshal.PtrToStringAnsi(imageNameBuffer),
                    };
                }
                finally
                {
                    if (uuidBuffer != IntPtr.Zero) il2cpp_free(uuidBuffer);
                    if (imageNameBuffer != IntPtr.Zero) il2cpp_free(imageNameBuffer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Bugsee: il2cpp_native_stack_trace failed: " + ex.Message);
                return null;
            }
            finally
            {
                gch.Free();
                if (addresses != IntPtr.Zero)
                {
                    il2cpp_free(addresses);
                }
            }
#else
            return null;
#endif
        }

#if BUGSEE_IL2CPP_NATIVE_STACK
        /// <summary>
        /// Normalize Unity's image UUID to match Bugsee symbol upload identity.
        /// ELF: raw NT_GNU_BUILD_ID hex (no dashes), same as
        /// <c>BugseeIl2CppModuleIdentity.TryReadElfBuildId</c> — do <b>not</b>
        /// apply Sentry's GUID byte-swap. Mach-O: strip dashes to lowercase hex.
        /// </summary>
        static string SanitizeDebugId(IntPtr debugIdPtr)
        {
            if (debugIdPtr == IntPtr.Zero) return null;

            var raw = Marshal.PtrToStringAnsi(debugIdPtr);
            if (string.IsNullOrEmpty(raw)) return null;

            // Length-safe: never write into the native buffer. Copy & normalize only.
            var cleaned = raw.Trim().Replace("-", "").ToLowerInvariant();
            if (cleaned.Length == 0) return null;

            // Some Unity builds append trailing zeros / padding in the C string.
            // Keep hex only.
            var sb = new System.Text.StringBuilder(cleaned.Length);
            for (var i = 0; i < cleaned.Length; i++)
            {
                var c = cleaned[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                    sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

#if UNITY_2023_1_OR_NEWER
        static IntPtr Il2CppGcHandleGetTargetShim(IntPtr gchandle) =>
            il2cpp_gchandle_get_target(gchandle);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr il2cpp_gchandle_get_target(IntPtr gchandle);
#else
        static IntPtr Il2CppGcHandleGetTargetShim(IntPtr gchandle) =>
            il2cpp_gchandle_get_target(gchandle.ToInt32());

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr il2cpp_gchandle_get_target(int gchandle);
#endif

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        static extern void il2cpp_free(IntPtr ptr);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        static extern void il2cpp_native_stack_trace(
            IntPtr exc,
            out IntPtr addresses,
            out int numFrames,
            out IntPtr imageUUID,
            out IntPtr imageName);
#endif
    }
}
