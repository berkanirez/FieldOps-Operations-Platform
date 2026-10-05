using Microsoft.AspNetCore.Identity;

namespace FieldOps.Api.Application;

// Day 122 (SECURITY_REVIEW.md F2): employee passwords are hashed with ASP.NET
// Core Identity's PasswordHasher — PBKDF2 with a random per-password salt and
// many iterations, deliberately slow so guessing is expensive. It ships in
// the ASP.NET Core shared framework (no extra package). Wrapped here because
// its TUser type parameter plays no role in this use: callers just "hash"
// and "verify". Writing our own hashing (e.g. a single fast SHA-256) would be
// exactly the kind of crypto mistake this class exists to avoid.
public class EmployeePasswordHasher
{
    private static readonly object NoUser = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    // SuccessRehashNeeded (the hash used older parameters) still means the
    // password is correct.
    public bool Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(NoUser, passwordHash, password) != PasswordVerificationResult.Failed;
}
