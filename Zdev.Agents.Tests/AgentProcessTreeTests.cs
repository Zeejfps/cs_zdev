using System.Diagnostics;
using Zdev.Agents.Acp;
using Zdev.Common;
using Xunit;

namespace Zdev.Agents.Tests;

/// <summary>A test that only means something on Windows, where an agent's processes are held in a
/// job object.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only: there an agent's processes are held in a job object.";
    }
}

/// <summary>Ending an agent ends every process it started, even one whose parent has already exited,
/// as a command wrapper such as npx's does once it has started the real adapter.</summary>
public sealed class AgentProcessTreeTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "zdev-agent-tree-" + Guid.NewGuid().ToString("N"));

    public AgentProcessTreeTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => DirectoryTree.Delete(_folder);

    [WindowsFact]
    public async Task Ending_an_agent_ends_a_process_its_exited_wrapper_left_running()
    {
        var pidFile = Path.Combine(_folder, "orphan.pid");
        var harness = OrphaningHarness(pidFile);
        using var cancel = new CancellationTokenSource();

        var starting = AcpAgentConnection.StartAsync(harness, _folder, null, new NoPrompt(), CurrentEnvironment(), cancel.Token);
        var orphan = await WaitForPid(pidFile);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        Assert.True(HasExited(orphan), $"process {orphan} outlived its agent");
    }

    /// <summary>Whether process <paramref name="pid"/> is gone, or goes within the timeout.</summary>
    private static bool HasExited(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }
        using (process)
            return process.WaitForExit(ExitTimeout);
    }

    /// <summary>An "agent" that is cmd starting PowerShell in the background and exiting at once,
    /// leaving PowerShell running with no live parent. PowerShell writes its id, then waits.</summary>
    private static AcpHarness OrphaningHarness(string pidFile)
    {
        var script = $"$PID | Out-File -Encoding ascii '{pidFile}'; Start-Sleep 120";
        string[] args = ["/c", "start", "\"\"", "/b", "powershell", "-NoProfile", "-Command", script];
        return new AcpHarness(new AcpHarnessId("orphaning"), "Orphaning", "cmd.exe", args, "default");
    }

    private static async Task<int> WaitForPid(string pidFile)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryReadPid(pidFile) is { } pid)
                return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException("The orphaned process never wrote its id.");
    }

    private static int? TryReadPid(string pidFile)
    {
        try
        {
            return int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static MapServerEnvironment CurrentEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        return new MapServerEnvironment(variables);
    }

    private sealed class NoPrompt : IAcpPermissionPrompt
    {
        public Task<string?> AskAsync(AcpPermissionRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
