namespace SealedTransfer.Core.Identity;

/// <summary>
/// Ensures a <see cref="User"/> row exists for the signed-in Entra ID principal.
/// Called on every authenticated request (idempotent — only writes on first sign-in or profile change).
/// </summary>
public sealed class UserSyncService(IUserRepository repository, TimeProvider clock)
{
    /// <summary>
    /// Returns the local user for the given Entra ID claims, creating one if needed.
    /// Also auto-creates an organization from the tenant when a new user signs in.
    /// </summary>
    public async Task<User> SyncAsync(
        Guid objectId,
        string displayName,
        string email,
        Guid? tenantId,
        string? tenantName,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var user = await repository.FindAsync(objectId, cancellationToken);

        if (user is null)
        {
            user = User.Create(objectId, displayName, email, now);

            // Auto-provision an org from the Entra ID tenant if one doesn't exist yet.
            if (tenantId is { } tid)
            {
                var org = await repository.FindOrganizationByTenantAsync(tid, cancellationToken);
                if (org is null)
                {
                    org = Organization.Create(Guid.NewGuid(), tenantName ?? "My organization", tid, now);
                    await repository.AddOrganizationAsync(org, cancellationToken);
                    // First user in the org becomes admin.
                    user.JoinOrganization(org.Id, OrgRole.Admin);
                }
                else
                {
                    user.JoinOrganization(org.Id, OrgRole.Member);
                }
            }

            await repository.AddAsync(user, cancellationToken);
        }
        else
        {
            user.RecordSignIn(displayName, email, now);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return user;
    }
}
