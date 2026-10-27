using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zdev.Agents.Acp;

/// <summary>
/// Every process an agent runs as: the one started, and all it starts in turn. An adapter is usually
/// reached through a wrapper such as npx, which starts the real adapter and may exit before it, and
/// the adapter starts the agent's own CLI and tools. Ending the tree ends all of them, whichever have
/// exited along the way.
/// </summary>
internal abstract partial class AgentProcessTree : IDisposable
{
    /// <summary>Takes <paramref name="process"/>, just started, and what it goes on to start. On Windows
    /// the processes are held in a job object, which also ends them should this process be killed;
    /// elsewhere, or where the job cannot be made, they are found through their parents.</summary>
    public static AgentProcessTree Adopt(Process process)
    {
        if (OperatingSystem.IsWindows() && JobTree.TryAdopt(process) is { } job)
            return job;
        return new DescendantTree(process);
    }

    /// <summary>Ends every process of the tree that is still running.</summary>
    public abstract void End();

    public abstract void Dispose();

    /// <summary>
    /// The processes held in a job object made to end them all when it is closed. A process joins the
    /// job as soon as it is started, and what it starts joins too, so none is lost when its parent
    /// exits. One started in the moment before the first process joins would escape, but the first
    /// starts nothing that soon: a wrapper is still loading when it joins.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class JobTree : AgentProcessTree
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private const uint EndedByZultree = 1;

        private readonly IntPtr _job;
        private int _closed;

        private JobTree(IntPtr job) => _job = job;

        public static JobTree? TryAdopt(Process process)
        {
            var job = Native.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (job == IntPtr.Zero)
                return null;
            if (!EndsOnClose(job) || !Native.AssignProcessToJobObject(job, process.Handle))
            {
                Native.CloseHandle(job);
                return null;
            }
            return new JobTree(job);
        }

        private static unsafe bool EndsOnClose(IntPtr job)
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = KillOnJobClose;
            return Native.SetInformationJobObject(job, ExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimits));
        }

        public override void End() => Native.TerminateJobObject(_job, EndedByZultree);

        /// <summary>Closes the job, which ends what is still in it.</summary>
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;
            Native.CloseHandle(_job);
        }
    }

    /// <summary>The processes found through their parents when the tree is ended. A process whose
    /// parent has exited can no longer be found that way, so without a job object one may survive.</summary>
    private sealed class DescendantTree(Process process) : AgentProcessTree
    {
        // Whether the first process has exited or not: its descendants are found through it either way.
        public override void End()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        public override void Dispose()
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe partial class Native
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(IntPtr job, int infoClass, void* info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TerminateJobObject(IntPtr job, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr handle);
    }
}
