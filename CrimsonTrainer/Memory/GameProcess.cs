using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CrimsonTrainer.Native;

namespace CrimsonTrainer.Memory;

/// <summary>
/// Handle to the game process plus the small set of memory operations the cheats need
/// (read / write / alloc / protect / suspend). This is the C# equivalent of what Cheat
/// Engine's auto assembler does behind the scenes for alloc(), fullaccess() and writes.
/// </summary>
internal sealed class GameProcess : IDisposable
{
    private const uint RequiredAccess =
        Kernel32.PROCESS_VM_OPERATION | Kernel32.PROCESS_VM_READ |
        Kernel32.PROCESS_VM_WRITE | Kernel32.PROCESS_QUERY_INFORMATION;

    private const int PageSize = 0x1000;

    public Process Process { get; }
    public nint Handle { get; }

    /// <summary>Main module name, e.g. "CrimsonDesert.exe".</summary>
    public string ModuleName { get; }

    /// <summary>Main module base — what "$process" / "CrimsonDesert.exe" resolve to in CE.</summary>
    public nint ModuleBase { get; }
    public int ModuleSize { get; }

    /// <summary>Placeholder used before a game is attached, so cheat metadata can exist without a process.</summary>
    public static GameProcess Detached { get; } = new();

    public bool IsDetached => Handle == 0;

    private GameProcess()
    {
        Process = null!;
        ModuleName = "CrimsonDesert.exe";
    }

    private GameProcess(Process process, nint handle, ProcessModule main)
    {
        Process = process;
        Handle = handle;
        ModuleName = main.ModuleName;
        ModuleBase = main.BaseAddress;
        ModuleSize = main.ModuleMemorySize;
    }

    /// <summary>Attaches to a running process by name (without ".exe"). Returns null if it is not running.</summary>
    public static GameProcess? TryAttach(string processName, int? pid = null)
    {
        var candidates = Process.GetProcessesByName(processName);
        var process = candidates.FirstOrDefault(p => !p.HasExited && (pid is null || p.Id == pid));
        foreach (var other in candidates)
            if (!ReferenceEquals(other, process)) other.Dispose();
        if (process is null) return null;

        var handle = Kernel32.OpenProcess(RequiredAccess, false, process.Id);
        if (handle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            process.Dispose();
            throw new Win32Exception(error, $"OpenProcess failed for PID {process.Id}. Try running the trainer as Administrator.");
        }

        ProcessModule main;
        try
        {
            main = process.MainModule ?? throw new InvalidOperationException("Process has no main module.");
        }
        catch (Win32Exception ex)
        {
            Kernel32.CloseHandle(handle);
            process.Dispose();
            throw new Win32Exception(ex.NativeErrorCode, $"Could not query the main module of PID {process.Id}. Try running the trainer as Administrator.");
        }

        return new GameProcess(process, handle, main);
    }

    public bool HasExited => IsDetached || Process.HasExited;

    /// <summary>Formats an address as "module+offset" when it falls inside the main module.</summary>
    public string Describe(nint address) =>
        address >= ModuleBase && address < ModuleBase + ModuleSize
            ? $"{ModuleName}+{(long)(address - ModuleBase):X}"
            : $"0x{(long)address:X}";

    public byte[] Read(nint address, int size)
    {
        var buffer = new byte[size];
        if (!TryRead(address, buffer, size))
            throw Win32Error($"ReadProcessMemory({Describe(address)}, {size})");
        return buffer;
    }

    public bool TryRead(nint address, byte[] buffer, int size) =>
        Kernel32.ReadProcessMemory(Handle, address, buffer, size, out var read) && read == size;

    /// <summary>
    /// Reads a range page by page, zero-filling pages that cannot be read. Used by the AOB
    /// scanner so a single unreadable page does not abort the whole module scan.
    /// </summary>
    public void ReadLenient(nint address, byte[] buffer, int size)
    {
        if (TryRead(address, buffer, size)) return;

        var page = new byte[PageSize];
        for (int offset = 0; offset < size; offset += PageSize)
        {
            int count = Math.Min(PageSize, size - offset);
            if (TryRead(address + offset, page, count))
                Array.Copy(page, 0, buffer, offset, count);
            else
                Array.Clear(buffer, offset, count);
        }
    }

    /// <summary>One read of the whole main module, so many patterns can be scanned without re-reading it.</summary>
    public byte[] ReadModuleImage()
    {
        var image = new byte[ModuleSize];
        ReadLenient(ModuleBase, image, ModuleSize);
        return image;
    }

    // ---- typed helpers (little-endian, like the game) ----

    public int ReadInt32(nint address) => BitConverter.ToInt32(Read(address, 4));
    public long ReadInt64(nint address) => BitConverter.ToInt64(Read(address, 8));
    public float ReadSingle(nint address) => BitConverter.ToSingle(Read(address, 4));
    public nint ReadPointer(nint address) => (nint)ReadInt64(address);

    public bool TryReadInt32(nint address, out int value)
    {
        var buf = new byte[4];
        bool ok = TryRead(address, buf, 4);
        value = ok ? BitConverter.ToInt32(buf) : 0;
        return ok;
    }

    public bool TryReadPointer(nint address, out nint value)
    {
        var buf = new byte[8];
        bool ok = TryRead(address, buf, 8);
        value = ok ? (nint)BitConverter.ToInt64(buf) : 0;
        return ok;
    }

    public void WriteInt32(nint address, int value) => Write(address, BitConverter.GetBytes(value));
    public void WriteInt64(nint address, long value) => Write(address, BitConverter.GetBytes(value));
    public void WriteSingle(nint address, float value) => Write(address, BitConverter.GetBytes(value));

    /// <summary>
    /// Follows a CE-style pointer chain: base is dereferenced, each offset is added and
    /// dereferenced again, and the final offset is added without dereferencing.
    /// Returns 0 when any pointer along the way is null / unreadable.
    /// </summary>
    public nint FollowChain(nint baseAddress, ReadOnlySpan<int> offsets, int finalOffset)
    {
        if (!TryReadPointer(baseAddress, out nint current) || current == 0) return 0;
        foreach (int offset in offsets)
        {
            if (!TryReadPointer(current + offset, out current) || current == 0) return 0;
        }
        return current + finalOffset;
    }

    private const uint MemPrivate = 0x20000;

    /// <summary>Every committed region the process can read (used by the full-memory AOB scan).</summary>
    public List<(nint Start, long Size)> EnumerateReadableRegions(bool privateOnly = false, long maxRegionSize = long.MaxValue)
    {
        var regions = new List<(nint, long)>();
        nint address = 0;
        int mbiSize = Marshal.SizeOf<Kernel32.MEMORY_BASIC_INFORMATION>();
        while (Kernel32.VirtualQueryEx(Handle, address, out var mbi, mbiSize) != 0)
        {
            bool readable = mbi.State == Kernel32.MEM_COMMIT_STATE
                            && (mbi.Protect & Kernel32.PAGE_GUARD) == 0
                            && mbi.Protect != Kernel32.PAGE_NOACCESS
                            && mbi.Protect != 0;
            if (privateOnly && mbi.Type != MemPrivate) readable = false;
            if ((long)mbi.RegionSize > maxRegionSize) readable = false;
            if (readable) regions.Add((mbi.BaseAddress, (long)mbi.RegionSize));

            nint next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= address) break; // wrapped around the address space
            address = next;
        }
        return regions;
    }

    /// <summary>
    /// Writes bytes, temporarily lifting page protection so read-only code pages can be
    /// patched, then flushes the instruction cache.
    /// </summary>
    public void Write(nint address, byte[] data)
    {
        if (!Kernel32.VirtualProtectEx(Handle, address, data.Length, Kernel32.PAGE_EXECUTE_READWRITE, out uint oldProtect))
            throw Win32Error($"VirtualProtectEx({Describe(address)})");
        try
        {
            if (!Kernel32.WriteProcessMemory(Handle, address, data, data.Length, out var written) || written != data.Length)
                throw Win32Error($"WriteProcessMemory({Describe(address)}, {data.Length})");
        }
        finally
        {
            Kernel32.VirtualProtectEx(Handle, address, data.Length, oldProtect, out _);
            Kernel32.FlushInstructionCache(Handle, address, data.Length);
        }
    }

    /// <summary>CE "alloc(newmem,size)": commits an RWX block anywhere in the target process.</summary>
    public nint Allocate(int size)
    {
        nint address = Kernel32.VirtualAllocEx(Handle, 0, size, Kernel32.MEM_COMMIT | Kernel32.MEM_RESERVE, Kernel32.PAGE_EXECUTE_READWRITE);
        if (address == 0) throw Win32Error($"VirtualAllocEx({size})");
        return address;
    }

    /// <summary>CE "dealloc": releases a block obtained from <see cref="Allocate"/>.</summary>
    public void Free(nint address)
    {
        if (address != 0) Kernel32.VirtualFreeEx(Handle, address, 0, Kernel32.MEM_RELEASE);
    }

    /// <summary>CE "fullaccess(address,size)": makes a range permanently RWX.</summary>
    public void MakeExecutableWritable(nint address, int size)
    {
        if (!Kernel32.VirtualProtectEx(Handle, address, size, Kernel32.PAGE_EXECUTE_READWRITE, out _))
            throw Win32Error($"VirtualProtectEx({Describe(address)}, {size:X})");
    }

    /// <summary>
    /// Suspends every thread of the game so a multi-byte code patch cannot be executed
    /// half-written. Dispose the returned scope to resume them.
    /// </summary>
    public ThreadSuspension SuspendThreads()
    {
        var handles = new List<nint>();
        Process.Refresh(); // Process.Threads is a cached snapshot
        foreach (ProcessThread thread in Process.Threads)
        {
            nint h = Kernel32.OpenThread(Kernel32.THREAD_SUSPEND_RESUME, false, thread.Id);
            if (h == 0) continue;
            if (Kernel32.SuspendThread(h) == uint.MaxValue)
            {
                Kernel32.CloseHandle(h);
                continue;
            }
            handles.Add(h);
        }
        return new ThreadSuspension(handles);
    }

    public void Dispose()
    {
        if (IsDetached) return;
        Kernel32.CloseHandle(Handle);
        Process.Dispose();
    }

    private static Win32Exception Win32Error(string what) =>
        new(Marshal.GetLastPInvokeError(), $"{what} failed");
}

/// <summary>Scope returned by <see cref="GameProcess.SuspendThreads"/>; resumes the threads on dispose.</summary>
internal sealed class ThreadSuspension : IDisposable
{
    private readonly List<nint> _threads;

    internal ThreadSuspension(List<nint> threads) => _threads = threads;

    public int Count => _threads.Count;

    public void Dispose()
    {
        foreach (nint h in _threads)
        {
            Kernel32.ResumeThread(h);
            Kernel32.CloseHandle(h);
        }
        _threads.Clear();
    }
}
