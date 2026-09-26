using System.Security.Claims;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Host.Operations;

/// <summary>
/// Read-only HTTP surface of the platform kernel: is the outbox/command backlog stuck, are the
/// scheduled jobs due, what is currently failing, and what retention the installation is configured
/// to keep. Answers "is the platform actually working" for an operator console without shell access.
/// <para>
/// The whole group is administrator-only: it exposes queue depth, job schedules and failure detail,
/// none of which a regular account has a use for. Nothing here writes.
/// </para>
/// </summary>
public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/operations").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/queue", async (OperationsQuery query, CancellationToken cancellationToken) =>
            Results.Ok(ToDto(await query.GetQueueSnapshotAsync(cancellationToken))));

        group.MapGet("/jobs", async (OperationsQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.GetJobsAsync(cancellationToken)).Select(ToDto)));

        group.MapGet("/commands/failed", async (int? limit, OperationsQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.GetFailedCommandsAsync(limit, cancellationToken)).Select(ToDto)));

        group.MapGet("/retention", (OperationsQuery query) => Results.Ok(ToDto(query.GetRetention())));

        group.MapGet("/settings", async (SettingsQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.ListAsync(cancellationToken)).Select(ToDto)));

        group.MapPut("/settings/{key}", async (
            string key,
            UpdateSettingRequest request,
            ClaimsPrincipal principal,
            ISettingsAdministration administration,
            SettingsQuery query,
            CancellationToken cancellationToken) =>
        {
            // RequireAuthorization(Administrator) above already refuses an unauthenticated or
            // non-administrator caller (401/403); this only guards the id ApplyAsync's audit trail needs.
            var viewer = Viewer.From(principal);
            if (viewer is null)
            {
                return Results.Unauthorized();
            }

            var change = request.Value is null
                ? SettingChange.Clear(key, request.ExpectedVersion)
                : SettingChange.Set(key, request.Value, request.ExpectedVersion);

            var result = await administration.ApplyAsync([change], viewer.Value.UserId, cancellationToken);
            if (result.IsFailure)
            {
                return SettingsFailureResult(result.Errors);
            }

            var summary = await query.GetAsync(key, cancellationToken);
            return summary is null ? Results.NotFound() : Results.Ok(ToDto(summary));
        });

        return endpoints;
    }

    private static object ToDto(QueueDepthSnapshot snapshot) => new
    {
        outboxPending = snapshot.OutboxPending,
        oldestOutboxAgeSeconds = snapshot.OldestOutboxAge.TotalSeconds,
        commandsByState = snapshot.CommandsByState,
    };

    private static object ToDto(ScheduledJobSummary job) => new
    {
        name = job.Name,
        commandType = job.CommandType,
        intervalSeconds = job.IntervalSeconds,
        lastRun = job.LastRun,
        nextDue = job.NextDue,
        enabled = job.Enabled,
    };

    private static object ToDto(FailedCommandSummary command) => new
    {
        id = command.Id,
        commandType = command.CommandType,
        attempts = command.Attempts,
        maxAttempts = command.MaxAttempts,
        lastAttemptAt = command.LastAttemptAt,
        error = command.Error,
    };

    private static object ToDto(RetentionSummary retention) => new
    {
        outboxRetentionSeconds = retention.OutboxRetentionSeconds,
        completedCommandRetentionSeconds = retention.CompletedCommandRetentionSeconds,
        failedCommandRetentionSeconds = retention.FailedCommandRetentionSeconds,
        batchSize = retention.BatchSize,
        intervalSeconds = retention.IntervalSeconds,
    };

    private static object ToDto(SettingSummary setting) => new
    {
        key = setting.Key,
        kind = setting.Kind.ToString(),
        isSecret = setting.IsSecret,
        configurationPath = setting.ConfigurationPath,
        value = setting.Value,
        isSet = setting.IsSet,
        source = setting.Source,
        version = setting.Version,
        defaultValue = setting.DefaultValue,
        allowedValues = setting.AllowedValues,
        minValue = setting.MinValue,
        maxValue = setting.MaxValue,
    };

    /// <summary>
    /// Maps a rejected <see cref="ISettingsAdministration.ApplyAsync"/> batch to the standard
    /// <c>{ error, message }</c> envelope, extended additively with <c>keys</c> — useful because a
    /// cross-key invariant such as <see cref="Cinomni.Operations.Retention.RetentionOptions.Check"/>'s
    /// names more than one key at once.
    /// </summary>
    private static IResult SettingsFailureResult(IReadOnlyList<SettingError> errors)
    {
        var first = errors[0];
        var status = first.Code switch
        {
            "settings.unknown_key" => StatusCodes.Status404NotFound,
            "settings.overridden_by_environment" or "settings.conflict" => StatusCodes.Status409Conflict,
            SettingsSecretCipher.SecretsUnavailableErrorCode => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };

        var message = errors.Count == 1
            ? first.Message
            : string.Join(" ", errors.Select(error => error.Message));
        var keys = errors.SelectMany(error => error.Keys).Distinct(StringComparer.Ordinal).ToArray();

        return Results.Json(new { error = first.Code, message, keys }, statusCode: status);
    }
}

/// <param name="Value"><c>null</c> clears the stored override; a string sets it.</param>
/// <param name="ExpectedVersion">The row version the caller last saw; <c>0</c> when it believes none exists.</param>
public sealed record UpdateSettingRequest(string? Value, long ExpectedVersion);
