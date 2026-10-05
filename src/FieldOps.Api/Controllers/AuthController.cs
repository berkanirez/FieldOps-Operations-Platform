using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FieldOps.Api.Application;
using FieldOps.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using FieldOps.Modules.Employees;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace FieldOps.Api.Controllers;

// Day 93: the first real authentication concept in this codebase. No
// [Authorize] exists anywhere yet, and the existing X-Organization-Id/
// X-Employee-Id header mechanism (Day 40) is untouched — this endpoint only
// ADDS token issuance, it replaces nothing today. Angular's JWT interceptor
// and the actual [Authorize]-protected endpoints are later days' work.
//
// Day 121: the whole API now requires a token (fallback policy in
// Program.cs), so login itself must be reachable anonymously. The signing
// key comes from JwtSettings, validated once at startup — the duplicated
// public fallback key that lived here is gone (SECURITY_REVIEW.md F4).
// Still no password check: SECURITY_REVIEW.md F2, a later step.
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IEmployeeDirectory _employeeDirectory;
    private readonly JwtSettings _jwtSettings;
    private readonly EmployeePasswordHasher _passwordHasher;

    // Day 122: a valid hash of a random value, verified when the employee
    // doesn't exist so that path costs the same PBKDF2 work as a wrong
    // password — otherwise the response time alone would reveal which
    // employee ids exist (a timing side channel).
    private static readonly string TimingEqualizerHash = new EmployeePasswordHasher().Hash(Guid.NewGuid().ToString());

    public AuthController(IEmployeeDirectory employeeDirectory, JwtSettings jwtSettings, EmployeePasswordHasher passwordHasher)
    {
        _employeeDirectory = employeeDirectory;
        _jwtSettings = jwtSettings;
        _passwordHasher = passwordHasher;
    }

    // Day 122 (SECURITY_REVIEW.md F2 + F7): the password is verified against
    // the stored hash. An unknown employee and a wrong password get the SAME
    // 401 response (no "employee does not exist" — that enabled enumeration),
    // and login is rate limited per client ("Login" policy in Program.cs).
    [HttpPost("login")]
    [EnableRateLimiting("Login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var employee = await _employeeDirectory.GetByIdAsync(request.EmployeeId, cancellationToken);
        var passwordHash = employee is null ? null : await _employeeDirectory.GetPasswordHashAsync(employee.Id, cancellationToken);

        var passwordMatches = _passwordHasher.Verify(passwordHash ?? TimingEqualizerHash, request.Password);
        if (employee is null || passwordHash is null || !passwordMatches)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid employee id or password.");
        }

        var signingKey = _jwtSettings.SigningKey;
        var issuer = _jwtSettings.Issuer;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, employee.Id.ToString()),
            new Claim("organizationId", employee.OrganizationId.ToString()),
            new Claim(ClaimTypes.Role, employee.Role.ToString()),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new LoginResponse(tokenString, employee.Id, employee.OrganizationId, employee.Role.ToString()));
    }
}
