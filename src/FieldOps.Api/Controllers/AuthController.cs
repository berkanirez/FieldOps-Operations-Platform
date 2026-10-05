using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FieldOps.Api.Application;
using FieldOps.Api.Models;
using Microsoft.AspNetCore.Authorization;
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

    public AuthController(IEmployeeDirectory employeeDirectory, JwtSettings jwtSettings)
    {
        _employeeDirectory = employeeDirectory;
        _jwtSettings = jwtSettings;
    }

    [HttpPost("login")]
    public ActionResult<LoginResponse> Login(LoginRequest request)
    {
        // Day 93's one real simplification, stated plainly: no password or
        // any other credential is checked here — knowing a valid EmployeeId
        // is treated as sufficient "proof" for this demo. A real login would
        // verify a hashed password (or delegate to a real identity provider)
        // before ever reaching the point of issuing a token.
        var employee = _employeeDirectory.GetById(request.EmployeeId);
        if (employee is null)
        {
            return NotFound($"Employee {request.EmployeeId} does not exist.");
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
