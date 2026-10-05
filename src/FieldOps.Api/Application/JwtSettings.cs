namespace FieldOps.Api.Application;

// Day 121: the signing key and issuer, read and validated ONCE in Program.cs
// (which refuses to start without a key) and shared with AuthController.
// Before, both files read configuration separately — each with its own copy
// of a public fallback key.
public record JwtSettings(string SigningKey, string Issuer);
