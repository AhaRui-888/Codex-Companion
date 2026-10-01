using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Owns only the app-server created for a private staging directory, including
// helpers it launches. No running desktop process is ever attached to this job.
internal sealed class ConfigurationServiceLifetime : IDisposable
{
    private IntPtr job;

    internal ConfigurationServiceLifetime()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits();
        limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf(typeof(ExtendedLimits));
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!SetInformationJobObject(job, 9, memory, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal void Attach(Process process)
    {
        if (AssignProcessToJobObject(job, process.Handle)) return;
        int error = Marshal.GetLastWin32Error();
        // Never perform configuration requests without ownership of this child.
        if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); }
        throw new InvalidOperationException("无法隔离 Codex 配置服务；原配置未修改。", new Win32Exception(error));
    }

    public void Dispose()
    {
        if (job == IntPtr.Zero) return;
        CloseHandle(job);
        job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr data, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
