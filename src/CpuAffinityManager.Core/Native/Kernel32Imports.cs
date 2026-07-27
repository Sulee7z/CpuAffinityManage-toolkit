using System.Runtime.InteropServices;

namespace CpuAffinityManager.Native;

/// <summary>
/// P/Invoke declarations for kernel32.dll. Used primarily for Job Object
/// operations, which remain documented and stable.
/// </summary>
public static class Kernel32Imports
{
    private const string Kernel32 = "kernel32.dll";

    /// <summary>
    /// Creates or opens a Job Object.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    /// <summary>
    /// Sets information on a Job Object (affinity limits, basic limits, etc.).
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    /// <summary>
    /// Assigns a process to a Job Object.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    /// <summary>
    /// Determines whether a process is running in a job object.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool IsProcessInJob(
        IntPtr ProcessHandle,
        IntPtr JobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool Result);

    /// <summary>
    /// Opens a handle to an existing process. May be blocked by ACL on
    /// anti-cheat protected processes — use NtOpenProcess as fallback.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwProcessId);

    /// <summary>
    /// Sets the hard affinity mask for a process.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr dwProcessAffinityMask);

    /// <summary>
    /// Closes a handle opened via OpenProcess or CreateJobObject.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Retrieves the full image name of a process.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    /// <summary>
    /// Retrieves information about logical processors, including relationship
    /// types (Core, Numa, Cache, Package).
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool GetLogicalProcessorInformation(
        IntPtr Buffer,
        ref uint ReturnLength);

    /// <summary>
    /// Retrieves CPU Set information, including efficiency class for hybrid CPUs.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool GetSystemCpuSetInformation(
        IntPtr Information,
        uint BufferLength,
        out uint ReturnedLength,
        IntPtr Process,
        uint Flags);

    /// <summary>
    /// Sets a process's default CPU Sets — the REAL soft-affinity mechanism: the
    /// scheduler keeps the process's threads on these CPU sets. Pass null/0 to clear.
    /// Expects CPU-set IDs (from GetSystemCpuSetInformation), NOT an affinity mask.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool SetProcessDefaultCpuSets(
        IntPtr Process,
        uint[]? CpuSetIds,
        uint CpuSetIdCount);

    /// <summary>
    /// Retrieves a handle to the current process (pseudo-handle, no close needed).
    /// </summary>
    [DllImport(Kernel32)]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Opens the access token for a process.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    /// <summary>
    /// Queries the value of a specific privilege in an access token.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(
        string? lpSystemName,
        string lpName,
        out LUID lpLuid);

    /// <summary>
    /// Enables or disables privileges in an access token.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    // Token access constants
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
}
