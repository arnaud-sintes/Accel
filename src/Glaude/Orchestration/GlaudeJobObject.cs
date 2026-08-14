namespace Glaude.Orchestration;

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// P2-T7: one Windows Job Object for the whole app (locked-in decision 7). Created at app
/// startup with <see cref="NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"/> set, so that
/// disposing this object (closing the job handle) kills every process still assigned to it —
/// this is the backstop that guarantees no orphaned <c>claude.exe</c> survives Glaude itself
/// being killed (e.g. from Task Manager), independent of the graceful shutdown path.
///
/// Every spawned `claude` process is expected to be assigned via <see cref="AssignProcess"/>
/// immediately after <c>CreateProcess</c> (spawn-suspended → assign → resume), per the ordering
/// called out in the plan — assigning after resume is a race (the child could exit, or do
/// damage, before the kill-on-close guarantee applies to it).
///
/// Handle-ownership discipline: the one OS handle this class owns (the job handle) lives in a
/// single <see cref="SafeHandle"/> (<see cref="JobObjectSafeHandle"/>); every Win32 call checks
/// its return value and reports <see cref="Marshal.GetLastWin32Error"/> on failure via
/// <see cref="Win32Exception"/>; <see cref="Dispose"/> is idempotent.
/// </summary>
public sealed class GlaudeJobObject : IDisposable
{
    private readonly JobObjectSafeHandle handle;
    private bool disposed;

    private GlaudeJobObject(JobObjectSafeHandle handle)
    {
        this.handle = handle;
    }

    /// <summary>
    /// Creates an unnamed job object and configures it with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so that closing the job (via <see cref="Dispose"/>)
    /// terminates every process still assigned to it.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// Thrown if <c>CreateJobObject</c> or <c>SetInformationJobObject</c> fails.
    /// </exception>
    public static GlaudeJobObject Create()
    {
        var rawHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        var safeHandle = new JobObjectSafeHandle(rawHandle, ownsHandle: true);
        if (safeHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            safeHandle.Dispose();
            throw new Win32Exception(error, "CreateJobObject failed.");
        }

        var limitInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limitInfo, buffer, fDeleteOld: false);

            bool ok = NativeMethods.SetInformationJobObject(
                safeHandle,
                NativeMethods.JobObjectInfoClass.JobObjectExtendedLimitInformation,
                buffer,
                (uint)size);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                safeHandle.Dispose();
                throw new Win32Exception(error, "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new GlaudeJobObject(safeHandle);
    }

    /// <summary>
    /// Assigns a process (by its open <see cref="IntPtr"/> process handle, e.g. from
    /// <c>CreateProcess</c>'s <c>PROCESS_INFORMATION.hProcess</c>) to this job object. Callers
    /// remain the owner of <paramref name="processHandle"/>; this call does not take ownership
    /// of it or close it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The job object has already been disposed.</exception>
    /// <exception cref="Win32Exception">Thrown if <c>AssignProcessToJobObject</c> fails.</exception>
    public void AssignProcess(IntPtr processHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        bool ok = NativeMethods.AssignProcessToJobObject(handle, processHandle);
        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>
    /// Closes the job handle. Per Windows semantics, because the job was created with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, this kills every process still assigned to the
    /// job (unless already removed/exited). Idempotent — safe to call more than once, including
    /// from a finalizer/<c>ProcessExit</c> path.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        handle.Dispose();
    }
}

/// <summary>One <see cref="SafeHandleZeroOrMinusOneIsInvalid"/> per Job Object OS handle.</summary>
internal sealed class JobObjectSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public JobObjectSafeHandle(IntPtr preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal static class NativeMethods
{
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public enum JobObjectInfoClass
    {
        JobObjectExtendedLimitInformation = 9,
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(
        JobObjectSafeHandle hJob,
        JobObjectInfoClass jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(JobObjectSafeHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
