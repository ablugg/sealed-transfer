namespace SealedTransfer.Core.Identity;

public interface IUserRepository
{
    Task<User?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task AddAsync(User user, CancellationToken cancellationToken);

    Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken cancellationToken);

    Task<Organization?> FindOrganizationByTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddOrganizationAsync(Organization organization, CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> ListOrganizationMembersAsync(Guid organizationId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
