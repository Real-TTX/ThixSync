using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

// PLACEHOLDER – replaced by the OAuth implementation.
public static partial class OAuthService
{
    public static partial bool IsConfigured(ConnectionKind kind) => false;

    public static partial Task<string> SignInAsync(Guid connectionId, CancellationToken cancellationToken) =>
        throw new BusinessException("Die Anmeldung bei Cloud-Speichern ist noch nicht verfügbar.");
}
