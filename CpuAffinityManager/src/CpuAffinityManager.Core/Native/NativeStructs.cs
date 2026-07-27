using System.Runtime.InteropServices;

namespace CpuAffinityManager.Native;

/// <summary>
/// Native structs for NT API interop.
/// </summary>

[StructLayout(LayoutKind.Sequential)]
public struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
public struct OBJECT_ATTRIBUTES
{
    public uint Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;

    public static OBJECT_ATTRIBUTES Create()
    {
        return new OBJECT_ATTRIBUTES
        {
            Length = (uint)Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
            RootDirectory = IntPtr.Zero,
            ObjectName = IntPtr.Zero,
            Attributes = 0x40, // OBJ_CASE_INSENSITIVE
            SecurityDescriptor = IntPtr.Zero,
            SecurityQualityOfService = IntPtr.Zero
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct CLIENT_ID
{
    public IntPtr UniqueProcess;
    public IntPtr UniqueThread;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_CPU_SET_INFORMATION
{
    public uint Size;
    public CPU_SET_INFORMATION_TYPE Type;
    public CpuSetUnion CpuSet;

    [StructLayout(LayoutKind.Explicit)]
    public struct CpuSetUnion
    {
        [FieldOffset(0)] public uint Id;
        [FieldOffset(4)] public ushort Group;
        [FieldOffset(6)] public byte LogicalProcessorIndex;
        [FieldOffset(7)] public byte CoreIndex;
        [FieldOffset(8)] public byte LastLevelCacheIndex;
        [FieldOffset(9)] public byte NumaNodeIndex;
        [FieldOffset(10)] public byte EfficiencyClass;
        [FieldOffset(11)] public byte AllFlags;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public ulong AllocationTag;

        // Individual flag accessors via bit positions in AllFlags.
        public bool Parked => (AllFlags & 0x01) != 0;
        public bool Allocated => (AllFlags & 0x02) != 0;
        public bool AllocatedToTargetProcess => (AllFlags & 0x04) != 0;
        public bool RealTime => (AllFlags & 0x08) != 0;
        public bool IsBigLittleCapable => (AllFlags & 0x10) != 0;
    }
}

public enum CPU_SET_INFORMATION_TYPE : uint
{
    CpuSetInformation = 0
}

public enum PROCESS_INFORMATION_CLASS : uint
{
    ProcessBasicInformation = 0,
    ProcessAffinityMask = 0x15,
    ProcessAffinityUpdateMode = 0x16,
    ProcessIoCounters = 0x20,
    ProcessDefaultCpuSets = 0x42,
    ProcessPowerThrottling = 0x6D,
    ProcessEfficiencyMode = 0x6E
}

public enum SYSTEM_INFORMATION_CLASS : uint
{
    SystemBasicInformation = 0,
    SystemProcessorInformation = 1,
    SystemPerformanceInformation = 2,
    SystemProcessorPerformanceInformation = 8,
    SystemCpuSetInformation = 0x49,
    SystemLogicalProcessorInformation = 0x5F
}

public enum THREAD_INFORMATION_CLASS : uint
{
    ThreadBasicInformation = 0,
    ThreadAffinityMask = 0x04
}

// PROCESS_ACCESS flags for NtOpenProcess
public static class ProcessAccess
{
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_CREATE_THREAD = 0x0002;
    public const uint PROCESS_SET_SESSIONID = 0x0004;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint PROCESS_CREATE_PROCESS = 0x0080;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;
}

// NT_STATUS codes
public static class NtStatus
{
    public const uint STATUS_SUCCESS = 0x00000000;
    public const uint STATUS_ACCESS_DENIED = 0xC0000022;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    public const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;
    public const uint STATUS_PROCESS_IS_TERMINATING = 0xC000010A;
}

// Job Object structures
public enum JOBOBJECTINFOCLASS
{
    JobObjectBasicAccountingInformation = 1,
    JobObjectBasicLimitInformation = 2,
    JobObjectBasicProcessIdList = 3,
    JobObjectBasicUIRestrictions = 4,
    JobObjectSecurityLimitInformation = 5,
    JobObjectEndOfJobTimeInformation = 6,
    JobObjectAssociateCompletionPortInformation = 7,
    JobObjectBasicAndIoAccountingInformation = 8,
    JobObjectExtendedLimitInformation = 9,
    JobObjectJobSetInformation = 10,
    JobObjectGroupInformation = 11,
    JobObjectNotificationLimitInformation = 12,
    JobObjectLimitViolationInformation = 13,
    JobObjectGroupInformationEx = 14,
    JobObjectCpuAffinityLimitInformation = 15,  // Undocumented, Windows 11
    JobObjectCpuRateControlInformation = 15,     // Server 2008+
    MaxJobObjectInfoClass = 18
}

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_CPU_AFFINITY_LIMIT_INFORMATION
{
    public uint Flags;          // 1 = EnableAffinity
    public ushort GroupNumber;  // Processor group (usually 0)
    public ushort Reserved;
    public ulong AffinityMask;  // 64-bit mask for up to 64 logical processors
}

// Job Object Limit Flags
public static class JobLimitFlags
{
    public const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x0001;
    public const uint JOB_OBJECT_LIMIT_PROCESS_TIME = 0x0002;
    public const uint JOB_OBJECT_LIMIT_JOB_TIME = 0x0004;
    public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x0008;
    public const uint JOB_OBJECT_LIMIT_AFFINITY = 0x0010;
    public const uint JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x0020;
    public const uint JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME = 0x0040;
    public const uint JOB_OBJECT_LIMIT_SCHEDULING_CLASS = 0x0080;
    public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x0100;
    public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x0200;
    public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x0400;
    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;
    public const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    public const uint JOB_OBJECT_LIMIT_SUBSET_AFFINITY = 0x4000;
}

// System Logical Processor Information (for GetLogicalProcessorInformation)
[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
{
    public UIntPtr ProcessorMask;
    public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
    public ProcessorUnion Union;

    [StructLayout(LayoutKind.Explicit)]
    public struct ProcessorUnion
    {
        [FieldOffset(0)] public ProcessorCore Core;
        [FieldOffset(0)] public ProcessorNumaNode NumaNode;
        [FieldOffset(0)] public ProcessorCache Cache;
        [FieldOffset(0)] public ulong Reserved0;
        [FieldOffset(0)] public ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorCore
    {
        public byte Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorNumaNode
    {
        public uint NodeNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorCache
    {
        public byte Level;
        public byte Associativity;
        public ushort LineSize;
        public uint Size;
        public PROCESSOR_CACHE_TYPE Type;
    }
}

public enum LOGICAL_PROCESSOR_RELATIONSHIP
{
    RelationProcessorCore = 0,
    RelationNumaNode = 1,
    RelationCache = 2,
    RelationProcessorPackage = 3,
    RelationGroup = 4,
    RelationProcessorDie = 5,
    RelationNumaNodeEx = 6,
    RelationProcessorModule = 7,
    RelationAll = 0xFFFF
}

public enum PROCESSOR_CACHE_TYPE
{
    CacheUnified = 0,
    CacheInstruction = 1,
    CacheData = 2,
    CacheTrace = 3
}

// Token privilege structure
[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID_AND_ATTRIBUTES Privileges;

    public TOKEN_PRIVILEGES(LUID luid, uint attributes)
    {
        PrivilegeCount = 1;
        Privileges = new LUID_AND_ATTRIBUTES
        {
            Luid = luid,
            Attributes = attributes
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

public static class TokenPrivilegeAttributes
{
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    public const uint SE_PRIVILEGE_ENABLED_BY_DEFAULT = 0x00000001;
    public const uint SE_PRIVILEGE_REMOVED = 0x00000004;
}
