using System.Text.Json;

namespace Cinomni.Operations.Messaging;

/// <summary>Serializes messages (events and commands) to/from the JSON payload we persist.</summary>
public interface IMessageSerializer
{
    string Serialize(object message);

    object Deserialize(string payload, Type type);
}

public sealed class MessageSerializer : IMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize(object message) =>
        JsonSerializer.Serialize(message, message.GetType(), Options);

    public object Deserialize(string payload, Type type) =>
        JsonSerializer.Deserialize(payload, type, Options)
        ?? throw new InvalidOperationException($"Failed to deserialize message of type '{type.FullName}'.");
}
