using System.ComponentModel.DataAnnotations;

namespace FieldOps.Api.Models;

// OrganizationId deliberately removed (Day 35) — which organization an
// employee belongs to is no longer something the client gets to state in
// the request body. It comes from the X-Organization-Id header instead
// (see EmployeesController), the same way a real system would derive it
// from an authenticated identity rather than trusting client-supplied data.
//
// Day 122: the new employee's initial password (at least 12 characters —
// length is the strongest simple rule; no complexity rules yet). Only its
// hash is stored.
public record CreateEmployeeRequest(
    [Required, StringLength(200)] string Name,
    [Required, StringLength(128, MinimumLength = 12)] string Password);
