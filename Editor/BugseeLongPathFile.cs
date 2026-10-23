using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Bugsee.WrapperPolicy;
using Microsoft.Win32.SafeHandles;

namespace Bugsee.Editor
{
    /// <summary>
    /// Reads of build outputs that survive a path over 260 characters on Windows. The Editor's Mono 6.13 (Unity 2021.3)
    /// lists and stats such a path but its <c>FileStream</c> fails with <see cref="DirectoryNotFoundException"/>, with
    /// <c>LongPathsEnabled</c> on or off. The plain open is tried first (it is right everywhere else, and on runtimes
    /// that handle long paths); only a failure on a long Windows path is retried through <c>CreateFileW</c> with the
    /// <c>\\?\</c> prefix, wrapped in a <see cref="FileStream"/>. Nothing is written; no other platform takes that branch.
    /// </summary>
    static class BugseeLongPathFile
    {
        static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        const uint GenericRead = 0x80000000;
        const uint ShareReadWrite = 0x1 | 0x2;
        const uint OpenExisting = 3;
        const uint FileAttributeNormal = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>Opens a build output for reading, shared with writers like the Gradle daemon.</summary>
        internal static FileStream OpenRead(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex) when (IsWindows && (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is PathTooLongException))
            {
                var stream = TryOpenExtended(path);
                if (stream == null)
                {
                    throw;
                }

                return stream;
            }
        }

        internal static byte[] ReadAllBytes(string path)
        {
            using (var stream = OpenRead(path))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        static FileStream TryOpenExtended(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }

            if (!LongPathPolicy.NeedsExtended(full, true))
            {
                return null;
            }

            var handle = CreateFile(LongPathPolicy.Extend(full), GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var code = Marshal.GetLastWin32Error();
                handle.Dispose();
                // The same exception type the plain open raises, so callers' handling does not change.
                throw new IOException(LongPathPolicy.Describe(full, new Win32Exception(code).Message) + ": " + full);
            }

            return new FileStream(handle, FileAccess.Read, 4096, false);
        }
    }
}
