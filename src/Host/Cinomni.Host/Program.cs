using System.Text.Json.Serialization;
using Cinomni.Acquisition;
using Cinomni.Catalog;
using Cinomni.Decision;
using Cinomni.Discovery;
using Cinomni.Downloads;
using Cinomni.Identity;
using Cinomni.Import;
using Cinomni.Library;
using Cinomni.Metadata;
using Cinomni.Host.RateLimiting;
using Cinomni.Host.Security;
using Cinomni.Playback;
using Cinomni.Subtitles;
using Cinomni.Monitoring;
using Cinomni.Notifications;
using Cinomni.Host;
using Cinomni.Host.Health;
using Cinomni.Host.Observability;
using Cinomni.Operations;
using Cinomni.ReleaseParsing;
using Cinomni.Requests;

// A `backup` verb is recognised before anything is built, and its tokens are kept out of the
// configuration command line so only real overrides reach the builder. Null means a normal start.
var backupCommand = BackupCommandLine.TryParse(args);

var builder = WebApplication.CreateBuilder(backupCommand?.HostArguments ?? args);

// Telemetry first, so a startup failure is already correlated. Log correlation is always on and costs
// nothing; the OpenTelemetry providers are registered only when an OTLP endpoint is configured, so a
// default installation sends nothing anywhere. A configuration that cannot be honoured throws here,
// rather than exporting silently to nowhere.
var observability = builder.AddCinomniObservability();

var connectionString = builder.Configuration.GetConnectionString("Cinomni")
    ?? throw new InvalidOperationException("Missing connection string 'Cinomni'.");

// Enums travel as their names ("Tmdb", "Movie") on the wire, both in and out.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// A body missing a required field, and an exception no endpoint caught, answer in the error envelope.
builder.Services.AddCinomniApiErrors();

// Composition root: platform kernel first, then each module registers itself. The list lives in
// CinomniModules so the wiring tests compose this installation rather than a copy of it.
builder.Services.AddCinomniModules(builder.Configuration, connectionString);

// Readiness checks come after the modules, because each one asks a module for the root or the client it
// already bound rather than reading configuration a second time.
builder.Services.AddCinomniHealthChecks(observability);

// The throttle on the anonymous credential routes. After the modules, because its two keys join the
// same settings catalogue every module contributes to.
builder.Services.AddCinomniRateLimiting();

var app = builder.Build();

// Pure path arithmetic, so it runs in every environment: a backup root that overlaps a media root would
// put a dump full of delivery-channel URLs and password verifiers inside a tree the library serves from,
// and would point the only deleting code in the product at somebody's media.
BackupStartup.VerifyRootIsSeparate(app.Services);

// The same arithmetic for the transcode root: Playback deletes inside it on its own initiative.
TranscodeRootStartup.VerifyRootIsSeparate(app.Services);

// A `backup` verb never serves traffic, and it runs BEFORE the migrate sequence on purpose: `backup
// check` against an empty database must not create all sixteen schemas and mask the mismatch it exists
// to find. Restore is deliberately not a verb here — it needs the Host stopped (see BackupCommandLine).
if (backupCommand is not null)
{
    return await backupCommand.RunAsync(app.Services);
}

// Packaging: in a container the storage roots arrive as mounts, so a root this account cannot write must
// stop the start here rather than leave an import deferring forever. It proves writability, not that the
// volume behind the path is the one intended — an absent root is created. Production only.
//
// A violated contract is somebody's configuration, not a defect in this process, so it ends the start
// the way a command-line verb ends: the reason already logged, and an exit code that says the
// configuration is wrong. The application is disposed first so the logging providers flush that line
// before the process goes. Everything above this point still crashes as it always did — a composition or
// binding failure is a bug, and its stack trace is the answer to it.
if (StartupChecks.VerifyRuntimeContract(app) is { } contractExitCode)
{
    await app.DisposeAsync();
    return contractExitCode;
}

BackupStartup.ReportBackupContract(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BackupStartup)));

// Same reasoning as the backup tool check above: this can only ever produce a log line, so it runs
// in every environment rather than only Production.
await StartupChecks.ReportHardwareCapabilitiesAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupChecks)));

// Every module has now registered its settings catalogue: a duplicate key across two modules is a
// composition bug and must stop the Host here, named, rather than surface as an unhandled 500 on the
// first PUT that happens to touch it.
Cinomni.Operations.Settings.SettingsCatalogueStartup.VerifyUniqueKeys(app.Services);

// The settings store's master-key contract: an installation without CINOMNI_SECRET_KEY still starts
// (secret settings just become unwritable, and any already stored read as unreadable), so this is a
// warning naming the variable, never a startup failure.
Cinomni.Operations.Settings.SettingsSecretsStartup.ReportSecretsContract(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Cinomni.Operations.Settings.SettingsSecretsStartup)));

// Startup sequence: migrate every schema, then
// recover before serving traffic — the platform first (requeue orphaned commands, upsert scheduled
// jobs), then the two modules whose work outlives the process.
//
// Only the first position is load-bearing: Operations owns the command queue the other two write
// into. Downloads before Import is a stable convention and nothing more — a transfer that completes
// during recovery writes its DownloadCompleted to the outbox, and the relay that delivers it is a
// hosted service that does not start until app.Run(), so Import's pass cannot see it either way. It
// arrives through the ordinary relay once the installation is serving.
//
// Neither module recovery can stop the installation from starting. The sidecar is a separate process
// and is routinely absent, and a download left un-imported for one more start is a far better outcome
// than a backend that refuses to serve the library over it.
await app.Services.MigrateOperationsAsync();

// The settings store's read path: loads whatever operations.setting held at boot into the in-process
// cache before any module resolves an ILiveOptions<T>. No module registers one yet, so this is inert
// today; it exists here because it must run after the operations schema is migrated and before the
// Host serves traffic, exactly like the migrate/recover sequence around it.
await app.Services.LoadSettingsAsync();

await app.Services.MigrateIdentityAsync();
await app.Services.MigrateCatalogAsync();
await app.Services.MigrateMetadataAsync();
await app.Services.MigrateMonitoringAsync();
await app.Services.MigrateDiscoveryAsync();
await app.Services.MigrateReleaseParsingAsync();
await app.Services.MigrateDecisionAsync();
await app.Services.MigrateAcquisitionAsync();
await app.Services.MigrateDownloadsAsync();
await app.Services.MigrateImportAsync();
await app.Services.MigrateLibraryAsync();
await app.Services.MigratePlaybackAsync();
await app.Services.MigrateSubtitlesAsync();
await app.Services.MigrateRequestsAsync();
await app.Services.MigrateNotificationsAsync();
await app.Services.RecoverOperationsAsync();
await app.Services.RecoverDownloadsAsync();
await app.Services.RecoverImportsAsync();

// First, before anything reads an address: whether X-Forwarded-For may be believed decides which
// client the rate limiter is counting, and getting it wrong is not a small error in either direction.
app.UseCinomniForwardedHeaders(
    builder.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cinomni.Host"));

// Immediately after, so every response the Host writes carries them — the web client bundle above all,
// since that is the origin whose scripts can read the session token out of localStorage.
app.UseCinomniSecurityHeaders();

// Right after, so it wraps everything that can throw, and its answers carry the headers above.
app.UseCinomniApiErrors();

// The built web client ships in wwwroot. UseStaticFiles (not MapStaticAssets) reads that directory at
// runtime, so the bundle can be laid down by a packaging step after the backend was published.
// WebClientFiles decides how long a browser may keep each file — see it for why an upgrade depends on it.
app.UseStaticFiles(WebClientFiles.Options());

// Before authentication, not after. The opaque-token handler runs on every request — it accepts a
// token from the query string as well as the header — so authenticating first would spend a pooled
// database connection and a session query on requests this is about to refuse, which is most of what
// a flood costs. This limiter reads the connection address and never the principal; the policy that
// counts a signed-in account is the second UseRateLimiter after this pair.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// After authorization, so the second-factor routes are counted against the account a session belongs
// to — a stolen session can come from any address.
app.UseCinomniAccountRateLimiter();

// The HTTP surface lives in CinomniApi, so the authorization test enumerates what the Host really maps.
app.MapCinomniEndpoints();

app.Run();

// Top-level statements: the serving path falls through to 0, and only a `backup` verb returns early
// with its own exit code.
return 0;
