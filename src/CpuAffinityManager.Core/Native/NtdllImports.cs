using System.Runtime.InteropServices;

namespace CpuAffinityManager.Native;

/// <summary>
/// P/Invoke declarations for ntdll.dll undocumented NT API functions.
/// </summary>
public static class NtdllImports
{
    private const string Ntdll = "ntdll.dll";

    /// <summary>
    /// Sets information about a process. Undocumented NT API used to set
    /// process affinity mask (class 0x15) and CPU Sets (class 0x42).
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtSetInformationProcess(
        IntPtr ProcessHandle,
        PROCESS_INFORMATION_CLASS ProcessInformationClass,
        ref UIntPtr ProcessInformation,
        uint ProcessInformationLength);

    /// <summary>
    /// Queries system information. Used to retrieve CPU Set information
    /// (class 0x49) and logical processor info.
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtQuerySystemInformation(
        SYSTEM_INFORMATION_CLASS SystemInformationClass,
        IntPtr SystemInformation,
        uint SystemInformationLength,
        out uint ReturnLength);

    /// <summary>
    /// Opens a handle to a process, bypassing standard Win32 ACL checks.
    /// Requires SeDebugPrivilege for protected processes.
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtOpenProcess(
        out IntPtr ProcessHandle,
        uint DesiredAccess,
        ref OBJECT_ATTRIBUTES ObjectAttributes,
        ref CLIENT_ID ClientId);

    /// <summary>
    /// Sets information about a thread (e.g., ThreadAffinityMask = 0x04).
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtSetInformationThread(
        IntPtr ThreadHandle,
        THREAD_INFORMATION_CLASS ThreadInformationClass,
        ref UIntPtr ThreadInformation,
        uint ThreadInformationLength);

    /// <summary>
    /// Closes a handle opened via NtOpenProcess.
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtClose(IntPtr Handle);

    /// <summary>
    /// Queries information about a process (e.g., image path via ProcessImageFileName = 27).
    /// </summary>
    [DllImport(Ntdll, SetLastError = true)]
    public static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        uint ProcessInformationLength,
        out uint ReturnLength);
}
