namespace Accel.App.Services;

using System.Text;

/// <summary>
/// Tokenizes the "Create session" dialog's free-text extra-CLI-args field (P2-T6) into a real argv
/// array: whitespace-separated tokens, with a pair of double-quotes grouping one token that itself
/// contains whitespace (e.g. <c>--name "hello world"</c> -&gt; <c>["--name", "hello world"]</c>), and
/// <c>""</c> inside a quoted token escaping a literal quote.
///
/// <para><b>Why this exists at all (the hard security requirement, plan P2-T6).</b> The dialog's
/// extra-args field is explicitly documented as untrusted-shaped, trusted-user input (see
/// <c>CreateSessionDialogViewModel.AdvancedArgsWarning</c>) - a user may type
/// <c>--permission-mode bypassPermissions</c>, meaning two separate argv elements. Turning that text
/// into <see cref="Accel.Orchestration.PtyLaunchSpec.Arguments"/> requires *some* tokenization, and
/// the naive version - <c>text.Split(' ')</c> - would silently re-split any token the user deliberately
/// wanted to keep as one (a path or display name containing a space), which is exactly the class of
/// quoting bug the array-not-a-string requirement exists to prevent. This class is the one place that
/// tokenization happens, and it happens once, in-process, before the result becomes real array
/// elements - never a re-joined string, never <c>cmd /c</c>, no globbing/expansion/operators.</para>
/// </summary>
public static class ExtraArgsParser
{
    /// <summary>
    /// Parses <paramref name="text"/> into argv tokens. Null/empty/whitespace-only input yields an
    /// empty array. Never throws - an unterminated quote is simply treated as running to the end of
    /// the string, since this is a forgiving text-box tokenizer, not a strict grammar.
    /// </summary>
    public static string[] Parse(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var hasCurrent = false;

        void FlushCurrent()
        {
            if (hasCurrent)
            {
                tokens.Add(current.ToString());
                current.Clear();
                hasCurrent = false;
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                // "" inside a quoted run is a literal quote - the one escape this tokenizer
                // supports, so a value can itself contain a `"`.
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    hasCurrent = true;
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                hasCurrent = true; // a bare "" must still produce a (possibly empty) token
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                FlushCurrent();
                continue;
            }

            current.Append(c);
            hasCurrent = true;
        }

        FlushCurrent();
        return tokens.ToArray();
    }
}
