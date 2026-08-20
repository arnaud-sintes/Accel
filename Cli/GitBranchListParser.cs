namespace Accel.Cli;

using System;
using System.Collections.Generic;

/// <summary>
/// Pure parser for <c>git for-each-ref --format=%(refname:short) refs/heads/</c> output — kept as
/// its own small class rather than folded into <see cref="GitStatusBuilder"/>, since branch
/// listing is a mutation-adjacent read (it only exists to populate the branch-switcher) rather
/// than part of the read-only status-summary path <see cref="GitStatusBuilder"/> owns.
/// </summary>
public static class GitBranchListParser
{
    public static string[] Parse(string forEachRefOutput)
    {
        if (string.IsNullOrEmpty(forEachRefOutput))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();

        foreach (string rawLine in forEachRefOutput.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length > 0)
            {
                result.Add(line);
            }
        }

        return result.ToArray();
    }
}
