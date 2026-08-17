namespace Accel.Versioning;

using System;

/// <summary>
/// A parseable version type representing Claude Code versions.
/// Supports comparison operations (e.g., version >= new ClaudeVersion(2, 1, 205)).
/// </summary>
public readonly struct ClaudeVersion : IComparable<ClaudeVersion>, IEquatable<ClaudeVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public ClaudeVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>
    /// Parses a version string like "2.1.224 (Claude Code)" into a ClaudeVersion.
    /// </summary>
    /// <param name="input">The version string to parse</param>
    /// <param name="version">The parsed version, or default if parsing failed</param>
    /// <returns>true if parsing succeeded; false if the input is unparseable</returns>
    public static bool TryParse(string? input, out ClaudeVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Extract the leading dotted-integer version part (e.g., "2.1.224" from "2.1.224 (Claude Code)")
        var versionPart = input.Split(' ')[0].Trim();

        var parts = versionPart.Split('.');
        if (parts.Length < 3)
            return false;

        if (!int.TryParse(parts[0], out var major))
            return false;
        if (!int.TryParse(parts[1], out var minor))
            return false;
        if (!int.TryParse(parts[2], out var patch))
            return false;

        version = new ClaudeVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// Compares this version to another version.
    /// </summary>
    public int CompareTo(ClaudeVersion other)
    {
        var majorCmp = Major.CompareTo(other.Major);
        if (majorCmp != 0)
            return majorCmp;

        var minorCmp = Minor.CompareTo(other.Minor);
        if (minorCmp != 0)
            return minorCmp;

        return Patch.CompareTo(other.Patch);
    }

    /// <summary>
    /// Checks equality with another version.
    /// </summary>
    public bool Equals(ClaudeVersion other)
    {
        return Major == other.Major && Minor == other.Minor && Patch == other.Patch;
    }

    /// <summary>
    /// Checks equality with an object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return obj is ClaudeVersion other && Equals(other);
    }

    /// <summary>
    /// Gets the hash code for this version.
    /// </summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor, Patch);
    }

    /// <summary>
    /// Returns a string representation of this version.
    /// </summary>
    public override string ToString()
    {
        return $"{Major}.{Minor}.{Patch}";
    }

    // Comparison operators
    public static bool operator ==(ClaudeVersion left, ClaudeVersion right) => left.Equals(right);
    public static bool operator !=(ClaudeVersion left, ClaudeVersion right) => !left.Equals(right);
    public static bool operator <(ClaudeVersion left, ClaudeVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(ClaudeVersion left, ClaudeVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(ClaudeVersion left, ClaudeVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(ClaudeVersion left, ClaudeVersion right) => left.CompareTo(right) >= 0;
}
