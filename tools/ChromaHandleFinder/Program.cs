using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Identify, with no guessing, WHO holds the Chroma keyboard color buffer section
// (Global\{74164FAD-...}) open and with what access. SECTION_MAP_WRITE (0x0002) = a writer
// (the renderer that produces the colours); SECTION_MAP_READ (0x0004) = a reader/consumer
// (e.g. Aurora's RazerSdkReader). Run elevated so OpenProcess(PROCESS_DUP_HANDLE) works on
// system services like RzSDKService.
internal static class Program
{
    private const int SystemExtendedHandleInformation = 64;
    private const int PROCESS_DUP_HANDLE = 0x0040;
    private const int DUPLICATE_SAME_ACCESS = 0x2;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")] private static extern uint NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);
    [DllImport("ntdll.dll")] private static extern uint NtQueryObject(IntPtr h, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, int access, bool inherit, int opts);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private static int Main(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "74164FAD";
        int len = 0x200000;
        IntPtr buf = Marshal.AllocHGlobal(len);
        uint st;
        while ((st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out _)) == STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(buf); len *= 2; buf = Marshal.AllocHGlobal(len);
        }
        if (st != 0) { Console.WriteLine($"NtQuerySystemInformation failed 0x{st:X}"); return 1; }

        long count = Marshal.ReadIntPtr(buf).ToInt64();
        int entrySize = Marshal.SizeOf<HandleEntry>();
        IntPtr basePtr = buf + IntPtr.Size * 2;
        IntPtr self = GetCurrentProcess();
        var procCache = new Dictionary<int, IntPtr>();
        var names = new Dictionary<int, string>();
        Console.WriteLine($"Scanning {count} handles for section '{target}' ...");
        int hits = 0;
        for (long i = 0; i < count; i++)
        {
            var e = Marshal.PtrToStructure<HandleEntry>(basePtr + (int)(i * entrySize));
            int pid = e.UniqueProcessId.ToInt32();
            if (pid <= 4) continue;
            if (!procCache.TryGetValue(pid, out var ph))
            {
                ph = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                procCache[pid] = ph;
            }
            if (ph == IntPtr.Zero) continue;
            if (!DuplicateHandle(ph, e.HandleValue, self, out var dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
            try
            {
                if (QueryStr(dup, ObjectTypeInformation) != "Section") continue; // type-first avoids name-query hangs
                var name = QueryStr(dup, ObjectNameInformation);
                if (string.IsNullOrEmpty(name) || name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!names.TryGetValue(pid, out var pname))
                {
                    try { pname = Process.GetProcessById(pid).ProcessName; } catch { pname = "?"; }
                    names[pid] = pname;
                }
                bool w = (e.GrantedAccess & 0x0002) != 0;
                bool r = (e.GrantedAccess & 0x0004) != 0;
                Console.WriteLine($"PID {pid,6}  {pname,-24} access=0x{e.GrantedAccess:X6}  {(w ? "WRITE" : "")}{(r ? " READ" : "")}  {name}");
                hits++;
            }
            finally { CloseHandle(dup); }
        }
        Console.WriteLine(hits == 0 ? "No process holds that section open." : $"{hits} holder(s) found.");
        return 0;
    }

    private static string QueryStr(IntPtr h, int cls)
    {
        int len = 0x2000;
        IntPtr b = Marshal.AllocHGlobal(len);
        try
        {
            if (NtQueryObject(h, cls, b, len, out _) != 0) return null;
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; (pad) PWSTR Buffer; } — Buffer at offset 8 on x64.
            ushort l = (ushort)Marshal.ReadInt16(b);
            IntPtr strPtr = Marshal.ReadIntPtr(b, 8);
            if (l == 0 || strPtr == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(strPtr, l / 2);
        }
        finally { Marshal.FreeHGlobal(b); }
    }
}
