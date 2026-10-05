using System.ComponentModel.DataAnnotations;

namespace FieldOps.Api.Models;

// Day 93: deliberately no password/credential field. Real authentication
// (hashed passwords, or a real identity provider) is out of today's scope —
// this endpoint only demonstrates token ISSUANCE for an employee that
// already exists, not proving who is actually making the request.
// Day 122 (SECURITY_REVIEW.md F2): the password is now required and
// verified against the stored hash.
public record LoginRequest([Required] int EmployeeId, [Required] string Password);
