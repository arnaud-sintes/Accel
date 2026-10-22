namespace Accel.App.Services;

using System;
using System.Collections.Generic;

/// <summary>
/// Path comparisons for "which project does this folder/file belong to", shared by panel A (selecting
/// the root that hosts a file) and panel C (placing a new terminal next to its project's tabs). Pure
/// string work - no disk access, so a root that has since been deleted still matches - and tolerant of
/// the mixed separators and trailing slashes these paths arrive with (a root typed with <c>/</c>, a
/// <c>cwd</c> recorded with <c>\</c>).
/// </summary>
public static class ProjectPaths
{
    /// <summary><paramref name="path"/> with a single separator style and no trailing separator, or
    /// <see langword="null"/> for a null/blank path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Trim().Replace('/', '\\');
        // Keep a bare drive root ("C:\") intact; anything longer loses its trailing separator.
        while (normalized.Length > 3 && normalized.EndsWith('\\'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    /// <summary>Whether both paths name the same location (case-insensitive, separator-insensitive).</summary>
    public static bool AreSame(string? a, string? b)
    {
        string? left = Normalize(a);
        string? right = Normalize(b);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> itself or lies below it. The
    /// match is on whole path segments: <c>C:\app-old</c> is not under <c>C:\app</c>.</summary>
    public static bool IsSameOrUnder(string? path, string? root)
    {
        string? p = Normalize(path);
        string? r = Normalize(root);
        if (p is null || r is null)
        {
            return false;
        }

        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The entry of <paramref name="roots"/> that <paramref name="path"/> lies in - the longest one, so a
    /// nested root wins over its parent - or <see langword="null"/> if none contains it.
    /// </summary>
    public static string? LongestContainingRoot(string? path, IEnumerable<string> roots)
    {
        string? best = null;
        int bestLength = -1;
        foreach (string root in roots)
        {
            if (!IsSameOrUnder(path, root))
            {
                continue;
            }

            int length = Normalize(root)!.Length;
            if (length > bestLength)
            {
                best = root;
                bestLength = length;
            }
        }

        return best;
    }
}
