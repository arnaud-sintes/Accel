namespace Accel.Orchestration;

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// What to launch inside a pseudoconsole. Deliberately a plain record-style options object so the
/// launch decision (which exe, which cwd, which initial size) stays with the caller and this file
/// stays a pure interop wrapper.
/// </summary>
public sealed class ConPtyLaunchSpec
{
    /// <summary>The command line handed to <c>CreateProcessW</c>'s <c>lpCommandLine</c>. Built by the
    /// caller (P2-T6 builds it from a real argv array); this class does no quoting or shell
    /// interpretation of its own.</summary>
    public required string CommandLine { get; init; }

    /// <summary>Optional <c>lpApplicationName</c>. When null, Windows resolves argv[0] out of
    /// <see cref="CommandLine"/> using the usual search path rules.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>Optional working directory for the child.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Initial pseudoconsole width in character cells.</summary>
    public int Columns { get; init; } = 80;

    /// <summary>Initial pseudoconsole height in character cells.</summary>
    public int Rows { get; init; } = 25;

    /// <summary>Adds <c>CREATE_SUSPENDED</c> to the creation flags so the caller can assign the child
    /// to a Job Object before it runs a single instruction (locked-in decision 7's
    /// spawn-suspended → assign → resume ordering, P2-T3/P2-T7). The caller <b>must</b> then call
    /// <see cref="ConPtySession.ResumeMainThread"/>; a suspended child never produces output and
    /// never exits, so forgetting it looks exactly like a hang.</summary>
    public bool CreateSuspended { get; init; }

    /// <summary>
    /// Optional environment variables to add to / override in / remove from the environment the child
    /// would otherwise inherit. A null value removes the variable. When this is null or empty the child
    /// inherits this process's environment exactly (<c>lpEnvironment = NULL</c>), which is the historical
    /// and default behaviour; only when it is non-empty is an explicit UTF-16 environment block built
    /// (and <c>CREATE_UNICODE_ENVIRONMENT</c> added to the creation flags).
    ///
    /// <para>Added for P2-T3: a terminal child legitimately needs a couple of environment knobs
    /// (<c>TERM</c>, and later per-session Claude Code variables), and there is no other way to set them
    /// without a shell.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }
}

/// <summary>
/// A Win32 failure from the ConPTY/process-creation path, carrying the actual OS error code.
/// Derives from <see cref="Win32Exception"/> so <see cref="Win32Exception.NativeErrorCode"/> and the
/// OS-formatted message come for free; <see cref="Exception.HResult"/> carries the raw HRESULT for the
/// three <c>*PseudoConsole</c> entry points (which report failure by HRESULT, not by
/// <c>SetLastError</c>).
/// </summary>
public sealed class ConPtyException : Win32Exception
{
    private ConPtyException(int nativeErrorCode, string message)
        : base(nativeErrorCode, message)
    {
    }

    /// <summary>The native API that failed, e.g. <c>CreatePipe</c>.</summary>
    public string Operation { get; private init; } = string.Empty;

    /// <summary>For a <c>SetLastError</c>-style API: the value of <c>GetLastError</c> captured
    /// immediately after the failing call.</summary>
    public static ConPtyException FromLastError(string operation, int lastError) =>
        new(lastError, $"{operation} failed. Win32 error {lastError} (0x{lastError:X8}): {new Win32Exception(lastError).Message}")
        {
            Operation = operation,
        };

    /// <summary>For an HRESULT-returning API (<c>CreatePseudoConsole</c>/<c>ResizePseudoConsole</c>).
    /// The HRESULT is authoritative; <paramref name="lastError"/> is captured too (these entry points
    /// are declared <c>SetLastError = true</c>) but may be stale/unrelated, so it is reported as
    /// supplementary information only. When the HRESULT carries FACILITY_WIN32 the embedded Win32 code
    /// is unwrapped into <see cref="Win32Exception.NativeErrorCode"/>.</summary>
    public static ConPtyException FromHResult(string operation, int hr, int lastError)
    {
        // Both operands must be int: `0xFFFF0000` on its own is a uint literal, which would promote the
        // whole comparison to long and never match a negative (high-bit-set) HRESULT.
        var isWin32Facility = (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000);
        var nativeErrorCode = isWin32Facility ? hr & 0xFFFF : lastError;
        var message =
            $"{operation} failed. HRESULT 0x{hr:X8}" +
            (isWin32Facility ? $" (Win32 error {nativeErrorCode}: {new Win32Exception(nativeErrorCode).Message})" : string.Empty) +
            $"; GetLastError at return was {lastError} (supplementary - HRESULT is authoritative).";
        return new ConPtyException(nativeErrorCode, message)
        {
            Operation = operation,
            HResult = hr,
        };
    }
}

/// <summary>
/// Owns one Windows pseudoconsole (HPCON) plus the child process attached to it.
///
/// <para><b>Scope (P2-T2).</b> This is the raw-interop layer and nothing else: it hands out the two
/// anonymous-pipe endpoints as byte-oriented <see cref="FileStream"/>s (plus their
/// <see cref="SafeFileHandle"/>s) and never reads or writes them itself. There is deliberately no
/// pump loop and no text decoding here - framing, UTF-8 decoding with a stateful decoder, and
/// backpressure all belong to <c>PtySession</c> (P2-T3).</para>
///
/// <para><b>Threading.</b> The pipes come from <c>CreatePipe</c>, so they are <i>not</i> opened
/// <c>FILE_FLAG_OVERLAPPED</c>; the streams are therefore created with <c>isAsync: false</c>.
/// <c>ReadAsync</c> on them works but is sync-over-threadpool, so the output side must be pumped by a
/// dedicated background thread (or a long-running task), never by the UI thread. Nothing in this class
/// blocks except <see cref="WaitForExit"/> and <see cref="Dispose"/> (see below).</para>
///
/// <para><b>Dispose can block.</b> <c>ClosePseudoConsole</c> flushes the pseudoconsole and waits for
/// conhost to finish, which can block indefinitely if nobody is draining the output pipe. The output
/// pump must therefore keep reading until EOF <i>while</i> <see cref="Dispose"/> runs, and
/// <see cref="Dispose"/> must not be called from the pump thread itself. Callers on a shutdown path
/// with a hard deadline (<c>AppDomain.ProcessExit</c>) should treat this as a bounded-wait operation.</para>
///
/// <para><b>Dispose does not force-kill.</b> It closes stdin and the pseudoconsole, which is what makes
/// a well-behaved child exit (measured: an interactive <c>cmd.exe</c> that was never told to exit does
/// end this way). It deliberately does not <c>TerminateProcess</c> and does not wait for the child -
/// graceful-then-forced teardown with a timeout belongs to <c>PtyRegistry</c> (P3-T2), which can use
/// <see cref="ProcessHandle"/>/<see cref="WaitForExit"/> before disposing.</para>
///
/// <para><b>Thread safety.</b> Not thread-safe. Concurrent <see cref="Resize"/>/<see cref="Dispose"/>
/// is memory-safe (every OS handle is a <see cref="SafeHandle"/>, so the worst outcome is an
/// <see cref="ObjectDisposedException"/> from the marshaller, never a use-after-free), but callers
/// should serialize their own access.</para>
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly SafeFileHandle _inputWriteHandle;
    private readonly SafeFileHandle _outputReadHandle;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly SafeProcessHandle _processHandle;
    private readonly SafeThreadHandle? _mainThreadHandle;

    private int _disposed;
    private int _columns;
    private int _rows;

    private ConPtySession(
        SafePseudoConsoleHandle pseudoConsole,
        SafeFileHandle inputWriteHandle,
        SafeFileHandle outputReadHandle,
        SafeProcessHandle processHandle,
        SafeThreadHandle? mainThreadHandle,
        int processId,
        int columns,
        int rows)
    {
        _pseudoConsole = pseudoConsole;
        _inputWriteHandle = inputWriteHandle;
        _outputReadHandle = outputReadHandle;
        _processHandle = processHandle;
        _mainThreadHandle = mainThreadHandle;
        ProcessId = processId;
        _columns = columns;
        _rows = rows;

        // bufferSize 1 == no buffering: a terminal must not sit on bytes waiting for a buffer to
        // fill. isAsync stays false because CreatePipe handles are not overlapped (see class docs).
        // The streams wrap handles this session owns; disposing the stream and then the handle is
        // safe either way, since SafeHandle.Dispose is idempotent.
        _inputStream = new FileStream(inputWriteHandle, FileAccess.Write, bufferSize: 1, isAsync: false);
        _outputStream = new FileStream(outputReadHandle, FileAccess.Read, bufferSize: 1, isAsync: false);
    }

    /// <summary>Belt-and-braces only. Correct use is an explicit <see cref="Dispose"/>; this exists so a
    /// dropped session still tears down in the right order. It is safe because <see cref="SafeHandle"/>
    /// derives from <c>CriticalFinalizerObject</c>, whose finalizer is guaranteed to run <i>after</i>
    /// this one - so the handles are still valid here and the ordering below still holds.</summary>
    ~ConPtySession() => DisposeCore();

    /// <summary>PID of the child attached to the pseudoconsole.</summary>
    public int ProcessId { get; }

    /// <summary>Current pseudoconsole width in cells.</summary>
    public int Columns => _columns;

    /// <summary>Current pseudoconsole height in cells.</summary>
    public int Rows => _rows;

    /// <summary>Write-only stream feeding the terminal's stdin. Owned by this session - do not dispose
    /// it separately; closing it early starts the pseudoconsole's shutdown.</summary>
    public FileStream InputStream => _inputStream;

    /// <summary>Read-only stream carrying the terminal's raw output bytes (VT sequences included, no
    /// decoding). Owned by this session. Reads return 0 (EOF) once the pseudoconsole is closed.</summary>
    public FileStream OutputStream => _outputStream;

    /// <summary>The raw write end of the input pipe, for callers that would rather do their own I/O
    /// than use <see cref="InputStream"/>. Owned by this session.</summary>
    public SafeFileHandle InputHandle => _inputWriteHandle;

    /// <summary>The raw read end of the output pipe. Owned by this session.</summary>
    public SafeFileHandle OutputHandle => _outputReadHandle;

    /// <summary>The child's process handle, for Job Object assignment (P2-T7) and exit observation.
    /// Owned by this session.</summary>
    public SafeProcessHandle ProcessHandle => _processHandle;

    /// <summary>
    /// Creates the pipes, the pseudoconsole and the attached child process, in that order. Either
    /// returns a fully constructed session or throws having released every OS resource it allocated -
    /// there is no partially-constructed state to leak (in particular a successful
    /// <c>CreatePseudoConsole</c> followed by a failing <c>CreateProcessW</c> still closes the HPCON
    /// and both pipes, in the documented order).
    /// </summary>
    /// <exception cref="ConPtyException">Any native call failed; carries the OS error code.</exception>
    public static ConPtySession Start(ConPtyLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.CommandLine))
        {
            throw new ArgumentException("CommandLine must be a non-empty command line.", nameof(spec));
        }

        ValidateSize(spec.Columns, spec.Rows);

        // The two handles the pseudoconsole itself reads/writes. They are dup'ed into conhost by
        // CreatePseudoConsole, so our copies are closed immediately afterwards - keeping them open
        // would hold the output pipe alive after the pseudoconsole dies and turn the output pump's
        // EOF into a permanent block.
        SafeFileHandle? ptyInputRead = null;
        SafeFileHandle? ptyOutputWrite = null;

        // The handles we keep: our write end of the input pipe and our read end of the output pipe.
        SafeFileHandle? ourInputWrite = null;
        SafeFileHandle? ourOutputRead = null;

        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;

        try
        {
            if (!Native.CreatePipe(out ptyInputRead, out ourInputWrite, IntPtr.Zero, 0))
            {
                throw ConPtyException.FromLastError("CreatePipe (input)", Marshal.GetLastWin32Error());
            }

            if (!Native.CreatePipe(out ourOutputRead, out ptyOutputWrite, IntPtr.Zero, 0))
            {
                throw ConPtyException.FromLastError("CreatePipe (output)", Marshal.GetLastWin32Error());
            }

            var hr = Native.CreatePseudoConsole(
                ToCoord(spec.Columns, spec.Rows),
                ptyInputRead,
                ptyOutputWrite,
                dwFlags: 0,
                out pseudoConsole);
            var lastError = Marshal.GetLastWin32Error();
            if (hr < 0 || pseudoConsole is null || pseudoConsole.IsInvalid)
            {
                throw ConPtyException.FromHResult("CreatePseudoConsole", hr, lastError);
            }

            ptyInputRead.Dispose();
            ptyInputRead = null;
            ptyOutputWrite.Dispose();
            ptyOutputWrite = null;

            (processHandle, threadHandle, var processId) = LaunchChild(pseudoConsole, spec);

            var session = new ConPtySession(
                pseudoConsole,
                ourInputWrite,
                ourOutputRead,
                processHandle,
                threadHandle,
                processId,
                spec.Columns,
                spec.Rows);

            // Ownership has transferred to the session; stop the catch block below from closing them.
            pseudoConsole = null;
            ourInputWrite = null;
            ourOutputRead = null;
            processHandle = null;
            threadHandle = null;
            return session;
        }
        catch
        {
            // Same order as Dispose: our input write end, then the pseudoconsole, then everything else.
            threadHandle?.Dispose();
            ourInputWrite?.Dispose();
            pseudoConsole?.Dispose();
            ourOutputRead?.Dispose();
            ptyInputRead?.Dispose();
            ptyOutputWrite?.Dispose();
            processHandle?.Dispose();
            throw;
        }
    }

    /// <summary>Resizes the pseudoconsole, which makes conhost re-flow and signal
    /// <c>WINDOW_BUFFER_SIZE_EVENT</c> to the child.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive or out-of-range dimensions.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="ConPtyException">The native call failed.</exception>
    public void Resize(int cols, int rows)
    {
        ValidateSize(cols, rows);
        ThrowIfDisposed();

        var hr = Native.ResizePseudoConsole(_pseudoConsole, ToCoord(cols, rows));
        var lastError = Marshal.GetLastWin32Error();
        if (hr < 0)
        {
            throw ConPtyException.FromHResult("ResizePseudoConsole", hr, lastError);
        }

        _columns = cols;
        _rows = rows;
    }

    /// <summary>Resumes a child launched with <see cref="ConPtyLaunchSpec.CreateSuspended"/>. Only valid
    /// once, and only for a suspended launch.</summary>
    /// <exception cref="InvalidOperationException">The child was not created suspended.</exception>
    public void ResumeMainThread()
    {
        ThrowIfDisposed();
        if (_mainThreadHandle is null)
        {
            throw new InvalidOperationException(
                "ResumeMainThread is only valid when the session was started with ConPtyLaunchSpec.CreateSuspended.");
        }

        var previousSuspendCount = Native.ResumeThread(_mainThreadHandle);
        if (previousSuspendCount == uint.MaxValue)
        {
            throw ConPtyException.FromLastError("ResumeThread", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Blocking wait on the child's process handle. Provided because this session owns the
    /// process handle; callers must not re-open it by PID (PID reuse). Blocks the calling thread -
    /// never call it from the UI thread.</summary>
    /// <returns>true if the child exited within the timeout.</returns>
    public bool WaitForExit(TimeSpan timeout)
    {
        ThrowIfDisposed();

        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Native.INFINITE
            : checked((uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));

        var result = Native.WaitForSingleObject(_processHandle, milliseconds);
        return result switch
        {
            Native.WAIT_OBJECT_0 => true,
            Native.WAIT_TIMEOUT => false,
            _ => throw ConPtyException.FromLastError("WaitForSingleObject", Marshal.GetLastWin32Error()),
        };
    }

    /// <summary>The child's exit code, or null while it is still running.</summary>
    public int? TryGetExitCode()
    {
        ThrowIfDisposed();

        if (!Native.GetExitCodeProcess(_processHandle, out var exitCode))
        {
            throw ConPtyException.FromLastError("GetExitCodeProcess", Marshal.GetLastWin32Error());
        }

        if (exitCode != Native.STILL_ACTIVE)
        {
            return exitCode;
        }

        // STILL_ACTIVE (259) is ambiguous: it is also a perfectly legal exit code, so
        // GetExitCodeProcess alone cannot tell "running" from "exited with 259" - measured, a child
        // run as `cmd.exe /c exit 259` reports exited from WaitForExit while this method reported
        // null forever. The process handle itself is the authoritative liveness signal (it becomes
        // signalled at exit and never un-signals), so disambiguate with a zero-timeout wait.
        return Native.WaitForSingleObject(_processHandle, 0) == Native.WAIT_OBJECT_0 ? exitCode : null;
    }

    /// <summary>Idempotent, non-throwing teardown. See <see cref="DisposeCore"/> for the ordering
    /// rationale.</summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    internal static void ValidateSize(int cols, int rows)
    {
        // COORD is a pair of SHORTs, so anything outside 1..short.MaxValue would silently truncate or
        // produce a negative dimension inside conhost.
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);
    }

    internal static Native.COORD ToCoord(int cols, int rows) => new()
    {
        X = (short)cols,
        Y = (short)rows,
    };

    /// <summary>
    /// The load-bearing close order, run exactly once:
    /// <list type="number">
    /// <item>our write end of the <b>input</b> pipe - the child sees EOF on stdin and gets the chance to
    /// exit on its own before anything is forced;</item>
    /// <item><c>ClosePseudoConsole</c> - closes conhost's dup'ed ends, signals the attached client to
    /// exit and flushes remaining output (this is the call that can block if the output pipe is not
    /// being drained);</item>
    /// <item>the remaining handles - our output read end last, because closing it before step 2 makes
    /// conhost's final writes fail with a broken pipe and can wedge <c>ClosePseudoConsole</c>.</item>
    /// </list>
    /// Reversing 1 and 2 loses the graceful-exit window; reversing 2 and 3 risks the wedge. Never throws
    /// (a throw from a finalizer or a <c>ProcessExit</c> handler would take the process down), and never
    /// re-enters: the interlocked gate means a second call returns immediately, and
    /// <see cref="SafeHandle.Dispose()"/> is itself idempotent.
    /// </summary>
    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The main thread handle is not part of the pty lifecycle - closing it neither resumes nor
        // kills the child - so it goes first, out of the way.
        SafeDispose(_mainThreadHandle);

        // 1. Input side first: EOF on the child's stdin.
        SafeDispose(_inputStream);
        SafeDispose(_inputWriteHandle);

        // 2. Then the pseudoconsole itself.
        SafeDispose(_pseudoConsole);

        // 3. Then the rest. Output read end after ClosePseudoConsole, never before.
        SafeDispose(_outputStream);
        SafeDispose(_outputReadHandle);
        SafeDispose(_processHandle);
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Teardown is best-effort by contract: this runs on finalizer and process-exit paths where
            // a throw is fatal, and every remaining handle still gets its own attempt below.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Builds the <c>STARTUPINFOEX</c> + attribute list carrying the HPCON and calls
    /// <c>CreateProcessW</c>. The attribute list is heap memory plus (once initialized) native state, so
    /// it is deleted and freed in a <c>finally</c> whether or not <c>CreateProcessW</c> succeeded.
    /// </summary>
    private static (SafeProcessHandle Process, SafeThreadHandle? MainThread, int ProcessId) LaunchChild(
        SafePseudoConsoleHandle pseudoConsole,
        ConPtyLaunchSpec spec)
    {
        var attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        var pseudoConsoleRefAdded = false;
        var environmentBlock = IntPtr.Zero;

        try
        {
            // Sizing call: documented to fail with ERROR_INSUFFICIENT_BUFFER while writing the required
            // size, so "returned false" is not an error here - only a different error code is.
            var size = IntPtr.Zero;
            if (!Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size))
            {
                var sizingError = Marshal.GetLastWin32Error();
                if (sizingError != Native.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw ConPtyException.FromLastError("InitializeProcThreadAttributeList (sizing)", sizingError);
                }
            }

            if (size == IntPtr.Zero)
            {
                throw ConPtyException.FromLastError(
                    "InitializeProcThreadAttributeList (sizing returned a zero size)",
                    Marshal.GetLastWin32Error());
            }

            attributeList = Marshal.AllocHGlobal(size);

            if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw ConPtyException.FromLastError("InitializeProcThreadAttributeList", Marshal.GetLastWin32Error());
            }

            attributeListInitialized = true;

            // The attribute value IS the HPCON (it is already a pointer-sized opaque handle), so
            // lpValue is the handle itself and cbSize is sizeof(HPCON). AddRef keeps the SafeHandle
            // from being finalized/closed while its raw value sits in native memory.
            pseudoConsole.DangerousAddRef(ref pseudoConsoleRefAdded);
            if (!Native.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole.DangerousGetHandle(),
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw ConPtyException.FromLastError("UpdateProcThreadAttribute", Marshal.GetLastWin32Error());
            }

            var startupInfo = default(Native.STARTUPINFOEX);
            startupInfo.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            startupInfo.lpAttributeList = attributeList;

            // STARTF_USESTDHANDLES with all three hStd* left at NULL. This looks wrong against the
            // canonical ConPTY samples (which set neither), and it is load-bearing: without it, Windows
            // propagates the PARENT's std handles into the child's process parameters even with
            // bInheritHandles = false, and those win over the pseudoconsole. Measured with a throwaway
            // probe on this machine (see this task's report), launching `cmd.exe /c echo MARKER_XYZ`
            // from a process whose stdout is a redirected pipe/file:
            //   - no STARTF_USESTDHANDLES: MARKER_XYZ appears in the PARENT's stdout; the pty output
            //     pipe receives 16 bytes of conhost init sequences and nothing else. Interactive cmd.exe
            //     additionally saw EOF on the inherited stdin and exited immediately.
            //   - STARTF_USESTDHANDLES + NULL handles: MARKER_XYZ arrives on the pty output pipe, as it
            //     must.
            // NULL rather than INVALID_HANDLE_VALUE (both work, measured): NULL is the "no handle at
            // all" convention, so nothing downstream can mistake it for a real handle. The child ends up
            // with proper ConDrv handles anyway, because console attach at process init hands the
            // pseudoconsole's handles to a client whose std handles are empty - which is exactly the
            // situation this creates.
            startupInfo.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            startupInfo.StartupInfo.hStdInput = IntPtr.Zero;
            startupInfo.StartupInfo.hStdOutput = IntPtr.Zero;
            startupInfo.StartupInfo.hStdError = IntPtr.Zero;

            var creationFlags = Native.EXTENDED_STARTUPINFO_PRESENT;
            if (spec.CreateSuspended)
            {
                creationFlags |= Native.CREATE_SUSPENDED;
            }

            // Environment: NULL (inherit) unless the caller asked for overrides. The block is UTF-16
            // with embedded NULs, so it cannot go through the string marshaller - it is copied into
            // unmanaged memory here and freed in the finally below, whether or not CreateProcessW
            // succeeded.
            var environmentChars = BuildEnvironmentBlock(spec.EnvironmentOverrides);
            if (environmentChars is not null)
            {
                environmentBlock = Marshal.AllocHGlobal(environmentChars.Length * sizeof(char));
                Marshal.Copy(environmentChars, 0, environmentBlock, environmentChars.Length);
                creationFlags |= Native.CREATE_UNICODE_ENVIRONMENT;
            }

            // CreateProcessW may write into lpCommandLine, so it must be a mutable, NUL-terminated
            // buffer - never a marshalled string literal.
            var commandLine = new char[spec.CommandLine.Length + 1];
            spec.CommandLine.CopyTo(0, commandLine, 0, spec.CommandLine.Length);
            commandLine[^1] = '\0';

            // bInheritHandles: false. The child needs no inherited handles at all - the pipes are
            // reached through the pseudoconsole, not through inheritance - so this stays minimal.
            if (!Native.CreateProcessW(
                    spec.ApplicationName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    environmentBlock,
                    spec.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw ConPtyException.FromLastError("CreateProcessW", Marshal.GetLastWin32Error());
            }

            // Wrap both raw handles into SafeHandles immediately - the statements below cannot throw, so
            // there is no window in which either handle is unowned.
            var process = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);
            SafeThreadHandle? mainThread = null;
            if (spec.CreateSuspended)
            {
                mainThread = new SafeThreadHandle(processInformation.hThread);
            }
            else
            {
                // Nothing will ever resume this thread, so do not hold the handle open for the life of
                // the session; the thread object stays alive regardless.
                new SafeThreadHandle(processInformation.hThread).Dispose();
            }

            return (process, mainThread, processInformation.dwProcessId);
        }
        finally
        {
            if (pseudoConsoleRefAdded)
            {
                pseudoConsole.DangerousRelease();
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }

            if (attributeList != IntPtr.Zero)
            {
                // DeleteProcThreadAttributeList is only legal on an initialized list; the allocation is
                // freed either way.
                if (attributeListInitialized)
                {
                    Native.DeleteProcThreadAttributeList(attributeList);
                }

                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    /// <summary>
    /// Builds a <c>CreateProcessW</c> UTF-16 environment block, or null when there is nothing to
    /// override (in which case the caller passes NULL and the child inherits this process's environment).
    ///
    /// <para>Shape, per the <c>lpEnvironment</c> documentation: <c>NAME=VALUE\0NAME=VALUE\0...\0</c> -
    /// i.e. every entry NUL-terminated plus one extra NUL to terminate the block. Names are compared
    /// case-insensitively (Windows environment semantics) and the result is sorted ordinal-ignore-case,
    /// which is the order Windows itself produces; a null override value removes the variable entirely.</para>
    /// </summary>
    internal static char[]? BuildEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides) =>
        BuildEnvironmentBlock(overrides, EnumerateProcessEnvironment());

    /// <summary>Test seam for <see cref="BuildEnvironmentBlock(IReadOnlyDictionary{string, string?}?)"/>
    /// taking an explicit base environment instead of the real process environment.</summary>
    internal static char[]? BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string?>? overrides,
        IEnumerable<KeyValuePair<string, string>> baseEnvironment)
    {
        ArgumentNullException.ThrowIfNull(baseEnvironment);

        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in baseEnvironment)
        {
            if (!string.IsNullOrEmpty(entry.Key))
            {
                merged[entry.Key] = entry.Value ?? string.Empty;
            }
        }

        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
            {
                // A name containing '=' or a NUL cannot be represented in the block at all; silently
                // accepting it would corrupt every entry after it.
                throw new ArgumentException(
                    $"Environment variable name '{name}' is empty or contains '=' or a NUL, which cannot be represented in a Win32 environment block.",
                    nameof(overrides));
            }

            if (value is null)
            {
                merged.Remove(name);
                continue;
            }

            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The value of environment variable '{name}' contains a NUL, which cannot be represented in a Win32 environment block.",
                    nameof(overrides));
            }

            merged[name] = value;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var name in merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append('=').Append(merged[name]).Append('\0');
        }

        // The extra terminator. An entirely empty environment is still a legal block ("\0\0" - a single
        // NUL after zero entries would be ambiguous, so emit one for the empty case too).
        builder.Append('\0');
        if (merged.Count == 0)
        {
            builder.Append('\0');
        }

        var block = new char[builder.Length];
        builder.CopyTo(0, block, 0, builder.Length);
        return block;
    }

    private static IEnumerable<KeyValuePair<string, string>> EnumerateProcessEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                yield return new KeyValuePair<string, string>(key, entry.Value as string ?? string.Empty);
            }
        }
    }

    /// <summary>HPCON owner. <c>ClosePseudoConsole</c> returns void and cannot fail, so
    /// <see cref="ReleaseHandle"/> always reports success. Zero is the invalid value (the API only
    /// writes the out-param on success).</summary>
    internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            Native.ClosePseudoConsole(handle);
            return true;
        }
    }

    /// <summary>Owner for <c>PROCESS_INFORMATION.hThread</c>. .NET has no public equivalent
    /// (<c>SafeProcessHandle</c> exists, <c>SafeThreadHandle</c> is internal to the BCL).</summary>
    internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle()
            : base(ownsHandle: true)
        {
        }

        public SafeThreadHandle(IntPtr existingHandle)
            : base(ownsHandle: true)
        {
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    /// <summary>
    /// The raw interop surface. Every entry point that reports failure through <c>GetLastError</c> is
    /// declared <c>SetLastError = true</c> so the value is captured by the marshaller before any other
    /// managed code can clobber it; the three <c>*PseudoConsole</c> entry points report an HRESULT and
    /// are declared that way too purely so the supplementary <c>GetLastError</c> value in
    /// <see cref="ConPtyException.FromHResult"/> is the one from the failing call.
    /// </summary>
    internal static class Native
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const uint CREATE_SUSPENDED = 0x00000004;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        internal const int STARTF_USESTDHANDLES = 0x00000100;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;
        internal const int STILL_ACTIVE = 259;
        internal const uint INFINITE = 0xFFFFFFFF;
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint WAIT_TIMEOUT = 0x00000102;

        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD
        {
            internal short X;
            internal short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFO
        {
            internal int cb;
            internal IntPtr lpReserved;
            internal IntPtr lpDesktop;
            internal IntPtr lpTitle;
            internal int dwX;
            internal int dwY;
            internal int dwXSize;
            internal int dwYSize;
            internal int dwXCountChars;
            internal int dwYCountChars;
            internal int dwFillAttribute;
            internal int dwFlags;
            internal short wShowWindow;
            internal short cbReserved2;
            internal IntPtr lpReserved2;
            internal IntPtr hStdInput;
            internal IntPtr hStdOutput;
            internal IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEX
        {
            internal STARTUPINFO StartupInfo;
            internal IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            internal IntPtr hProcess;
            internal IntPtr hThread;
            internal int dwProcessId;
            internal int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            IntPtr lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        /// <summary>HRESULT-returning. <paramref name="hInput"/> is the pseudoconsole's <i>read</i> end
        /// (we write the other end); <paramref name="hOutput"/> is its <i>write</i> end (we read the
        /// other end). Both are dup'ed into conhost, so the caller closes its copies afterwards.</summary>
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern int CreatePseudoConsole(
            COORD size,
            SafeFileHandle hInput,
            SafeFileHandle hOutput,
            uint dwFlags,
            out SafePseudoConsoleHandle phPC);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern int ResizePseudoConsole(SafePseudoConsoleHandle hPC, COORD size);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        /// <summary><paramref name="lpSize"/> is a SIZE_T in/out, hence IntPtr rather than int.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string? lpApplicationName,
            [In][Out] char[] lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeThreadHandle hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);
    }
}
