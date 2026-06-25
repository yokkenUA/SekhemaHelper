using GameHelper;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SekhemaHelper
{
    // Own cross-process reader (GameHelper's SafeMemoryHandle methods are internal to plugins),
    // mirroring the proven pattern from the Atlas plugin.
    internal static class Mem
    {
        private static IntPtr handle = IntPtr.Zero;
        private static int handlePid;

        private static void EnsureHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (handle != IntPtr.Zero && handlePid == pid)
                return;
            Close();
            handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                ProcessMemoryUtilities.Native.ProcessAccessFlags.Read |
                ProcessMemoryUtilities.Native.ProcessAccessFlags.QueryInformation, pid);
            handlePid = pid;
        }

        // --- Heap-region enumeration + bulk read (for the honour signature scan) ---

        // Main-module address range of the game process (used by the honour scan's density pre-filter:
        // a heap region that holds no pointer back into the module is unlikely to hold a game object).
        public static bool GetModuleRange(out ulong modBase, out ulong modEnd)
        {
            modBase = 0;
            modEnd = 0;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)Core.Process.Pid);
                var main = proc.MainModule;
                if (main == null)
                    return false;
                modBase = (ulong)main.BaseAddress.ToInt64();
                modEnd = modBase + (ulong)main.ModuleMemorySize;
                return modEnd > modBase;
            }
            catch
            {
                return false;
            }
        }

        public readonly struct Region
        {
            public readonly IntPtr Base;
            public readonly long Size;
            public Region(IntPtr b, long s) { Base = b; Size = s; }
        }

        // Enumerate committed, readable, PRIVATE (heap) regions of the game process.
        public static List<Region> EnumerateHeapRegions()
        {
            EnsureHandle();
            var regions = new List<Region>();
            if (handle == IntPtr.Zero)
                return regions;

            ulong addr = 0x10000;
            const ulong maxAddr = 0x7FFFFFFEFFFF;
            int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
            while (addr < maxAddr)
            {
                if (VirtualQueryEx(handle, (IntPtr)addr, out var mbi, (UIntPtr)(ulong)mbiSize) == UIntPtr.Zero)
                    break;
                long regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0)
                    break;

                const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000;
                const uint PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
                bool readable = (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0 && mbi.Protect != 0;
                if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && readable)
                    regions.Add(new Region(mbi.BaseAddress, regionSize));

                ulong next = (ulong)mbi.BaseAddress.ToInt64() + (ulong)regionSize;
                if (next <= addr)
                    break;
                addr = next;
            }

            return regions;
        }

        // Bulk read with no 4096 cap (chunked region scanning). Returns the bytes read (exact-size
        // array), or empty on failure. Uses the 3-arg overload (reads buffer.Length) like
        // GameHelper's SafeMemoryHandle — the (buffer,int,out) overload's int is a buffer offset,
        // not a length, which corrupts indexing.
        public static byte[] ReadChunk(IntPtr address, int count)
        {
            if (address == IntPtr.Zero || count <= 0)
                return Array.Empty<byte>();
            EnsureHandle();
            if (handle == IntPtr.Zero)
                return Array.Empty<byte>();
            var buf = new byte[count];
            if (ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(
                    handle, address, buf, out IntPtr read))
            {
                int got = (int)read.ToInt64();
                if (got >= count)
                    return buf;
                if (got > 0)
                {
                    var t = new byte[got];
                    Array.Copy(buf, t, got);
                    return t;
                }
            }
            return Array.Empty<byte>();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(
            IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        public static void Close()
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
            handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero)
                return default;
            EnsureHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(handle, address, ref result);
            return result;
        }

        public static byte[] ReadBytes(IntPtr address, int count)
        {
            if (address == IntPtr.Zero || count <= 0 || count > 4096)
                return Array.Empty<byte>();
            EnsureHandle();
            var buf = new byte[count];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(handle, address, buf);
            return buf;
        }

        public static string ReadWideString(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars * 2);
            if (bytes.Length == 0)
                return string.Empty;
            var s = System.Text.Encoding.Unicode.GetString(bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        /// <summary>
        ///     Reads an MSVC std::wstring at <paramref name="address"/>, honouring SSO:
        ///     length @ +0x10, capacity @ +0x18; chars are inline (16-byte buffer @ +0x00)
        ///     when capacity &lt;= 7, otherwise a heap pointer @ +0x00.
        /// </summary>
        public static string ReadStdWString(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return string.Empty;
            long length = Read<long>(address + 0x10);
            long capacity = Read<long>(address + 0x18);
            if (length <= 0 || length > 1000 || capacity <= 0 || capacity > 1000)
                return string.Empty;
            if (capacity <= 7)
            {
                var bytes = ReadBytes(address, 16);
                if (bytes.Length < 2)
                    return string.Empty;
                var s = System.Text.Encoding.Unicode.GetString(bytes);
                return length < s.Length ? s.Substring(0, (int)length) : s;
            }

            return ReadWideString(Read<IntPtr>(address), (int)length);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
