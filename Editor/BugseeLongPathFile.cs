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

        /// <summary>The whole file, in one allocation of its length (a libil2cpp.so can be over a gigabyte).</summary>
        internal static byte[] ReadAllBytes(string path)
        {
            using (var stream = OpenRead(path))
            {
                if (stream.Length > int.MaxValue)
                {
                    throw new IOException("The file is over 2 GB: " + path);
                }

                var bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException("The file ended early: " + path);
                    }

                    read += n;
                }

                return bytes;
            }
        }

        internal static string ReadAllText(string path)
        {
            using (var reader = new StreamReader(OpenRead(path), System.Text.Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// The file opened through the extended-length form, or null when that does not apply (not Windows, a short or
        /// relative path). Throws an <see cref="IOException"/> with the system's own message and the path when the system
        /// refuses it (missing, locked, access denied): that is a different problem than the length.
        /// </summary>
        internal static FileStream TryOpenExtended(string path)
        {
            if (!IsWindows)
            {
                return null;
            }

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
                throw new IOException(new Win32Exception(code).Message + ": " + full);
            }

            return new FileStream(handle, FileAccess.Read, 4096, false);
        }
    }
}
