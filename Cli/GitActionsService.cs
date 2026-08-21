namespace Accel.Cli;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>The result of a mutating git operation — mirrors
/// <see cref="Accel.Orchestration.FileSystemEntryExecutor"/>'s "every failure is a named outcome,
/// not an exception" convention.</summary>
public enum GitActionOutcome
{
    Success,
    NotARepository,
    NothingToDo,
    Conflict,
    AuthenticationFailed,
    Rejected,
    CommandFailed,
    TimedOut,
}

/// <summary>Outcome plus, for a failure, the message worth showing the user (raw, trimmed stderr
/// unless a more specific classification applies).</summary>
public sealed record GitActionResult(GitActionOutcome Outcome, string? ErrorMessage)
{
    public static readonly GitActionResult Ok = new(GitActionOutcome.Success, null);

    public static GitActionResult Failed(GitActionOutcome outcome, string? message) => new(outcome, message);
}

/// <summary>
/// Pure, WPF-free async API for mutating git operations — the mutation counterpart to
/// <see cref="GitStatusBuilder"/> (which is entirely read-only). Every method shells out to
/// <c>git</c> via <see cref="GitCommandRunner"/> and returns a <see cref="GitActionResult"/>;
/// none of them throw. Callers (namely <c>GitPanelViewModel</c>) own all confirmation/dialog UX —
/// this class only executes what it's asked to.
/// </summary>
public static class GitActionsService
{
    public static Task<GitActionResult> StageAsync(string repoRootPath, string relativePath, CancellationToken ct = default) =>
        RunSimple(repoRootPath, new[] { "add", "--", relativePath }, ct);

    /// <summary>Uses `git reset -- &lt;path&gt;` rather than the newer `git restore --staged`: the
    /// latter needs to resolve HEAD as its restore source and fails outright
    /// (<c>fatal: could not resolve 'HEAD'</c>) in a brand-new repository that has no commits yet -
    /// a state a staged-but-uncommitted file is squarely in. `reset` degrades gracefully to "unstage
    /// against an empty tree" in that case.</summary>
    public static Task<GitActionResult> UnstageAsync(string repoRootPath, string relativePath, CancellationToken ct = default) =>
        RunSimple(repoRootPath, new[] { "reset", "--", relativePath }, ct);

    public static Task<GitActionResult> StageAllAsync(string repoRootPath, CancellationToken ct = default) =>
        RunSimple(repoRootPath, new[] { "add", "-A" }, ct);

    /// <summary>Reverts a single path back to HEAD, regardless of whether it was staged, unstaged,
    /// or untracked. Untracked files have no HEAD content to restore, so they're removed instead
    /// (<c>git clean</c>); a staged-and-modified file is restored in a single
    /// <c>git restore --staged --worktree</c> call rather than two separate steps, so a failure
    /// partway through can't leave the file half-unstaged.</summary>
    public static async Task<GitActionResult> DiscardAsync(
        string repoRootPath,
        string relativePath,
        bool isStaged,
        bool isUntracked,
        CancellationToken ct = default)
    {
        if (isUntracked)
        {
            return await RunSimple(repoRootPath, new[] { "clean", "-f", "--", relativePath }, ct).ConfigureAwait(false);
        }

        if (!isStaged)
        {
            return await RunSimple(repoRootPath, new[] { "restore", "--", relativePath }, ct).ConfigureAwait(false);
        }

        var result = await RunSimple(repoRootPath, new[] { "restore", "--staged", "--worktree", "--", relativePath }, ct).ConfigureAwait(false);
        if (result.Outcome != GitActionOutcome.CommandFailed
            || result.ErrorMessage is null
            || !result.ErrorMessage.Contains("could not resolve 'HEAD'", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        // A staged-but-never-committed file: there's no HEAD content to restore the worktree to, so
        // discarding it means removing it entirely, same as an untracked file.
        await RunSimple(repoRootPath, new[] { "reset", "--", relativePath }, ct).ConfigureAwait(false);
        return await RunSimple(repoRootPath, new[] { "clean", "-f", "--", relativePath }, ct).ConfigureAwait(false);
    }

    /// <summary>Commits currently staged changes. The message is piped via stdin
    /// (<c>git commit -F -</c>) rather than passed as a <c>-m</c> argument, avoiding any
    /// quoting/escaping concerns for messages containing quotes or newlines.</summary>
    public static async Task<GitActionResult> CommitAsync(string repoRootPath, string message, CancellationToken ct = default)
    {
        var outcome = await GitCommandRunner.RunAsync(
            repoRootPath,
            new[] { "commit", "-F", "-" },
            GitCommandRunner.LocalOperationTimeout,
            stdin: message,
            ct).ConfigureAwait(false);

        return Classify(outcome);
    }

    public static async Task<GitActionResult> PushAsync(string repoRootPath, CancellationToken ct = default)
    {
        var outcome = await GitCommandRunner.RunAsync(
            repoRootPath,
            new[] { "push" },
            GitCommandRunner.NetworkOperationTimeout,
            stdin: null,
            ct).ConfigureAwait(false);

        return Classify(outcome);
    }

    public static async Task<GitActionResult> PullAsync(string repoRootPath, CancellationToken ct = default)
    {
        var outcome = await GitCommandRunner.RunAsync(
            repoRootPath,
            new[] { "pull" },
            GitCommandRunner.NetworkOperationTimeout,
            stdin: null,
            ct).ConfigureAwait(false);

        return Classify(outcome);
    }

    /// <summary>Local branch names (short form), or <see langword="null"/> if the listing failed
    /// (not a repo, git missing, timeout).</summary>
    public static async Task<string[]?> ListLocalBranchesAsync(string repoRootPath, CancellationToken ct = default)
    {
        var outcome = await GitCommandRunner.RunAsync(
            repoRootPath,
            new[] { "for-each-ref", "--format=%(refname:short)", "refs/heads/" },
            GitCommandRunner.LocalOperationTimeout,
            stdin: null,
            ct).ConfigureAwait(false);

        return outcome.Succeeded ? GitBranchListParser.Parse(outcome.StandardOutput) : null;
    }

    public static Task<GitActionResult> CheckoutBranchAsync(string repoRootPath, string branchName, CancellationToken ct = default) =>
        RunSimple(repoRootPath, new[] { "checkout", branchName }, ct);

    /// <summary>
    /// Marks a conflicted path resolved exactly as git itself defines it: `git add` collapses the
    /// three unmerged index stages back down to one. Deliberately the same command
    /// <see cref="StageAsync"/> runs — the distinction is entirely in the caller's UX (a separate
    /// menu item, only offered once the user has actually edited the file), which is why panel B
    /// hides plain "Stage" on a conflicted row: staging a file whose conflict markers are still in it
    /// tells git the conflict is settled when it isn't.
    /// </summary>
    public static Task<GitActionResult> MarkResolvedAsync(string repoRootPath, string relativePath, CancellationToken ct = default) =>
        RunSimple(repoRootPath, new[] { "add", "--", relativePath }, ct);

    /// <summary>
    /// Resolves a conflicted path wholesale in favour of one side: checks out that stage over the
    /// working-tree file and immediately marks it resolved, so the row leaves the conflict list in
    /// one action rather than sitting in a half-resolved state the panel would have to explain.
    /// <paramref name="ours"/> selects stage 2 (`--ours`) versus stage 3 (`--theirs`).
    /// </summary>
    /// <remarks>Fails with git's own message for the pairs where the requested side has no version of
    /// the file at all (a "deleted by us"/"deleted by them" conflict — `git checkout` reports
    /// <c>does not have our version</c>), rather than silently doing something else: those cases are a
    /// choice between keeping and removing the file, not between two contents, and belong to a
    /// deliberate user action, not to this shortcut.</remarks>
    public static async Task<GitActionResult> AcceptConflictSideAsync(
        string repoRootPath,
        string relativePath,
        bool ours,
        CancellationToken ct = default)
    {
        string sideFlag = ours ? "--ours" : "--theirs";
        var checkout = await RunSimple(repoRootPath, new[] { "checkout", sideFlag, "--", relativePath }, ct).ConfigureAwait(false);
        if (checkout.Outcome != GitActionOutcome.Success)
        {
            return checkout;
        }

        return await MarkResolvedAsync(repoRootPath, relativePath, ct).ConfigureAwait(false);
    }

    /// <summary>Abandons the in-progress operation and returns the working tree to where it was
    /// before it started (`&lt;operation&gt; --abort`). <see cref="GitInProgressOperation.None"/> is
    /// <see cref="GitActionOutcome.NothingToDo"/> rather than an error — the panel's Abort button can
    /// race a refresh that already saw the operation finish.</summary>
    public static Task<GitActionResult> AbortOperationAsync(
        string repoRootPath,
        GitInProgressOperation operation,
        CancellationToken ct = default)
    {
        string? verb = OperationVerb(operation);
        return verb is null
            ? Task.FromResult(GitActionResult.Failed(GitActionOutcome.NothingToDo, "No merge, rebase, cherry-pick or revert is in progress."))
            : RunSimple(repoRootPath, new[] { verb, "--abort" }, ct);
    }

    /// <summary>
    /// Carries the in-progress operation forward now that its conflicts are resolved.
    /// </summary>
    /// <remarks>
    /// <para>A merge is completed by committing, not by a <c>--continue</c> subcommand in the sense
    /// the other three have one: `git merge --continue` is a thin alias for `git commit` that still
    /// insists on an editor. This runs `commit --no-edit` directly, which takes the merge message git
    /// already prepared in <c>MERGE_MSG</c> — the same commit `--continue` would make, minus the
    /// editor this app has no terminal to host.</para>
    /// <para>The other three get <c>-c core.editor=true</c> for the same reason: `rebase --continue`
    /// wants to open the commit message for editing, and a git that cannot launch an editor here
    /// would hang until <see cref="GitCommandRunner.LocalOperationTimeout"/> killed it, leaving the
    /// rebase stopped. <c>true</c> as the editor exits 0 immediately, accepting the message as-is.</para>
    /// </remarks>
    public static Task<GitActionResult> ContinueOperationAsync(
        string repoRootPath,
        GitInProgressOperation operation,
        CancellationToken ct = default)
    {
        if (operation == GitInProgressOperation.Merge)
        {
            return RunSimple(repoRootPath, new[] { "commit", "--no-edit" }, ct);
        }

        string? verb = OperationVerb(operation);
        return verb is null
            ? Task.FromResult(GitActionResult.Failed(GitActionOutcome.NothingToDo, "No merge, rebase, cherry-pick or revert is in progress."))
            : RunSimple(repoRootPath, new[] { "-c", "core.editor=true", verb, "--continue" }, ct);
    }

    /// <summary>The git subcommand name for an in-progress operation, or <see langword="null"/> for
    /// <see cref="GitInProgressOperation.None"/>.</summary>
    private static string? OperationVerb(GitInProgressOperation operation) => operation switch
    {
        GitInProgressOperation.Merge => "merge",
        GitInProgressOperation.Rebase => "rebase",
        GitInProgressOperation.CherryPick => "cherry-pick",
        GitInProgressOperation.Revert => "revert",
        _ => null,
    };

    /// <summary>Whether the working tree has any uncommitted changes (staged, unstaged, or
    /// untracked) — reuses <see cref="GitStatusBuilder.Build"/> rather than adding a second status
    /// call, since that method already answers exactly this question.</summary>
    public static bool HasUncommittedChanges(string repoRootPath) =>
        GitStatusBuilder.Build(repoRootPath) is { Length: > 0 };

    private static async Task<GitActionResult> RunSimple(string repoRootPath, string[] arguments, CancellationToken ct)
    {
        var outcome = await GitCommandRunner.RunAsync(
            repoRootPath,
            arguments,
            GitCommandRunner.LocalOperationTimeout,
            stdin: null,
            ct).ConfigureAwait(false);

        return Classify(outcome);
    }

    private static GitActionResult Classify(GitCommandOutcome outcome)
    {
        if (!outcome.Started)
        {
            return GitActionResult.Failed(GitActionOutcome.NotARepository, "git could not be started.");
        }

        if (outcome.TimedOut)
        {
            return GitActionResult.Failed(GitActionOutcome.TimedOut, "The git command timed out.");
        }

        if (outcome.Succeeded)
        {
            return GitActionResult.Ok;
        }

        string stderr = outcome.StandardError;
        string stdout = outcome.StandardOutput;

        if (stdout.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return GitActionResult.Failed(GitActionOutcome.NothingToDo, stdout.Trim());
        }

        if (stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("fatal: Authentication", StringComparison.OrdinalIgnoreCase))
        {
            return GitActionResult.Failed(GitActionOutcome.AuthenticationFailed, stderr.Trim());
        }

        if (stderr.Contains("[rejected]", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase))
        {
            return GitActionResult.Failed(GitActionOutcome.Rejected, stderr.Trim());
        }

        if (stderr.Contains("CONFLICT", StringComparison.Ordinal))
        {
            return GitActionResult.Failed(GitActionOutcome.Conflict, stderr.Trim());
        }

        if (stderr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return GitActionResult.Failed(GitActionOutcome.NothingToDo, stderr.Trim());
        }

        string message = string.IsNullOrWhiteSpace(stderr) ? outcome.StandardOutput.Trim() : stderr.Trim();
        return GitActionResult.Failed(GitActionOutcome.CommandFailed, message);
    }
}
