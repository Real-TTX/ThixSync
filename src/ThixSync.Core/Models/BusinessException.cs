namespace ThixSync.Core.Models;

/// <summary>
/// A rule of the business layer was not met (validation, permission, state). The message is German and meant for the
/// user; <see cref="Field"/> names the input it belongs to, so the UI can show it right there.
/// </summary>
public sealed class BusinessException(string message, string? field = null) : Exception(message)
{
    public string? Field { get; } = field;
}
