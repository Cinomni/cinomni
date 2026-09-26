namespace Cinomni.Operations.Messaging;

/// <summary>A mapping between a stable message name and its CLR type, registered at startup.</summary>
public sealed record MessageTypeRegistration(string Name, Type Type);

/// <summary>
/// Resolves between the stable name persisted for a message (event or command) and its CLR
/// type. Using an explicit registered name — not the .NET type name — keeps messages
/// rename-safe: a persisted message whose discriminator were the class name, resolved by
/// reflection, would stop deserializing as soon as the type was renamed or moved.
/// </summary>
public interface IMessageTypeRegistry
{
    string GetName(Type type);

    Type Resolve(string name);
}

public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly Dictionary<string, Type> _byName;
    private readonly Dictionary<Type, string> _byType;

    public MessageTypeRegistry(IEnumerable<MessageTypeRegistration> registrations)
    {
        _byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        _byType = new Dictionary<Type, string>();

        foreach (var (name, type) in registrations)
        {
            if (!_byName.TryAdd(name, type))
            {
                throw new InvalidOperationException($"Duplicate message name '{name}'.");
            }

            _byType[type] = name;
        }
    }

    public string GetName(Type type) =>
        _byType.TryGetValue(type, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Message type '{type.FullName}' is not registered. Register it at startup (AddIntegrationEvent/AddCommand).");

    public Type Resolve(string name) =>
        _byName.TryGetValue(name, out var type)
            ? type
            : throw new InvalidOperationException($"Unknown message name '{name}'.");
}
