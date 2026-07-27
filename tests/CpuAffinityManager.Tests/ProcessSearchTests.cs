using CpuAffinityManager.Monitoring;

namespace CpuAffinityManager.Tests;

public class ProcessSearchTests
{
    [Theory]
    [InlineData("cpuz", "cpuz_x64.exe", null, 1234, true)]
    [InlineData("cpu-z", "cpuz_x64.exe", null, 1234, true)]
    [InlineData("cpuz", "cpu-z_x64.exe", null, 1234, true)]
    [InlineData("cpuz", "CPU-Z-v2.08.0-CN.exe", null, 1234, true)]
    [InlineData("cpu-z", "CPU-Z-v2.08.0-CN.exe", null, 1234, true)]
    [InlineData("x64", "cpuz_x64.exe", null, 1234, true)]
    [InlineData("Tools", "cpuz_x64.exe", @"C:\Tools\CPU-Z\cpuz_x64.exe", 1234, true)]
    [InlineData("1234", "cpuz_x64.exe", null, 1234, true)]
    [InlineData("gpu-z", "cpuz_x64.exe", null, 1234, false)]
    public void Matches_FindsCpuZNameVariantsAndMetadata(
        string query,
        string name,
        string? path,
        int pid,
        bool expected)
    {
        Assert.Equal(expected, ProcessSearch.Matches(query, name, path, pid));
    }
}
