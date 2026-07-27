using System.Runtime.InteropServices;
using CpuAffinityManager.Native;

namespace CpuAffinityManager.Cpu;

/// <summary>
/// Detects CPU topology by combining CPU Set information (for efficiency classes
/// on hybrid architectures) with logical processor information (for core/SMT/CCD layout).
/// </summary>
public class CpuTopologyService : ICpuTopologyService
{
    private CpuTopology? _cached;
    private readonly object _lock = new();

    public CpuTopology Detect()
    {
        if (_cached != null)
            return _cached;

        lock (_lock)
        {
            if (_cached != null)
                return _cached;

            _cached = DetectInternal();
            return _cached;
        }
    }

    public ulong BuildMask(string mode, ulong? customMask = null)
    {
        var topology = Detect();
        return CpuTopology.BuildMask(topology, mode, customMask);
    }

    private static CpuTopology DetectInternal()
    {
        int totalLogical = Environment.ProcessorCount;

        // Default: fallback for systems without hybrid architecture
        ulong pcoreMask = ~0UL;
        ulong ecoreMask = 0;
        ulong smt0Mask = 0;
        ulong smt1Mask = 0;
        ulong ccd0Mask = 0;
        ulong ccd1Mask = 0;
        int pcoreCount = totalLogical;
        int ecoreCount = 0;
        bool smtEnabled = false;

        // Step 1: Try CPU Set information for efficiency class detection
        var efficiencyMap = QueryCpuSetEfficiency(totalLogical);

        if (efficiencyMap.Count > 0)
        {
            pcoreMask = 0;
            ecoreMask = 0;
            pcoreCount = 0;
            ecoreCount = 0;
            byte maxEfficiencyClass = efficiencyMap.Values.Max();
            byte minEfficiencyClass = efficiencyMap.Values.Min();

            foreach (var kvp in efficiencyMap)
            {
                int lpIndex = kvp.Key;
                byte effClass = kvp.Value;

                if (lpIndex >= 64) continue; // Beyond 64-bit mask range

                ulong bit = 1UL << lpIndex;

                if (maxEfficiencyClass > minEfficiencyClass && effClass < maxEfficiencyClass)
                {
                    // Lower efficiency class = E-core on hybrid CPUs.
                    ecoreMask |= bit;
                    ecoreCount++;
                }
                else
                {
                    // Highest efficiency class, or all CPUs on non-hybrid systems.
                    pcoreMask |= bit;
                    pcoreCount++;
                }
            }

            // Fallback: if no P-cores detected (non-hybrid or older CPU),
            // treat all cores as P-cores
            if (pcoreMask == 0 && ecoreMask != 0)
            {
                pcoreMask = ecoreMask;
                pcoreCount = ecoreCount;
                ecoreMask = 0;
                ecoreCount = 0;
            }
        }

        // Step 2: Detect SMT topology using GetLogicalProcessorInformation
        var smtResult = DetectSmtLayout(totalLogical);
        if (smtResult.HasValue)
        {
            smtEnabled = smtResult.Value.smtEnabled;
            smt0Mask = smtResult.Value.smt0Mask;
            smt1Mask = smtResult.Value.smt1Mask;
        }

        // Step 3: Detect AMD CCD layout (for dual-CCD Ryzen)
        var ccdResult = DetectCcdLayout();
        if (ccdResult.HasValue)
        {
            ccd0Mask = ccdResult.Value.ccd0Mask;
            ccd1Mask = ccdResult.Value.ccd1Mask;
        }

        // Step 4: Detect multi-socket / multi-package layout
        var socketResult = DetectSockets();
        int socketCount = socketResult.socketCount;
        List<ulong> socketMasks = socketResult.socketMasks;

        return new CpuTopology
        {
            TotalLogicalProcessors = totalLogical,
            PcoreCount = pcoreCount,
            EcoreCount = ecoreCount,
            SmtEnabled = smtEnabled,
            PcoreMask = pcoreMask,
            EcoreMask = ecoreMask,
            Smt0Mask = smt0Mask,
            Smt1Mask = smt1Mask,
            Ccd0Mask = ccd0Mask,
            Ccd1Mask = ccd1Mask,
            SocketCount = socketCount,
            SocketMasks = socketMasks,
        };
    }

    /// <summary>
    /// Queries CPU Set information to get efficiency class per logical processor.
    /// Uses the undocumented SystemCpuSetInformation (0x49) class on Windows 11.
    /// </summary>
    private static unsafe Dictionary<int, byte> QueryCpuSetEfficiency(int totalLogicalProcessors)
    {
        var result = new Dictionary<int, byte>();

        // Try to get the required buffer size
        if (!Kernel32Imports.GetSystemCpuSetInformation(
            IntPtr.Zero,
            0,
            out uint returnLength,
            IntPtr.Zero,
            0) && returnLength == 0)
        {
            return result;
        }

        if (returnLength == 0)
            return result;

        // Allocate buffer and query
        IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
        try
        {
            if (!Kernel32Imports.GetSystemCpuSetInformation(
                buffer,
                returnLength,
                out _,
                IntPtr.Zero,
                0))
            {
                return result;
            }

            // Parse the buffer: array of SYSTEM_CPU_SET_INFORMATION
            uint offset = 0;
            while (offset < returnLength)
            {
                var cpuSet = Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(buffer + (int)offset);
                if (cpuSet.Size == 0)
                    break;

                if (cpuSet.Type == CPU_SET_INFORMATION_TYPE.CpuSetInformation)
                {
                    // Map logical processor index to efficiency class
                    byte efficiencyClass = cpuSet.CpuSet.EfficiencyClass;
                    // Use Id as a fallback key — for most single-group systems
                    // this is the logical processor index
                    if (cpuSet.CpuSet.Group == 0 && cpuSet.CpuSet.LogicalProcessorIndex < 64)
                    {
                        result[cpuSet.CpuSet.LogicalProcessorIndex] = efficiencyClass;
                    }
                }

                offset += cpuSet.Size;
            }

            if (result.Count < Math.Min(totalLogicalProcessors, 64))
                result.Clear();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// Detects SMT layout by examining which logical processors share the same physical core.
    /// </summary>
    private static (bool smtEnabled, ulong smt0Mask, ulong smt1Mask)? DetectSmtLayout(int totalLogical)
    {
        if (totalLogical > 64) return null; // Beyond single-group mask range

        // Use GetLogicalProcessorInformation to find core relationships
        uint bufferSize = 0;
        Kernel32Imports.GetLogicalProcessorInformation(IntPtr.Zero, ref bufferSize);

        if (bufferSize == 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (!Kernel32Imports.GetLogicalProcessorInformation(buffer, ref bufferSize))
            {
                int error = Marshal.GetLastWin32Error();
                // On older systems, use GetLogicalProcessorInformationEx
                // Fallback: assume 2-way SMT if logical count > physical count
                var (physical, _) = CountPhysicalCores(buffer, bufferSize);
                if (physical > 0 && physical < totalLogical && totalLogical <= physical * 2)
                {
                    // Simple interleaved SMT: even = SMT0, odd = SMT1
                    ulong smt0 = 0, smt1 = 0;
                    for (int i = 0; i < totalLogical; i++)
                    {
                        int coreSlot = i / (totalLogical / physical);
                        if (coreSlot == 0)
                            smt0 |= 1UL << i;
                        else
                            smt1 |= 1UL << i;
                    }
                    return (true, smt0, smt1);
                }
                return null;
            }

            var (physCores, _) = CountPhysicalCores(buffer, bufferSize);

            if (physCores > 0 && physCores < totalLogical)
            {
                // Detected SMT: physical cores < logical processors
                // Build masks by iterating logical processors by core
                ulong smt0 = 0, smt1 = 0;
                var coreLpMap = BuildSmtMap(buffer, bufferSize, totalLogical);
                foreach (var (lpIndex, smtId) in coreLpMap)
                {
                    if (lpIndex >= 64) continue;
                    if (smtId == 0)
                        smt0 |= 1UL << lpIndex;
                    else
                        smt1 |= 1UL << lpIndex;
                }
                return (true, smt0, smt1);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return null;
    }

    /// <summary>
    /// Builds a map of logical processor index → SMT thread ID (0 or 1).
    /// </summary>
    private static Dictionary<int, int> BuildSmtMap(IntPtr buffer, uint bufferSize, int totalLogical)
    {
        var map = new Dictionary<int, int>();
        int offset = 0;
        int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();

        while (offset + structSize <= (int)bufferSize)
        {
            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + offset);

            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
            {
                ulong mask = (ulong)info.ProcessorMask;
                int smtId = 0;
                for (int i = 0; i < totalLogical; i++)
                {
                    if ((mask & (1UL << i)) != 0)
                    {
                        map[i] = smtId++;
                    }
                }
            }

            offset += structSize;
        }

        return map;
    }

    /// <summary>
    /// Counts physical cores and packages from logical processor info.
    /// </summary>
    private static (int physicalCores, int packages) CountPhysicalCores(IntPtr buffer, uint bufferSize)
    {
        int physicalCores = 0;
        int packages = 0;
        int offset = 0;
        int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();

        while (offset + structSize <= (int)bufferSize)
        {
            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + offset);

            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                physicalCores++;
            else if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage)
                packages++;

            offset += structSize;
        }

        return (physicalCores, packages);
    }

    /// <summary>
    /// Detects physical CPU sockets/packages. Returns socket count and per-socket masks.
    /// </summary>
    private static (int socketCount, List<ulong> socketMasks) DetectSockets()
    {
        var socketMasks = new List<ulong>();

        uint bufferSize = 0;
        Kernel32Imports.GetLogicalProcessorInformation(IntPtr.Zero, ref bufferSize);
        if (bufferSize == 0)
            return (1, socketMasks);

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (!Kernel32Imports.GetLogicalProcessorInformation(buffer, ref bufferSize))
                return (1, socketMasks);

            int offset = 0;
            int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();

            while (offset + structSize <= (int)bufferSize)
            {
                var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + offset);

                if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage)
                {
                    socketMasks.Add((ulong)info.ProcessorMask);
                }

                offset += structSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return (socketMasks.Count > 0 ? socketMasks.Count : 1, socketMasks);
    }

    /// <summary>
    /// Detects AMD CCD layout by finding ProcessorDie relationships.
    /// CCD0 and CCD1 are separate dies on dual-CCD Ryzen processors.
    /// </summary>
    private static (ulong ccd0Mask, ulong ccd1Mask)? DetectCcdLayout()
    {
        uint bufferSize = 0;
        Kernel32Imports.GetLogicalProcessorInformation(IntPtr.Zero, ref bufferSize);

        if (bufferSize == 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (!Kernel32Imports.GetLogicalProcessorInformation(buffer, ref bufferSize))
                return null;

            int offset = 0;
            int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
            var dieMasks = new List<ulong>();

            while (offset + structSize <= (int)bufferSize)
            {
                var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + offset);

                if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorDie)
                {
                    dieMasks.Add((ulong)info.ProcessorMask);
                }

                offset += structSize;
            }

            if (dieMasks.Count >= 2)
            {
                return (dieMasks[0], dieMasks[1]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return null;
    }
}
