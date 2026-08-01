using CpuAffinityManager.Cpu;

namespace CpuAffinityManager.Tests;

public class CpuTopologyTests
{
    [Fact]
    public void Detect_ReturnsValidTopology()
    {
        var service = new CpuTopologyService();
        var topology = service.Detect();

        Assert.NotNull(topology);
        Assert.True(topology.TotalLogicalProcessors > 0);
        Assert.True(topology.PcoreCount + topology.EcoreCount <= topology.TotalLogicalProcessors);
        Assert.True(topology.TotalLogicalProcessors <= 64 || topology.PcoreMask != 0);
        Assert.True(topology.SocketCount >= 1);
    }

    [Fact]
    public void Detect_IsCached()
    {
        var service = new CpuTopologyService();
        var t1 = service.Detect();
        var t2 = service.Detect();

        Assert.Same(t1, t2);
    }

    [Theory]
    [InlineData("all-cores")]
    [InlineData("p-cores")]
    [InlineData("p-cores-smt")]
    [InlineData("p-cores-no-smt")]
    [InlineData("first-half")]
    [InlineData("second-half")]
    public void BuildMask_PredefinedModes_ReturnsNonZeroMask(string mode)
    {
        var service = new CpuTopologyService();
        var topology = service.Detect();
        var mask = CpuTopology.BuildMask(topology, mode);

        Assert.True(mask != 0, $"Mode '{mode}' returned zero mask");
    }

    [Fact]
    public void BuildMask_EMode_MayBeZeroOnNonHybrid()
    {
        var service = new CpuTopologyService();
        var topology = service.Detect();
        var mask = CpuTopology.BuildMask(topology, "e-cores");
        // E-cores may be 0 on non-hybrid CPUs — that's valid, no assertion
    }

    [Fact]
    public void BuildMask_CustomMode_ReturnsCustomMask()
    {
        var service = new CpuTopologyService();
        var topology = service.Detect();
        ulong customMask = 0x0000000F;
        var mask = CpuTopology.BuildMask(topology, "custom", customMask);

        Assert.Equal(customMask, mask);
    }

    [Fact]
    public void BuildMask_CustomModeWithoutMask_ReturnsZero()
    {
        var service = new CpuTopologyService();
        var topology = service.Detect();
        var mask = CpuTopology.BuildMask(topology, "custom", null);

        Assert.Equal(0UL, mask);
    }

    #region Composite / Fallback Mode Tests

    [Fact]
    public void BuildMask_FallbackChain_UsesFirstNonZero()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 8,
            PcoreCount = 8,
            EcoreCount = 0,
            SmtEnabled = false,
            PcoreMask = 0xFF,
            EcoreMask = 0,
            SocketCount = 1
        };

        // "e-cores|first-half": e-cores mask is 0, so falls back to first-half
        ulong mask = CpuTopology.BuildMask(topology, "e-cores|first-half");

        Assert.Equal(0x0FUL, mask); // first-half of 8 cores = bits 0-3
    }

    [Fact]
    public void BuildMask_FallbackChain_UsesFirstWhenAvailable()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 12,
            PcoreCount = 8,
            EcoreCount = 4,
            PcoreMask = 0x0FF,  // bits 0-7 = P-cores
            EcoreMask = 0xF00,  // bits 8-11 = E-cores
            SocketCount = 1
        };

        // "p-cores|all-cores": p-cores is non-zero, so use it
        ulong mask = CpuTopology.BuildMask(topology, "p-cores|all-cores");

        Assert.Equal(0x0FFUL, mask); // P-cores only
    }

    [Fact]
    public void BuildMask_TripleFallback_ReturnsCorrect()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 8,
            PcoreCount = 0,
            EcoreCount = 0,
            PcoreMask = 0,
            EcoreMask = 0,
            SocketCount = 1
        };

        // "e-cores|p-cores|first-half": both e and p are 0, falls to first-half
        ulong mask = CpuTopology.BuildMask(topology, "e-cores|p-cores|first-half");

        Assert.Equal(0x0FUL, mask); // first-half of 8
    }

    #endregion

    #region Socket Filter Tests

    [Fact]
    public void BuildMask_SocketFilter_RestrictsToSocket()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 16,
            PcoreCount = 16,
            PcoreMask = 0xFFFF,
            SocketCount = 2,
            SocketMasks = new List<ulong>
            {
                0x00FF, // Socket 0: bits 0-7
                0xFF00  // Socket 1: bits 8-15
            }
        };

        // "all-cores@socket0" should restrict to socket 0 cores
        ulong mask0 = CpuTopology.BuildMask(topology, "all-cores@socket0");
        Assert.Equal(0x00FFUL, mask0);

        // "all-cores@socket1" should restrict to socket 1 cores
        ulong mask1 = CpuTopology.BuildMask(topology, "all-cores@socket1");
        Assert.Equal(0xFF00UL, mask1);
    }

    [Fact]
    public void BuildMask_InvalidSocket_ReturnsZero()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 8,
            PcoreCount = 8,
            PcoreMask = 0xFF,
            SocketCount = 1,
            SocketMasks = new List<ulong> { 0xFF }
        };

        // Socket 5 doesn't exist → should return 0
        ulong mask = CpuTopology.BuildMask(topology, "all-cores@socket5");
        Assert.Equal(0UL, mask);
    }

    [Fact]
    public void BuildMask_NoSocketSuffix_UsesAllCores()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 16,
            PcoreCount = 16,
            PcoreMask = 0xFFFF,
            SocketCount = 2,
            SocketMasks = new List<ulong> { 0x00FF, 0xFF00 }
        };

        // No @socket suffix → all cores
        ulong mask = CpuTopology.BuildMask(topology, "all-cores");
        Assert.Equal(0xFFFFUL, mask);
    }

    #endregion

    #region Half Mask Tests

    [Fact]
    public void BuildHalfMask_FirstHalf_CorrectSize()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 8,
            PcoreCount = 8,
            PcoreMask = 0xFF,
            SocketCount = 1
        };

        ulong mask = CpuTopology.BuildHalfMask(topology, firstHalf: true);
        Assert.Equal(0x0FUL, mask);
    }

    [Fact]
    public void BuildHalfMask_SecondHalf_CorrectSize()
    {
        var topology = new CpuTopology
        {
            TotalLogicalProcessors = 8,
            PcoreCount = 8,
            PcoreMask = 0xFF,
            SocketCount = 1
        };

        ulong mask = CpuTopology.BuildHalfMask(topology, firstHalf: false);
        Assert.Equal(0xF0UL, mask);
    }

    #endregion
}
