using Microsoft.EntityFrameworkCore;
using SealedTransfer.Core.Identity;

namespace SealedTransfer.Infrastructure.Persistence;

public sealed class EfUserRepository(SealedTransferDbContext db) : IUserRepository
{
    public async Task<User?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.FindAsync([id], cancellationToken);

    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken) =>
        await db.Users.AddAsync(user, cancellationToken);

    public async Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Organizations.FindAsync([id], cancellationToken);

    public async Task<Organization?> FindOrganizationByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Organizations.FirstOrDefaultAsync(o => o.TenantId == tenantId, cancellationToken);

    public async Task AddOrganizationAsync(Organization organization, CancellationToken cancellationToken) =>
        await db.Organizations.AddAsync(organization, cancellationToken);

    public async Task<IReadOnlyList<User>> ListOrganizationMembersAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.Users.Where(u => u.OrganizationId == organizationId).OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await db.SaveChangesAsync(cancellationToken);
}
