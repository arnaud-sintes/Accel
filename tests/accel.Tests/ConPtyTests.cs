namespace Accel.Tests;

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for the parts of <see cref="ConPtySession"/> that are provable without a real OS
/// process: native struct/constant marshalling (where a wrong number is an AccessViolation at
/// runtime, not a compile error), argument validation, and error-path exception construction.
///
/// <para>The live behaviour - launching a child attached to a real pseudoconsole, reading its output,
/// resizing it, and tearing everything down without leaking handles - is deliberately NOT tested here:
/// it needs real OS resource lifecycle, so it is covered by the hidden <c>pty-smoke-test</c> verb
/// (<see cref="ConPtySmokeTest"/>) instead of by fakes. Keeping real pseudoconsoles out of the xUnit
/// run also keeps the suite fast and non-flaky.</para>
/// </summary>
public class ConPtyTests
{
    // Hard-coded x64 sizes. STARTUPINFO: 4 cb + 4 pad + 3*8 pointers = 32, + 8*4 DWORDs = 64,
    // + 2 + 2 shorts + 4 pad = 72, + 8 lpReserved2 = 80, + 3*8 std handles = 104.
    // STARTUPINFOEX adds one pointer = 112. If either of these ever changes, `StartupInfo.cb` is
    // wrong and CreateProcessW reads past the struct.
    [Fact]
    public void StartupInfoSizesMatchTheX64WindowsLayout()
    {
        Assert.Equal(8, IntPtr.Size); // the numbers below are the 64-bit layout
        Assert.Equal(104, Marshal.SizeOf<ConPtySession.Native.STARTUPINFO>());
        Assert.Equal(112, Marshal.SizeOf<ConPtySession.Native.STARTUPINFOEX>());
    }

    [Fact]
    public void CoordIsFourBytesOfTwoShorts()
    {
        Assert.Equal(4, Marshal.SizeOf<ConPtySession.Native.COORD>());
    }

    [Fact]
    public void ProcessInformationMatchesTheX64WindowsLayout()
    {
        // 2 handles (16) + 2 DWORDs (8) = 24, no trailing padding.
        Assert.Equal(24, Marshal.SizeOf<ConPtySession.Native.PROCESS_INFORMATION>());
    }

    [Fact]
    public void NativeConstantsMatchTheWindowsHeaders()
    {
        Assert.Equal(0x00080000u, ConPtySession.Native.EXTENDED_STARTUPINFO_PRESENT);
        Assert.Equal(0x00000004u, ConPtySession.Native.CREATE_SUSPENDED);
        Assert.Equal(0x00020016, ConPtySession.Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE);
        Assert.Equal(0x00000100, ConPtySession.Native.STARTF_USESTDHANDLES);
        Assert.Equal(122, ConPtySession.Native.ERROR_INSUFFICIENT_BUFFER);
        Assert.Equal(259, ConPtySession.Native.STILL_ACTIVE);
        Assert.Equal(0x00000102u, ConPtySession.Native.WAIT_TIMEOUT);
        Assert.Equal(0u, ConPtySession.Native.WAIT_OBJECT_0);
    }

    [Fact]
    public void ToCoordMapsColumnsToXAndRowsToY()
    {
        var coord = ConPtySession.ToCoord(cols: 120, rows: 40);
        Assert.Equal((short)120, coord.X);
        Assert.Equal((short)40, coord.Y);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(80, 0)]
    [InlineData(-1, 25)]
    [InlineData(80, -1)]
    [InlineData(short.MaxValue + 1, 25)]
    [InlineData(80, short.MaxValue + 1)]
    public void ValidateSizeRejectsDimensionsThatWouldTruncateOrGoNegativeInsideCoord(int cols, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConPtySession.ValidateSize(cols, rows));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(80, 25)]
    [InlineData(short.MaxValue, short.MaxValue)]
    public void ValidateSizeAcceptsTheWholeRepresentableRange(int cols, int rows)
    {
        ConPtySession.ValidateSize(cols, rows);
    }

    [Fact]
    public void StartRejectsAnEmptyCommandLineBeforeTouchingAnyNativeApi()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ConPtySession.Start(new ConPtyLaunchSpec { CommandLine = "   " }));
        Assert.Equal("spec", exception.ParamName);
    }

    [Fact]
    public void StartValidatesTheSizeBeforeAllocatingAnyHandles()
    {
        // Ordering matters: the size check must run before CreatePipe/CreatePseudoConsole, otherwise a
        // bad size would leak the pipes it had already created.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConPtySession.Start(new ConPtyLaunchSpec { CommandLine = "cmd.exe", Columns = 0 }));
    }

    [Fact]
    public void StartRejectsANullSpec()
    {
        Assert.Throws<ArgumentNullException>(() => ConPtySession.Start(null!));
    }

    [Fact]
    public void FromLastErrorCarriesTheOperationAndTheWin32Code()
    {
        var exception = ConPtyException.FromLastError("CreatePipe (input)", 5);

        Assert.Equal("CreatePipe (input)", exception.Operation);
        Assert.Equal(5, exception.NativeErrorCode);
        Assert.Contains("CreatePipe (input)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Win32 error 5", exception.Message, StringComparison.Ordinal);
        // The OS-formatted text for ERROR_ACCESS_DENIED, whatever the machine's language is.
        Assert.Contains(new Win32Exception(5).Message, exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<Win32Exception>(exception);
    }

    [Fact]
    public void FromHResultUnwrapsAFacilityWin32HResultIntoTheWin32Code()
    {
        // 0x80070005 == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)
        var exception = ConPtyException.FromHResult("CreatePseudoConsole", unchecked((int)0x80070005), lastError: 0);

        Assert.Equal("CreatePseudoConsole", exception.Operation);
        Assert.Equal(5, exception.NativeErrorCode);
        Assert.Equal(unchecked((int)0x80070005), exception.HResult);
        Assert.Contains("HRESULT 0x80070005", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Win32 error 5", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromHResultKeepsANonWin32HResultVerbatimAndFallsBackToLastError()
    {
        // 0x80004005 == E_FAIL: no embedded Win32 code, so GetLastError is the only extra signal there is.
        var exception = ConPtyException.FromHResult("ResizePseudoConsole", unchecked((int)0x80004005), lastError: 6);

        Assert.Equal(6, exception.NativeErrorCode);
        Assert.Equal(unchecked((int)0x80004005), exception.HResult);
        Assert.Contains("HRESULT 0x80004005", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32 error 5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GetLastError at return was 6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchSpecDefaultsToAnEightyByTwentyFiveNonSuspendedConsole()
    {
        var spec = new ConPtyLaunchSpec { CommandLine = "cmd.exe" };

        Assert.Equal(80, spec.Columns);
        Assert.Equal(25, spec.Rows);
        Assert.False(spec.CreateSuspended);
        Assert.Null(spec.ApplicationName);
        Assert.Null(spec.WorkingDirectory);
    }
}
