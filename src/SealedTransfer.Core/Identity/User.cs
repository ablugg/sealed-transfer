namespace SealedTransfer.Core.Identity;

/// <summary>
/// A user synced from Entra ID on first sign-in. The server stores only the object id
/// and display info from the id token; it never holds passwords or secrets.
/// </summary>
public sealed class User
{
    private User()
    {
    }

    /// <summary>The Entra ID object id (oid claim). Stable across tenant operations.</summary>
    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSignInAt { get; private set; }

    /// <summary>The organization this user belongs to, if any.</summary>
    public Guid? OrganizationId { get; private set; }

    public Organization? Organization { get; private set; }

    public OrgRole Role { get; private set; }

    public static User Create(Guid entraObjectId, string displayName, string email, DateTimeOffset now) =>
        new()
        {
            Id = entraObjectId,
            DisplayName = displayName,
            Email = email,
            CreatedAt = now,
            LastSignInAt = now,
            Role = OrgRole.Member,
        };

    public void RecordSignIn(string displayName, string email, DateTimeOffset now)
    {
        DisplayName = displayName;
        Email = email;
        LastSignInAt = now;
    }

    public void JoinOrganization(Guid organizationId, OrgRole role)
    {
        OrganizationId = organizationId;
        Role = role;
    }

    public void LeaveOrganization()
    {
        OrganizationId = null;
        Role = OrgRole.Member;
    }

    public void ChangeRole(OrgRole role) => Role = role;
}
