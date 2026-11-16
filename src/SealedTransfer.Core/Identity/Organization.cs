namespace SealedTransfer.Core.Identity;

/// <summary>
/// A group of users who share transfer visibility and admin controls.
/// Created by the first admin or provisioned from Entra ID tenant info.
/// </summary>
public sealed class Organization
{
    private readonly List<User> _members = [];

    private Organization()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The Entra ID tenant id, if this org maps to a tenant.</summary>
    public Guid? TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<User> Members => _members.AsReadOnly();

    public static Organization Create(Guid id, string name, Guid? tenantId, DateTimeOffset now) =>
        new()
        {
            Id = id,
            Name = name,
            TenantId = tenantId,
            CreatedAt = now,
        };

    public void Rename(string name) => Name = name;
}
