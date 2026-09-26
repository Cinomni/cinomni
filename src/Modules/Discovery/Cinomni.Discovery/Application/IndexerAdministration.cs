using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Net;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Discovery.Application;

public sealed class IndexerAdministration(
    DiscoveryDbContext dbContext,
    IUnitOfWork unitOfWork,
    IndexerCredentialProtector credentials,
    IIndexerClient? indexerClient = null,
    IIndexerSessionInvalidator? sessions = null,
    ILogger<IndexerAdministration>? logger = null)
    : IIndexerAdministration
{
    private readonly ILogger<IndexerAdministration> log = logger ?? NullLogger<IndexerAdministration>.Instance;

    public async Task<IReadOnlyList<IndexerCatalogEntry>> ListCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from entry in dbContext.IndexerCatalogEntries
                join source in dbContext.IndexerCatalogSources on entry.SourceId equals source.Id
                where source.Enabled
                orderby source.CreatedAt, source.Id, entry.Position
                select new { Entry = entry, SourceName = source.Name })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var installed = await dbContext.Indexers
            .AsNoTracking()
            .Where(i => i.CatalogSourceId != null && i.CatalogKey != null)
            .Select(i => new { SourceId = i.CatalogSourceId!.Value, Key = i.CatalogKey!, i.Id })
            .ToDictionaryAsync(i => (i.SourceId, i.Key), i => i.Id, cancellationToken);

        return rows.Select(row => ToCatalogEntry(
                row.Entry,
                row.SourceName,
                installed.TryGetValue((row.Entry.SourceId, row.Entry.Key), out var id) ? new IndexerId(id) : null))
            .ToList();
    }

    public async Task<Result<IndexerId>> InstallCatalogIndexerAsync(
        IndexerCatalogSourceId sourceId,
        string key,
        string? name,
        string? baseUrl,
        int? priority,
        IndexerSettings? settings,
        CancellationToken cancellationToken = default)
    {
        var found = await FindCatalogEntryAsync(sourceId, key, cancellationToken);
        if (found.IsFailure)
        {
            return Result<IndexerId>.Failure(found.Error);
        }

        var manifest = found.Value;
        if (await FindInstalledAsync(manifest, cancellationToken) is { } existing)
        {
            return Result<IndexerId>.Success(existing);
        }

        var draft = ValidateCatalogDraft(ToCatalogEntry(manifest, string.Empty, null), name, baseUrl, priority, settings);
        if (draft.IsFailure)
        {
            return Result<IndexerId>.Failure(draft.Error);
        }

        // Re-parsed at install, not trusted from the refresh: the parser may have grown stricter since.
        var parsed = IndexerDefinitionParser.Parse(manifest.RawDefinition, strictSelectors: true);
        if (parsed.IsFailure)
        {
            return Result<IndexerId>.Failure(new Error(
                "discovery.catalog.invalid_manifest", $"This catalog entry's definition is invalid: {parsed.Error.Message}"));
        }

        var definition = new IndexerDefinition
        {
            Id = Uuid7.New(), Name = TruncateName($"{manifest.Name} catalog v{manifest.Version}"),
            SchemaVersion = parsed.Value.SchemaVersion,
            ContentHash = ComputeContentHash(manifest.RawDefinition),
            RawContent = manifest.RawDefinition,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var indexer = NewCatalogIndexer(manifest, draft.Value, definition.Id);

        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.IndexerDefinitions.Add(definition);
                dbContext.Indexers.Add(indexer);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent install of the same entry won the unique (source, key) index.
            if (await FindInstalledAsync(manifest, cancellationToken) is { } winner)
            {
                return Result<IndexerId>.Success(winner);
            }

            throw;
        }

        return Result<IndexerId>.Success(new IndexerId(indexer.Id));
    }

    public async Task<Result<IndexerTestResult>> TestCatalogIndexerAsync(
        IndexerCatalogSourceId sourceId,
        string key,
        string? name,
        string? baseUrl,
        int? priority,
        IndexerSettings? settings,
        CancellationToken cancellationToken = default)
    {
        var found = await FindCatalogEntryAsync(sourceId, key, cancellationToken);
        if (found.IsFailure)
        {
            return Result<IndexerTestResult>.Failure(found.Error);
        }

        var manifest = found.Value;
        var draft = ValidateCatalogDraft(ToCatalogEntry(manifest, string.Empty, null), name, baseUrl, priority, settings);
        if (draft.IsFailure)
        {
            return Result<IndexerTestResult>.Failure(draft.Error);
        }

        var summary = new IndexerSummary(new IndexerId(Guid.Empty), draft.Value.Name, IndexerProtocol.Definition,
            draft.Value.BaseUrl, draft.Value.Priority, true, DefinitionId: new IndexerDefinitionId(Guid.Empty),
            Settings: draft.Value.Settings, CatalogKey: manifest.Key, CatalogVersion: manifest.Version,
            CatalogSourceId: sourceId);
        return Result<IndexerTestResult>.Success(await RunTestAsync(
            summary, null, manifest.RawDefinition, cancellationToken));
    }

    public async Task<Result> SetSettingsAsync(
        IndexerId indexerId,
        IndexerSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateSettingsValues(settings))
        {
            return Result.Failure(new Error(
                "discovery.invalid_settings", "Indexer settings are outside their allowed bounds."));
        }

        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        // Resolved against the source's current snapshot when there is one. An indexer with no source
        // (added by hand, installed from the catalog earlier versions shipped built in, or whose source
        // was removed) is a plain definition indexer, and the choice is the administrator's.
        var entry = indexer is { CatalogSourceId: Guid sourceId, CatalogKey: { } catalogKey }
            ? await dbContext.IndexerCatalogEntries.AsNoTracking()
                .Where(e => e.SourceId == sourceId && e.Key == catalogKey)
                .Select(e => new { e.Name, e.RequiresFlareSolverr })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (entry?.RequiresFlareSolverr == true && !settings.UseFlareSolverr)
        {
            return Result.Failure(new Error(
                "discovery.catalog.browser_required", $"{entry.Name} requires FlareSolverr."));
        }

        ApplySettings(indexer, settings);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// The snapshot entry an install or test addresses. A disabled source's entries are neither
    /// listed nor installable, so both answer as if the entry were not there.
    /// </summary>
    private async Task<Result<IndexerCatalogSourceEntry>> FindCatalogEntryAsync(
        IndexerCatalogSourceId sourceId, string key, CancellationToken cancellationToken)
    {
        var source = await dbContext.IndexerCatalogSources.AsNoTracking()
            .Where(s => s.Id == sourceId.Value).Select(s => new { s.Enabled }).SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return Result<IndexerCatalogSourceEntry>.Failure(new Error(
                IndexerCatalogSources.NotFoundCode, "No catalog source with that id."));
        }

        var normalizedKey = key?.Trim().ToLowerInvariant() ?? string.Empty;
        var entry = source.Enabled
            ? await dbContext.IndexerCatalogEntries.AsNoTracking()
                .SingleOrDefaultAsync(e => e.SourceId == sourceId.Value && e.Key == normalizedKey, cancellationToken)
            : null;
        return entry is null
            ? Result<IndexerCatalogSourceEntry>.Failure(new Error(
                "discovery.catalog.not_found", "No catalog indexer with that key in an enabled source."))
            : Result<IndexerCatalogSourceEntry>.Success(entry);
    }

    private async Task<IndexerId?> FindInstalledAsync(IndexerCatalogSourceEntry entry, CancellationToken cancellationToken)
    {
        var id = await dbContext.Indexers.AsNoTracking()
            .Where(i => i.CatalogSourceId == entry.SourceId && i.CatalogKey == entry.Key)
            .Select(i => (Guid?)i.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return id is Guid value ? new IndexerId(value) : null;
    }

    private static IndexerCatalogEntry ToCatalogEntry(
        IndexerCatalogSourceEntry entry, string sourceName, IndexerId? installedIndexerId) => new(
        entry.Key, entry.Version, entry.Name, entry.Description, IndexerProtocol.Definition, entry.ReleaseProtocol,
        entry.BaseUrls, entry.RequiresFlareSolverr, installedIndexerId, entry.DefaultPriority,
        entry.ToDefaultSettings(), new IndexerCatalogSourceId(entry.SourceId), sourceName);

    private static string TruncateName(string name) =>
        name.Length > IndexerDefinition.NameMaxLength ? name[..IndexerDefinition.NameMaxLength] : name;

    private static bool DeclaresSession(string? rawDefinition) =>
        IndexerDefinitionParser.Parse(rawDefinition ?? string.Empty) is { IsSuccess: true } parsed
        && parsed.Value.Session is not null;

    public async Task<Result<IndexerTestResult>> TestIndexerAsync(
        IndexerId indexerId,
        CancellationToken cancellationToken = default)
    {
        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result<IndexerTestResult>.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        var definition = indexer.DefinitionId is Guid definitionId
            ? await dbContext.IndexerDefinitions.AsNoTracking()
                .Where(d => d.Id == definitionId).Select(d => d.RawContent).SingleOrDefaultAsync(cancellationToken)
            : null;
        var session = await dbContext.IndexerSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IndexerId == indexerId.Value, cancellationToken);
        var summary = ToSummary(indexer, DeclaresSession(definition), session);
        var credential = credentials.TryDecrypt(indexer) is { } secret
            ? new IndexerCredential(indexer.CredentialUsername, secret)
            : null;
        var test = await RunTestAsync(summary, credential, definition, cancellationToken);
        indexer.LastTestedAt = test.TestedAt;
        indexer.LastTestSucceeded = test.Succeeded;
        indexer.LastTestCode = test.Code;
        indexer.LastTestMessage = test.Message;
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result<IndexerTestResult>.Success(test);
    }

    public async Task<Result<IndexerId>> AddIndexerAsync(
        string name,
        IndexerProtocol protocol,
        string baseUrl,
        int priority,
        IndexerDefinitionId? definitionId = null,
        NewIndexerCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        if (credential is not null && CredentialShapeError(credential.Username, credential.Secret) is { } shapeError)
        {
            return Result<IndexerId>.Failure(shapeError);
        }

        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > Indexer.NameMaxLength)
        {
            return Result<IndexerId>.Failure(new Error(
                "discovery.invalid_name", $"Indexer name must be between 1 and {Indexer.NameMaxLength} characters."));
        }

        if (!Enum.IsDefined(protocol))
        {
            return Result<IndexerId>.Failure(new Error("discovery.invalid_protocol", "Unknown indexer protocol."));
        }

        if (PriorityError(priority) is { } priorityError)
        {
            return Result<IndexerId>.Failure(priorityError);
        }

        // Reject an internal/loopback/metadata base URL at the boundary (SSRF); the actual
        // resolved IP is re-checked at connect time to defend against DNS rebinding.
        if (!SsrfGuard.TryValidatePublicUrl(baseUrl, out var uri))
        {
            // Counted, never quoted: an indexer base URL routinely carries its API key in the query
            // string, so the value stays in the caller's error and out of every time series.
            NetworkGuardMetrics.RecordRejection(
                NetworkGuardMetrics.Reasons.InvalidUrl, CinomniTelemetry.Modules.Discovery);

            return Result<IndexerId>.Failure(
                new Error("discovery.invalid_base_url", "Base URL must be an absolute http(s) URL with a public host."));
        }

        // Measured as stored: the normalised form can be longer than what was typed (an encoded path).
        if (uri!.ToString().Length > Indexer.BaseUrlMaxLength)
        {
            return Result<IndexerId>.Failure(new Error(
                "discovery.invalid_base_url", $"Base URL must be at most {Indexer.BaseUrlMaxLength} characters."));
        }

        if (protocol == IndexerProtocol.Definition)
        {
            if (definitionId is null)
            {
                return Result<IndexerId>.Failure(new Error(
                    "discovery.missing_definition", "A 'Definition' protocol indexer requires a definitionId."));
            }

            var definitionExists = await dbContext.IndexerDefinitions
                .AsNoTracking()
                .AnyAsync(d => d.Id == definitionId.Value.Value, cancellationToken);
            if (!definitionExists)
            {
                return Result<IndexerId>.Failure(
                    new Error("discovery.definition_not_found", "No indexer definition with that id."));
            }
        }
        else if (definitionId is not null)
        {
            return Result<IndexerId>.Failure(new Error(
                "discovery.unexpected_definition", "A definitionId is only valid for the 'Definition' protocol."));
        }

        var indexer = new Indexer
        {
            Id = Uuid7.New(),
            Name = normalizedName,
            Protocol = protocol,
            BaseUrl = uri!.ToString(),
            Priority = priority,
            Enabled = true,
            DefinitionId = definitionId?.Value,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (credential is not null)
        {
            if (CredentialProtocolError(protocol, uri, credential.Username) is { } protocolError)
            {
                return Result<IndexerId>.Failure(protocolError);
            }

            if (StoreCredential(indexer, credential.Username, credential.Secret) is { } storeError)
            {
                return Result<IndexerId>.Failure(storeError);
            }
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Indexers.Add(indexer);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result<IndexerId>.Success(new IndexerId(indexer.Id));
    }

    public async Task<IReadOnlyList<IndexerSummary>> ListIndexersAsync(CancellationToken cancellationToken = default)
    {
        var indexers = await dbContext.Indexers
            .AsNoTracking()
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Name)
            .ToListAsync(cancellationToken);

        // What the session listing needs about each indexer's definition: only whether it declares a
        // login. Parsing is the module's own way of knowing anything about a definition — the
        // validated shape, not the stored JSON, is what the engine acts on.
        var declaredSessions = await LoadDeclaredSessionsAsync(indexers, cancellationToken);
        var sessionRows = await dbContext.IndexerSessions
            .AsNoTracking()
            .ToDictionaryAsync(s => s.IndexerId, cancellationToken);

        return indexers
            // A state, never the secret and never its ciphertext. It costs one decryption per indexer
            // and it is the only way the caller learns the difference between a credential that is
            // stored and one this installation can actually use — a row written under a master key
            // that has since been rotated or lost is populated and useless, and reporting it as
            // "configured" claims something the installation cannot know.
            .Select(i => ToSummary(
                i,
                i.DefinitionId is Guid definitionId && declaredSessions.TryGetValue(definitionId, out var declared) && declared,
                sessionRows.GetValueOrDefault(i.Id)))
            .ToList();
    }

    public async Task<Result> SetCapabilitiesAsync(
        IndexerId indexerId,
        IndexerCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        if (Indexer.CapabilityProblem(capabilities) is { } problem)
        {
            return Result.Failure(new Error("discovery.invalid_capabilities", problem));
        }

        indexer.ApplyCapabilities(capabilities);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Stores this indexer's credential, replacing whatever was there. There is no read counterpart
    /// by design: a secret goes in and never comes back out, so a caller that wants to change one
    /// supplies it again rather than editing what is stored.
    /// </summary>
    public async Task<Result> SetCredentialAsync(
        IndexerId indexerId,
        string? username,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (CredentialShapeError(username, secret) is { } shapeError)
        {
            return Result.Failure(shapeError);
        }

        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        if (CredentialProtocolError(indexer.Protocol, new Uri(indexer.BaseUrl), username) is { } protocolError)
        {
            return Result.Failure(protocolError);
        }

        // Encrypting before the transaction opens keeps it short, and means an installation with no
        // master key is refused without having written anything (the settings store's own order).
        if (StoreCredential(indexer, username, secret) is { } storeError)
        {
            return Result.Failure(storeError);
        }

        await DeleteSessionAsync(indexerId, cancellationToken);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        await ForgetSessionAsync(indexerId, cancellationToken);
        return Result.Success();
    }

    /// <summary>What any credential must satisfy, before the indexer it belongs to is known.</summary>
    private static Error? CredentialShapeError(string? username, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new Error(
                "discovery.invalid_credential", "A credential needs a secret; clear it instead to remove one.");
        }

        return username?.Trim().Length > Indexer.CredentialUsernameMaxLength
            ? new Error(
                "discovery.invalid_credential",
                $"The username must be at most {Indexer.CredentialUsernameMaxLength} characters.")
            : null;
    }

    /// <summary>
    /// What a username and password must satisfy on a Torznab/Newznab indexer, where they travel as
    /// HTTP Basic. A 'Definition' login is held to https by the session engine against its own login
    /// URL, which may differ from the base URL, so it is not checked here.
    /// </summary>
    private static Error? CredentialProtocolError(IndexerProtocol protocol, Uri baseUri, string? username)
    {
        if (protocol == IndexerProtocol.Definition || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        // RFC 7617: the user-id ends at the first colon, so one inside it would silently shift the
        // rest of the name into the password.
        if (username.Contains(':', StringComparison.Ordinal))
        {
            return new Error("discovery.invalid_credential", "The username cannot contain ':'.");
        }

        // Basic over plain HTTP is the password in clear on the wire, and an account password is
        // usually reused elsewhere — unlike an API key issued for this one indexer.
        return baseUri.Scheme == Uri.UriSchemeHttps
            ? null
            : new Error(
                "discovery.insecure_credential",
                "A username and password are only accepted for an indexer whose base URL is https.");
    }

    /// <summary>Encrypts under the indexer's own identity and writes the credential onto the entity.</summary>
    private Error? StoreCredential(Indexer indexer, string? username, string secret)
    {
        var encrypted = credentials.Encrypt(indexer.Id, secret);
        if (encrypted.IsFailure)
        {
            return encrypted.Error;
        }

        indexer.SetCredential(username, encrypted.Value.Cipher, encrypted.Value.Nonce, encrypted.Value.KeyId);
        return null;
    }

    /// <summary>Removes the credential; the indexer is queried unauthenticated from the next search on.</summary>
    public async Task<Result> ClearCredentialAsync(IndexerId indexerId, CancellationToken cancellationToken = default)
    {
        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        indexer.ClearCredential();
        await DeleteSessionAsync(indexerId, cancellationToken);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        await ForgetSessionAsync(indexerId, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetEnabledAsync(
        IndexerId indexerId, bool enabled, CancellationToken cancellationToken = default)
    {
        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        if (indexer.Enabled == enabled)
        {
            return Result.Success();
        }

        indexer.Enabled = enabled;
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetPriorityAsync(
        IndexerId indexerId, int priority, CancellationToken cancellationToken = default)
    {
        if (PriorityError(priority) is { } priorityError)
        {
            return Result.Failure(priorityError);
        }

        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        if (indexer.Priority == priority)
        {
            return Result.Success();
        }

        indexer.Priority = priority;
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeleteIndexerAsync(IndexerId indexerId, CancellationToken cancellationToken = default)
    {
        var indexer = await dbContext.Indexers.FirstOrDefaultAsync(i => i.Id == indexerId.Value, cancellationToken);
        if (indexer is null)
        {
            return Result.Failure(new Error("discovery.indexer_not_found", "No indexer with that id."));
        }

        // The session row cascades, but removing it in this save keeps the credential's session from
        // outliving the indexer even if a later edit changes that cascade. The definition is not
        // touched: it may still be referenced, and deleting it here would be a second decision.
        await DeleteSessionAsync(indexerId, cancellationToken);
        dbContext.Indexers.Remove(indexer);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        await ForgetSessionAsync(indexerId, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Drops the session the running process is still holding in memory, after the row it was
    /// stored in has been deleted. Deleting only the row would leave a live installation searching
    /// with a jar its own listing reports as gone — the manager caches per credential, so a changed
    /// secret would sign in again on its own, but an identical one re-submitted would not.
    /// </summary>
    private Task ForgetSessionAsync(IndexerId indexerId, CancellationToken cancellationToken) =>
        sessions?.InvalidateAsync(indexerId.Value, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Marks the indexer's session row for deletion in the same save as the credential change: a
    /// stored session authenticates the account it signed in as, and outliving that account would
    /// let an old login answer for a new one.
    /// </summary>
    private async Task DeleteSessionAsync(IndexerId indexerId, CancellationToken cancellationToken)
    {
        var session = await dbContext.IndexerSessions
            .FirstOrDefaultAsync(s => s.IndexerId == indexerId.Value, cancellationToken);
        if (session is not null)
        {
            dbContext.IndexerSessions.Remove(session);
        }
    }

    public async Task<Result<IndexerDefinitionId>> UploadDefinitionAsync(
        string name,
        string rawContent,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > IndexerDefinition.NameMaxLength)
        {
            return Result<IndexerDefinitionId>.Failure(new Error(
                "discovery.invalid_name",
                $"Definition name must be between 1 and {IndexerDefinition.NameMaxLength} characters."));
        }

        // The same parser a search would use: a bad definition fails here with the exact named
        // error it would otherwise only surface at search time.
        var parsed = IndexerDefinitionParser.Parse(rawContent, strictSelectors: true);
        if (parsed.IsFailure)
        {
            return Result<IndexerDefinitionId>.Failure(parsed.Error);
        }

        var definition = new IndexerDefinition
        {
            Id = Uuid7.New(),
            Name = normalizedName,
            SchemaVersion = parsed.Value.SchemaVersion,
            ContentHash = ComputeContentHash(rawContent),
            RawContent = rawContent,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.IndexerDefinitions.Add(definition);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result<IndexerDefinitionId>.Success(new IndexerDefinitionId(definition.Id));
    }

    public Task<Result<DefinitionExtractionResult>> ValidateDefinitionAsync(
        string rawContent,
        string? sampleResponseBody,
        string? sampleRequestUrl,
        CancellationToken cancellationToken = default)
    {
        var parsed = IndexerDefinitionParser.Parse(rawContent, strictSelectors: true);
        if (parsed.IsFailure)
        {
            return Task.FromResult(Result<DefinitionExtractionResult>.Failure(parsed.Error));
        }

        if (string.IsNullOrWhiteSpace(sampleResponseBody))
        {
            // Format-only validation: nothing to extract from without a sample response.
            return Task.FromResult(Result<DefinitionExtractionResult>.Success(new DefinitionExtractionResult([], [])));
        }

        if (!Uri.TryCreate(sampleRequestUrl, UriKind.Absolute, out var requestUri))
        {
            return Task.FromResult(Result<DefinitionExtractionResult>.Failure(new Error(
                "discovery.definition.validate.invalid_sample_request_url",
                "'sampleRequestUrl' must be an absolute URL when a sample response is supplied.")));
        }

        // Never a real HTTP request: the sample response was supplied by the caller. The extraction
        // carries the field rules that failed as well as the candidates, which is what makes this a
        // debugging tool rather than a second opinion of the same silence.
        var extraction = DefinitionResponseParser.Parse(parsed.Value, sampleResponseBody, "sample", requestUri);
        return Task.FromResult(Result<DefinitionExtractionResult>.Success(extraction));
    }

    public async Task<IReadOnlyList<IndexerDefinitionSummary>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await dbContext.IndexerDefinitions
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return definitions
            .Select(d => new IndexerDefinitionSummary(
                new IndexerDefinitionId(d.Id), d.Name, d.SchemaVersion, d.ContentHash, d.CreatedAt))
            .ToList();
    }

    private static string ComputeContentHash(string rawContent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent)));

    /// <summary>
    /// Which of the fan-out's definitions declare a login, keyed by definition id. One query for all
    /// of them; a definition that no longer parses counts as declaring nothing, the same answer the
    /// search path reaches by refusing to run it.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, bool>> LoadDeclaredSessionsAsync(
        IReadOnlyList<Indexer> indexers,
        CancellationToken cancellationToken)
    {
        var definitionIds = indexers
            .Select(i => i.DefinitionId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        if (definitionIds.Length == 0)
        {
            return ReadOnlyDictionary<Guid, bool>.Empty;
        }

        var documents = await dbContext.IndexerDefinitions
            .AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.RawContent })
            .ToListAsync(cancellationToken);

        return documents.ToDictionary(d => d.Id, d => DeclaresSession(d.RawContent));
    }

    private IndexerSummary ToSummary(Indexer i, bool sessionDeclared, IndexerSession? session)
    {
        // Classified once. It is an AES-GCM decryption per indexer, and the listing needs the answer
        // twice — for the credential state it reports and for the session state derived from it.
        var credentialState = credentials.Classify(i);
        return new IndexerSummary(
            new IndexerId(i.Id), i.Name, i.Protocol, i.BaseUrl, i.Priority, i.Enabled, i.ToCapabilities(),
            i.DefinitionId is Guid definitionId ? new IndexerDefinitionId(definitionId) : null,
            i.CredentialUsername, credentialState, i.ToSettings(), i.CatalogKey, i.CatalogVersion,
            i.LastTestedAt, i.LastTestSucceeded, i.LastTestCode, i.LastTestMessage,
            DeclaresLogin: sessionDeclared,
            SessionState: SessionStateFor(sessionDeclared, i, credentialState, session),
            LastLoginAt: session?.CapturedAt,
            CatalogSourceId: i.CatalogSourceId is Guid sourceId ? new IndexerCatalogSourceId(sourceId) : null);
    }

    private static IndexerSessionState SessionStateFor(
        bool sessionDeclared, Indexer indexer, IndexerCredentialState credentialState, IndexerSession? session)
    {
        // A session belongs to a credential: an unreadable or absent one means there is nothing to
        // sign in with, which from the session's side is the same "none" as a definition that
        // declares no login at all.
        if (!sessionDeclared
            || indexer.SecretCipher is null
            || credentialState != IndexerCredentialState.Readable)
        {
            return IndexerSessionState.None;
        }

        if (session?.CookiesCipher is not null)
        {
            return IndexerSessionState.Active;
        }

        return session is null || session.LastAttemptOk
            ? IndexerSessionState.NotLoggedIn
            : IndexerSessionState.Failed;
    }

    internal const string NotSignedInCode = "discovery.indexer.test_not_signed_in";

    private async Task<IndexerTestResult> RunTestAsync(
        IndexerSummary summary,
        IndexerCredential? credential,
        string? definition,
        CancellationToken cancellationToken)
    {
        var testedAt = DateTimeOffset.UtcNow;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (indexerClient is null)
            {
                return new IndexerTestResult(false, "discovery.indexer.transport_unavailable",
                    "Indexer transport is unavailable.", 0, 0, testedAt);
            }

            var candidates = await indexerClient.SearchAsync(summary, credential, definition,
                new Cinomni.Search.Contracts.SearchCriterion("Sintel", null, null, null, "Movie"), cancellationToken);
            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var authenticated = await AuthenticationOfAsync(summary, credential, definition, cancellationToken);

            // A search that ran because signing in did not is not a passing test of a login indexer: the
            // site answered its public face — often just the login page — and a private tracker searched
            // that way finds nothing, every time, while the row says the indexer works.
            if (authenticated == false)
            {
                return new IndexerTestResult(false, NotSignedInCode,
                    "The site answered, but signing in did not work, so it was searched as a visitor. Check the "
                    + "username and password; a failed sign-in waits a few minutes before it is tried again.",
                    candidates.Count, elapsed, testedAt, authenticated);
            }

            return new IndexerTestResult(true, "discovery.indexer.test_succeeded", "Indexer test succeeded.",
                candidates.Count, elapsed, testedAt, authenticated);
        }
        catch (IndexerQuotaExceededException exhausted)
        {
            return new IndexerTestResult(false, exhausted.Code, "The daily request limit is exhausted.", 0,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, testedAt);
        }
        // A cancelled request is the caller leaving; an HttpClient timeout also surfaces as a cancellation,
        // but with the caller's token untouched, and that one is a result worth reporting.
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var (code, message) = IndexerTestFailure.Describe(failure);
            // The operator's log gets the whole exception (host included, which is what debugging a
            // refused indexer needs); the API answer gets only the bounded code and a fixed sentence.
            log.LogWarning(failure, "Indexer test for {IndexerName} failed ({Code})", summary.Name, code);
            return new IndexerTestResult(false, code, message, 0,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, testedAt);
        }
    }

    /// <summary>
    /// Whether the test that just ran searched <em>with</em> the indexer's session. Only meaningful
    /// when the definition declares a login, a usable credential was resolved, and the test targeted
    /// a stored indexer (a catalog draft has no row to have signed in for) — null otherwise, because
    /// "authenticated" is not a question for an indexer that needs no login.
    /// </summary>
    private async Task<bool?> AuthenticationOfAsync(
        IndexerSummary summary,
        IndexerCredential? credential,
        string? definition,
        CancellationToken cancellationToken)
    {
        if (credential is null || definition is null || summary.Id.Value == Guid.Empty
            || !DeclaresSession(definition))
        {
            return null;
        }

        var row = await dbContext.IndexerSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IndexerId == summary.Id.Value, cancellationToken);
        return row?.CookiesCipher is not null;
    }

    private static Result<CatalogDraft> ValidateCatalogDraft(
        IndexerCatalogEntry entry, string? name, string? baseUrl, int? priority, IndexerSettings? settings)
    {
        var defaultBaseUrl = entry.BaseUrls[0];
        var draft = new CatalogDraft(name?.Trim() ?? entry.Name, baseUrl?.Trim() ?? defaultBaseUrl,
            priority ?? entry.DefaultPriority, settings ?? entry.DefaultSettings);
        var validation = ValidateSettings(draft.Name, draft.BaseUrl, draft.Priority, draft.Settings);
        if (validation.IsFailure)
        {
            return Result<CatalogDraft>.Failure(validation.Error);
        }

        if (!entry.BaseUrls.Any(url => DefinitionQueryBuilder.SameOrigin(new Uri(url), new Uri(draft.BaseUrl))))
        {
            return Result<CatalogDraft>.Failure(new Error(
                "discovery.catalog.unsupported_base_url", "Base URL is not supported by this catalog entry."));
        }

        if (entry.RequiresFlareSolverr && !draft.Settings.UseFlareSolverr)
        {
            return Result<CatalogDraft>.Failure(new Error(
                "discovery.catalog.browser_required", $"{entry.Name} requires FlareSolverr."));
        }

        return Result<CatalogDraft>.Success(draft);
    }

    private static Result ValidateSettings(string name, string baseUrl, int priority, IndexerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > Indexer.NameMaxLength)
        {
            return Result.Failure(new Error("discovery.invalid_name", $"Indexer name must contain 1 to {Indexer.NameMaxLength} characters."));
        }

        if (PriorityError(priority) is { } priorityError)
        {
            return Result.Failure(priorityError);
        }

        // Measured as stored (NewCatalogIndexer keeps new Uri(...).ToString()): normalisation lengthens a
        // URL — a trailing slash, every stray '%' escaped — so the typed length is not the stored one.
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !SsrfGuard.TryValidatePublicUrl(baseUrl, out _)
            || new Uri(baseUrl).ToString().Length > Indexer.BaseUrlMaxLength)
        {
            return Result.Failure(new Error("discovery.invalid_base_url", "Base URL must be an absolute http(s) URL with a public host."));
        }

        if (!ValidateSettingsValues(settings))
        {
            return Result.Failure(new Error("discovery.invalid_settings", "Indexer settings are outside their allowed bounds."));
        }

        return Result.Success();
    }

    private const int MinPriority = 1;

    private const int MaxPriority = 50;

    private static Error? PriorityError(int priority) =>
        priority is >= MinPriority and <= MaxPriority
            ? null
            : new Error("discovery.invalid_priority", "Priority must be between 1 and 50.");

    private static bool ValidateSettingsValues(IndexerSettings settings) =>
        settings.MinimumSeeders is not < 0
        && settings.QueryLimit is not <= 0
        && settings.GrabLimit is not <= 0
        && Enum.IsDefined(settings.LimitsUnit);

    private static Indexer NewCatalogIndexer(IndexerCatalogSourceEntry entry, CatalogDraft draft, Guid definitionId)
    {
        var indexer = new Indexer
        {
            Id = Uuid7.New(), Name = draft.Name, Protocol = IndexerProtocol.Definition,
            BaseUrl = new Uri(draft.BaseUrl).ToString(), Priority = draft.Priority, Enabled = true,
            DefinitionId = definitionId, CatalogKey = entry.Key, CatalogVersion = entry.Version,
            CatalogSourceId = entry.SourceId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ApplySettings(indexer, draft.Settings);
        return indexer;
    }

    private static void ApplySettings(Indexer indexer, IndexerSettings settings)
    {
        indexer.MinimumSeeders = settings.MinimumSeeders;
        indexer.PreferMagnet = settings.PreferMagnet;
        indexer.QueryLimit = settings.QueryLimit;
        indexer.GrabLimit = settings.GrabLimit;
        indexer.LimitsUnit = settings.LimitsUnit;
        indexer.UseFlareSolverr = settings.UseFlareSolverr;
    }

    private sealed record CatalogDraft(string Name, string BaseUrl, int Priority, IndexerSettings Settings);
}
