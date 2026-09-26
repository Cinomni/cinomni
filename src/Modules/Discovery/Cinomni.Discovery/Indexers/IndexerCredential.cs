namespace Cinomni.Discovery.Indexers;

/// <summary>
/// A resolved credential on its way to one indexer request: the account name where the protocol uses
/// one, and the secret in plaintext because the transport is about to send it.
/// <para>
/// It is deliberately <b>not</b> a field on <see cref="Cinomni.Discovery.Contracts.IndexerSummary"/>.
/// That record is simultaneously the HTTP response body an administrator lists and the configuration
/// the adapter reads, so a secret placed on it would be one forgotten exclusion away from being
/// serialized to a browser. Keeping it in a separate parameter means the leak is not something a
/// projection has to remember to prevent — there is nothing on the DTO to leak.
/// </para>
/// </summary>
/// <param name="Secret">
/// An API key for Torznab/Newznab, or the password for a definition-backed login. Never logged, never
/// put in an exception message, and never returned over HTTP — an indexer request URL carrying one is
/// already treated as sensitive throughout this module.
/// </param>
public sealed record IndexerCredential(string? Username, string Secret);
