namespace Accel.Versioning;

/// <summary>
/// Detects whether curl.exe is available in System32.
/// Never throws; returns false on any failure.
/// </summary>
public static class CurlProbe
{
    /// <summary>
    /// Checks if curl.exe exists at the standard location (System32\curl.exe).
    /// </summary>
    /// <returns>true if curl.exe is found and accessible; false otherwise</returns>
    public static bool IsAvailable()
    {
        try
        {
            var curlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "curl.exe"
            );
            return File.Exists(curlPath);
        }
        catch
        {
            return false;
        }
    }
}
