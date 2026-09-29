namespace SolaceConsumer;

/// <summary>
/// A message the consumer refuses on its own merits — an unusable filename, a missing
/// property, a hash that does not match. Terminal: the message is acked and logged
/// rather than retried, because retrying would never change the outcome.
/// </summary>
public sealed class RejectedAttachmentException(string message) : Exception(message);
