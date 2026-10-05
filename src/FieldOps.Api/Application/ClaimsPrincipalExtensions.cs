using System.Security.Claims;

namespace FieldOps.Api.Application;

// Day 121: the caller's identity, read from the validated JWT's claims
// (AuthController puts them there at login) instead of client-supplied
// X-Organization-Id / X-Employee-Id headers (SECURITY_REVIEW.md F1). Returns
// null when a claim is missing or malformed, so callers can reject the
// request rather than guess.
public static class ClaimsPrincipalExtensions
{
    public static int? GetEmployeeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int? GetOrganizationId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("organizationId"), out var id) ? id : null;
}
