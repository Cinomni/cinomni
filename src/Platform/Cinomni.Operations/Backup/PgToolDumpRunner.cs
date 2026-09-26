using System.Diagnostics;
using Cinomni.Kernel.Results;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Production <see cref="IDatabaseDumpRunner"/>: launches <c>pg_dump</c> and <c>pg_restore</c> as
/// isolated child processes, hardened the same way the ffprobe adapter is — an argv list built by
/// <see cref="PgToolCommand"/>, <c>UseShellExecute=false</c>, both pipes drained concurrently so a
/// chatty tool cannot deadlock on a full buffer, and a timeout that kills the whole process tree.
/// <para>
/// The connection target — including the password — is parsed once here and never leaves this object.
/// Standard error is captured for the failure message but truncated, because a libpq failure can echo
/// the connection parameters it tried.
/// </para>
/// </summary>
public sealed class PgToolDumpRunner : IDatabaseDumpRunner
{
    /// <summary>How much of a tool's standard error survives into a result message.</summary>
    private const int MaxErrorLength = 400;

    private readonly PgConnectionTarget _target;
    private readonly BackupOptions _options;
    private readonly ILogger<PgToolDumpRunner> _logger;

    public PgToolDumpRunner(string connectionString, BackupOptions options, ILogger<PgToolDumpRunner> logger)
    {
        _target = PgToolCommand.TargetFrom(connectionString);
        _options = options;
        _logger = logger;
    }

    /// <summary>The database the dump reads, for the manifest. Never the credentials that reach it.</summary>
    public string DatabaseName => _target.Database;

    public Task<Result> DumpAsync(string targetFile, CancellationToken cancellationToken = default) =>
        RunAsync(
            PgToolCommand.Dump(_options.PgDumpPath, _target, targetFile),
            _options.PgDumpPath,
            "backup.dump_failed",
            cancellationToken);

    public Task<Result> RestoreAsync(
        string sourceFile,
        string databaseName,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            PgToolCommand.Restore(_options.PgRestorePath, _target, databaseName, sourceFile),
            _options.PgRestorePath,
            "backup.restore_failed",
            cancellationToken);

    public async Task<Result<string>> ProbeVersionAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await ExecuteAsync(PgToolCommand.Version(_options.PgDumpPath), cancellationToken);

        return outcome switch
        {
            { Launched: false } => Result<string>.Failure(new Error(
                "backup.tool_missing",
                $"The backup tool '{_options.PgDumpPath}' could not be launched. Install the PostgreSQL "
                + "client (version 16 or newer) or set Backup:PgDumpPath.")),
            { ExitCode: 0 } => Result<string>.Success(outcome.StandardOutput.Trim()),
            _ => Result<string>.Failure(new Error(
                "backup.tool_failed",
                $"'{_options.PgDumpPath} --version' exited with {outcome.ExitCode}.")),
        };
    }

    private async Task<Result> RunAsync(
        ProcessStartInfo startInfo,
        string binaryPath,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync(startInfo, cancellationToken);

        if (!outcome.Launched)
        {
            return Result.Failure(new Error(
                "backup.tool_missing",
                $"The backup tool '{binaryPath}' could not be launched. Install the PostgreSQL client "
                + "(version 16 or newer) or configure its path under the Backup section."));
        }

        if (outcome.TimedOut)
        {
            return Result.Failure(new Error(
                "backup.timed_out",
                $"'{binaryPath}' did not finish within {_options.Timeout}. Raise Backup:Timeout if the "
                + "database is large, and check the storage the backup root is on."));
        }

        if (outcome.ExitCode != 0)
        {
            return Result.Failure(new Error(
                errorCode,
                $"'{binaryPath}' exited with {outcome.ExitCode}: {Truncate(outcome.StandardError)}"));
        }

        return Result.Success();
    }

    private async Task<ProcessOutcome> ExecuteAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A missing tool is an operational state, not a crash: the job reports it and the operator
            // installs the client. The message names the binary and nothing else.
            _logger.LogWarning(
                "Could not launch '{BinaryPath}': {Reason}", startInfo.FileName, exception.Message);
            return ProcessOutcome.NotLaunched;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        // Drain both pipes concurrently: leaving one unread risks the child blocking on a full buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return ProcessOutcome.Timeout;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await SafeReadAsync(stdoutTask);
        var standardError = await SafeReadAsync(stderrTask);

        return new ProcessOutcome(true, false, process.ExitCode, standardOutput, standardError);
    }

    private static async Task<string> SafeReadAsync(Task<string> read)
    {
        try
        {
            return await read;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone between the check and the kill — nothing to do.
        }
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxErrorLength ? trimmed : trimmed[..MaxErrorLength] + "…";
    }

    private readonly record struct ProcessOutcome(
        bool Launched,
        bool TimedOut,
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public static ProcessOutcome NotLaunched { get; } = new(false, false, -1, string.Empty, string.Empty);

        public static ProcessOutcome Timeout { get; } = new(true, true, -1, string.Empty, string.Empty);
    }
}
