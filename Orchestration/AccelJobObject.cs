namespace Accel.Orchestration;

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// P2-T7: one Windows Job Object for the whole app (locked-in decision 7). Created at app
/// startup with <see cref="NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"/> set, so that
/// disposing this object (closing the job handle) kills every process still assigned to it —
/// this is the backstop that guarantees no orphaned <c>claude.exe</c> survives Accel itself
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
public sealed class AccelJobObject : IDisposable
{
    /// <summary>
    /// The one process-wide job object, created on first use and <b>never disposed</b>.
    ///
    /// <para><b>Why a static (rooting requirement, P2-T2b finding 1).</b> The job handle lives in a
    /// <see cref="SafeHandle"/>, and <see cref="SafeHandle"/> has a critical finalizer that closes the
    /// handle. Because the job carries <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, "handle closed" means
    /// "every assigned process is terminated". So a <see cref="AccelJobObject"/> that becomes
    /// unreachable while sessions are live does not merely stop protecting them - it silently kills
    /// every <c>claude.exe</c> in it at the next GC. Holding the instance in a <c>static readonly</c>
    /// <see cref="Lazy{T}"/> field makes it a GC root for the entire life of the AppDomain, which is
    /// exactly the lifetime the plan's locked-in decision 7 asks for ("one Windows Job Object for the
    /// whole app").</para>
    ///
    /// <para><b>Do not dispose this.</b> There is no correct moment to: while the app runs, disposing
    /// kills every session; at process exit the OS closes the handle anyway, which is precisely the
    /// kill-on-close backstop that stops orphaned children surviving a Task-Manager kill of Accel.
    /// <see cref="PtySession"/> additionally keeps its own reference to whichever
    /// <see cref="AccelJobObject"/> it was assigned to for the whole life of the session, so even a
    /// caller-supplied (non-static, e.g. test-owned) job cannot be collected out from under a live
    /// session.</para>
    /// </summary>
    public static AccelJobObject Shared => SharedLazy.Value;

    private static readonly Lazy<AccelJobObject> SharedLazy =
        new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly JobObjectSafeHandle handle;
    private bool disposed;

    private AccelJobObject(JobObjectSafeHandle handle)
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
    public static AccelJobObject Create()
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

        return new AccelJobObject(safeHandle);
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
    /// Preferred overload: assigns a process to this job object given the <see cref="SafeProcessHandle"/>
    /// that owns its handle (e.g. <see cref="ConPtySession.ProcessHandle"/>).
    ///
    /// <para><b>Why this exists (P2-T2b finding 2).</b> The <see cref="IntPtr"/> overload forces callers
    /// to reach for <see cref="SafeHandle.DangerousGetHandle"/>, which hands the raw handle value to
    /// native code with no reference held: if the owning <see cref="SafeHandle"/> is closed or finalized
    /// between <c>DangerousGetHandle</c> and the P/Invoke returning, the value is stale and
    /// <c>AssignProcessToJobObject</c> is called on a closed - possibly recycled - handle. This overload
    /// does the <see cref="SafeHandle.DangerousAddRef"/>/<see cref="SafeHandle.DangerousRelease"/> pair
    /// around the call so the handle provably cannot be released for its duration, and releases the ref
    /// in a <c>finally</c> so a throwing P/Invoke cannot leak it.</para>
    ///
    /// <para>Ownership is unchanged: the caller still owns <paramref name="processHandle"/>.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="processHandle"/> is null.</exception>
    /// <exception cref="ArgumentException">The handle is invalid (closed or never set).</exception>
    /// <exception cref="ObjectDisposedException">The job object has already been disposed.</exception>
    /// <exception cref="Win32Exception">Thrown if <c>AssignProcessToJobObject</c> fails.</exception>
    public void AssignProcess(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (processHandle.IsInvalid || processHandle.IsClosed)
        {
            throw new ArgumentException(
                "The process handle is invalid or already closed; it cannot be assigned to a job object.",
                nameof(processHandle));
        }

        bool refAdded = false;
        try
        {
            // Throws ObjectDisposedException if the handle was closed between the check above and here,
            // which is the correct outcome - better a managed exception than a P/Invoke on a stale value.
            processHandle.DangerousAddRef(ref refAdded);
            AssignProcess(processHandle.DangerousGetHandle());
        }
        finally
        {
            if (refAdded)
            {
                processHandle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Diagnostic: whether <paramref name="processHandle"/> is currently assigned to <i>this</i> job
    /// object. Used by the <c>pty-session-smoke-test</c> verb to positively confirm that the
    /// spawn-suspended → assign → resume ordering really put the child in the job (as opposed to just
    /// "the API returned success"), and usable by <c>accel doctor</c> later (CX-T3).
    /// </summary>
    /// <remarks>
    /// <c>IsProcessInJob</c> with a non-null job handle answers "is it in this specific job", which is
    /// what we want; passing NULL would answer the weaker "is it in any job at all".
    /// </remarks>
    public bool ContainsProcess(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ObjectDisposedException.ThrowIf(disposed, this);

        bool refAdded = false;
        try
        {
            processHandle.DangerousAddRef(ref refAdded);
            if (!NativeMethods.IsProcessInJob(processHandle.DangerousGetHandle(), handle, out bool result))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "IsProcessInJob failed.");
            }

            return result;
        }
        finally
        {
            if (refAdded)
            {
                processHandle.DangerousRelease();
            }
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
    public static extern bool IsProcessInJob(
        IntPtr processHandle,
        JobObjectSafeHandle jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
