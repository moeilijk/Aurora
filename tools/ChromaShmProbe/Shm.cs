using System.Runtime.InteropServices;

namespace ChromaShmProbe;

/// <summary>Read-only access to named Win32 file-mapping (shared memory) sections.</summary>
public static class Shm
{
    private const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32")]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>Returns the full byte content of the named mapping, or null if it cannot be opened.</summary>
    public static byte[]? Read(string name)
    {
        var h = OpenFileMappingW(FILE_MAP_READ, false, name);
        if (h == IntPtr.Zero) return null;
        try
        {
            var p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (p == IntPtr.Zero) return null;
            try
            {
                VirtualQuery(p, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
                long size = (long)mbi.RegionSize;
                if (size <= 0) return Array.Empty<byte>();
                if (size > 64 * 1024 * 1024) size = 64 * 1024 * 1024; // sanity cap
                var buf = new byte[size];
                Marshal.Copy(p, buf, 0, (int)size);
                return buf;
            }
            finally { UnmapViewOfFile(p); }
        }
        finally { CloseHandle(h); }
    }
}
