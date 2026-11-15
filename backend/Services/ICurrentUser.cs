using ClinicalRag.Api.Models;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Resolves the current authenticated user from the request token.
/// Encapsulates the identity logic previously inline in WorkspacesController.
///
/// The token's NameIdentifier claim is the Firebase UID (an arbitrary string);
/// it maps to the Users table's Guid Id via <see cref="GetUserIdAsync"/>.
/// </summary>
public interface ICurrentUser
{
    string? FirebaseUid { get; }
    string Email { get; }
    bool IsAuthenticated { get; }

    /// <summary>
    /// Resolves the Users row from the token, creating it if it does not exist (dev convenience).
    /// </summary>
    Task<User> EnsureUserAsync();

    /// <summary>
    /// Resolves the Users.Id (Guid) for the current Firebase UID, or Guid.Empty if no matching row exists.
    /// </summary>
    Task<Guid> GetUserIdAsync();
}
