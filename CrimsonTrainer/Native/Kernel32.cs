using System.Runtime.InteropServices;

namespace CrimsonTrainer.Native;

/// <summary>Raw Win32 imports used by the trainer. Kept to the minimum the cheats need.</summary>
internal static partial class Kernel32
{
    // Process access rights
    public const uint PROCESS_VM_OPERATION      = 0x0008;
    public const uint PROCESS_VM_READ           = 0x0010;
    public const uint PROCESS_VM_WRITE          = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // Thread access rights
    public const uint THREAD_SUSPEND_RESUME = 0x0002;

    // VirtualAllocEx / VirtualFreeEx
    public const uint MEM_COMMIT  = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;

    // Page protection
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, [Out] byte[] lpBuffer, nint nSize, out nint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, [In] byte[] lpBuffer, nint nSize, out nint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAllocEx(nint hProcess, nint lpAddress, nint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(nint hProcess, nint lpAddress, nint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtectEx(nint hProcess, nint lpAddress, nint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nint dwSize);

    // VirtualQueryEx
    public const uint MEM_COMMIT_STATE = 0x1000;
    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_GUARD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nint dwLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SuspendThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint hThread);
}
