namespace Cinomni.Kernel.Identifiers;

/// <summary>
/// Generates <c>UUIDv7</c> identifiers: time-ordered, avoiding the index
/// fragmentation of UUIDv4. This is the basis for the stable internal identity of
/// aggregates.
/// </summary>
public static class Uuid7
{
    /// <summary>Creates a new UUIDv7 based on the current instant.</summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>Creates a UUIDv7 anchored to a specific instant (useful for deterministic tests).</summary>
    public static Guid New(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
