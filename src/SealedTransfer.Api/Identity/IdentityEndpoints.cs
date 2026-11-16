using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SealedTransfer.Core.Identity;

namespace SealedTransfer.Api.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/identity").WithTags("Identity");

        group.MapGet("/me", GetMeAsync).RequireAuthorization();
        group.MapGet("/org/members", GetOrgMembersAsync).RequireAuthorization();

        return routes;
    }

    /// <summary>
    /// Returns the current user's profile. Creates or updates the local user record
    /// from the Auth0 token claims on every call.
    /// </summary>
    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal,
        UserSyncService userSync,
        CancellationToken cancellationToken)
    {
        var parsed = ParseClaims(principal);
        if (parsed is null)
        {
            return Results.Problem("Token is missing required claims.", statusCode: 401);
        }

        var (sub, displayName, email) = parsed.Value;

        // Auth0 sub is "provider|id" — we derive a stable GUID from it.
        var objectId = DeriveGuid(sub);

        var user = await userSync.SyncAsync(objectId, displayName, email, tenantId: null, tenantName: null, cancellationToken);

        return Results.Ok(new MeResponse(
            user.Id,
            user.DisplayName,
            user.Email,
            user.OrganizationId,
            user.Organization?.Name,
            user.Role.ToString()));
    }

    private static async Task<IResult> GetOrgMembersAsync(
        ClaimsPrincipal principal,
        IUserRepository repository,
        CancellationToken cancellationToken)
    {
        var parsed = ParseClaims(principal);
        if (parsed is null)
        {
            return Results.Problem("Token is missing required claims.", statusCode: 401);
        }

        var objectId = DeriveGuid(parsed.Value.Sub);
        var user = await repository.FindAsync(objectId, cancellationToken);
        if (user?.OrganizationId is null)
        {
            return Results.Ok(Array.Empty<OrgMemberResponse>());
        }

        if (user.Role != OrgRole.Admin)
        {
            return Results.Forbid();
        }

        var members = await repository.ListOrganizationMembersAsync(user.OrganizationId.Value, cancellationToken);
        return Results.Ok(members.Select(m => new OrgMemberResponse(m.Id, m.DisplayName, m.Email, m.Role.ToString())));
    }

    private static (string Sub, string DisplayName, string Email)? ParseClaims(ClaimsPrincipal principal)
    {
        // Auth0 puts the user id in the "sub" claim (e.g. "auth0|abc123" or "google-oauth2|123").
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
        {
            return null;
        }

        var displayName = principal.FindFirstValue("name")
                          ?? principal.FindFirstValue("nickname")
                          ?? "Unknown";

        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email")
                    ?? "unknown@unknown.com";

        return (sub, displayName, email);
    }

    /// <summary>Derives a deterministic GUID from the Auth0 sub claim.</summary>
    private static Guid DeriveGuid(string sub)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sub));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string Email,
    Guid? OrganizationId,
    string? OrganizationName,
    string Role);

public sealed record OrgMemberResponse(
    Guid Id,
    string DisplayName,
    string Email,
    string Role);
