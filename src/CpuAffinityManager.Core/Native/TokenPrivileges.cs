namespace CpuAffinityManager.Native;

/// <summary>
/// Helper class for enabling and managing Windows security privileges.
/// Used to obtain SeDebugPrivilege for accessing protected processes.
/// </summary>
public static class TokenPrivileges
{
    /// <summary>
    /// Common privilege names used by the tool.
    /// </summary>
    public static class Privilege
    {
        public const string SeDebug = "SeDebugPrivilege";
        public const string SeIncreaseBasePriority = "SeIncreaseBasePriorityPrivilege";
    }

    /// <summary>
    /// Enables the specified privilege for the current process.
    /// Returns true if successful.
    /// </summary>
    /// <param name="privilegeName">Privilege name, e.g., "SeDebugPrivilege".</param>
    /// <returns>true if the privilege was enabled successfully.</returns>
    public static bool Enable(string privilegeName)
    {
        // Open the access token for the current process
        if (!Kernel32Imports.OpenProcessToken(
                Kernel32Imports.GetCurrentProcess(),
                Kernel32Imports.TOKEN_QUERY | Kernel32Imports.TOKEN_ADJUST_PRIVILEGES,
                out IntPtr hToken))
        {
            return false;
        }

        try
        {
            // Lookup the privilege LUID
            if (!Kernel32Imports.LookupPrivilegeValue(null, privilegeName, out LUID luid))
            {
                return false;
            }

            // Enable the privilege
            var tp = new TOKEN_PRIVILEGES(luid, TokenPrivilegeAttributes.SE_PRIVILEGE_ENABLED);

            // Ignore previous state
            bool result = Kernel32Imports.AdjustTokenPrivileges(
                hToken,
                false,
                ref tp,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<TOKEN_PRIVILEGES>(),
                IntPtr.Zero,
                IntPtr.Zero);

            // AdjustTokenPrivileges can return true even if the privilege wasn't assigned.
            // Check GetLastError for ERROR_NOT_ALL_ASSIGNED (1300).
            if (!result)
                return false;

            int lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return lastError != 1300; // ERROR_NOT_ALL_ASSIGNED
        }
        finally
        {
            Kernel32Imports.CloseHandle(hToken);
        }
    }

    /// <summary>
    /// Enables SeDebugPrivilege. Required for accessing protected/high-integrity
    /// processes via NtOpenProcess.
    /// </summary>
    public static bool EnableDebugPrivilege()
    {
        return Enable(Privilege.SeDebug);
    }
}
