using SolaceSystems.Solclient.Messaging;
using SolaceSystems.Solclient.Messaging.SDT;

namespace SolaceConsumer;

/// <summary>
/// Reads the user properties an attachment message has to carry. A property that is not
/// there is a rejection naming it, which is what turns publisher/consumer drift into a
/// printed line instead of a file written from the wrong values.
/// </summary>
public static class RequiredProperties
{
    /// <summary>Reads a property that must be present, rejecting by name when it is not.</summary>
    public static string String(IMapContainer? properties, string key) =>
        OptionalString(properties, key)
        ?? throw new RejectedAttachmentException($"missing property '{key}'");

    /// <summary>
    /// Reads a property that may legitimately be absent, returning null when it is.
    ///
    /// Absence is detected through the exception the SDK raises for a key that was never set —
    /// measured against 10.30.0, not assumed: the typed getters throw
    /// <see cref="FieldNotFoundException"/> rather than returning a default. A key that *is* set,
    /// but to another type, is not absence: it is rejected, because a message carrying a numeric
    /// transfer-id is malformed and reading it as a whole file would write a chunk as one.
    /// </summary>
    public static string? OptionalString(IMapContainer? properties, string key)
    {
        if (properties is null)
        {
            return null;
        }

        ISDTField field;
        try
        {
            field = properties.GetField(key);
        }
        catch (FieldNotFoundException)
        {
            return null;
        }

        if (field.Type != SDTFieldType.STRING)
        {
            throw new RejectedAttachmentException($"property '{key}' is {field.Type}, not a string");
        }

        return field.Value is string { Length: > 0 } value ? value : null;
    }

    /// <summary>
    /// Reads a required numeric property.
    ///
    /// Presence is never inferred from the value. Callers reach here only after the transfer-id
    /// presence test has established that the message is a chunk, so a property that is missing,
    /// or set to a type this cannot read, means the message is malformed — and both are turned
    /// into a rejection here rather than a number quietly defaulting to zero.
    /// </summary>
    public static int Int32(IMapContainer? properties, string key) =>
        Read(properties, key, container => container.GetInt32(key));

    /// <inheritdoc cref="Int32"/>
    public static long Int64(IMapContainer? properties, string key) =>
        Read(properties, key, container => container.GetInt64(key));

    private static T Read<T>(IMapContainer? properties, string key, Func<IMapContainer, T> read)
    {
        try
        {
            return read(properties!);
        }
        catch (FieldNotFoundException)
        {
            throw new RejectedAttachmentException($"missing property '{key}'");
        }
        catch (Exception ex)
        {
            throw new RejectedAttachmentException($"unreadable property '{key}': {ex.GetType().Name}");
        }
    }
}
