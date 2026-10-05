using FieldOps.Api.Application;
using FieldOps.Api.Models;
using FieldOps.Modules.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrganizationsController : ControllerBase
{
    private readonly IOrganizationDirectory _organizationDirectory;

    public OrganizationsController(IOrganizationDirectory organizationDirectory)
    {
        _organizationDirectory = organizationDirectory;
    }

    // Day 123 (SECURITY_REVIEW.md F13, found while closing Week 21): this used
    // to list EVERY organization to any caller — in a multi-tenant SaaS, one
    // customer company could read the names of all the others. A caller now
    // sees only the organization in their validated token. The list shape is
    // kept (a one-element array) so existing clients don't break.
    [HttpGet]
    public ActionResult<IReadOnlyList<OrganizationDto>> GetAll()
    {
        // Mapping the module's own public shape (OrganizationSummary) into
        // this API's own HTTP-facing DTO — two separate boundaries (module
        // contract vs. HTTP contract) kept explicit even though they happen
        // to look identical today, the same discipline StockPilot applied
        // between its store layer and its HTTP DTOs.
        var organizationId = User.GetOrganizationId();
        var organization = organizationId is null ? null : _organizationDirectory.GetById(organizationId.Value);
        IReadOnlyList<OrganizationDto> dtos = organization is null
            ? []
            : [new OrganizationDto(organization.Id, organization.Name)];
        return Ok(dtos);
    }

    // Day 123: another organization's id returns 404, not 403 — the same
    // "don't confirm it exists" rule as work orders (Day 115).
    [HttpGet("{id}")]
    public ActionResult<OrganizationDto> GetById(int id)
    {
        if (id != User.GetOrganizationId())
        {
            return NotFound();
        }

        var organization = _organizationDirectory.GetById(id);
        if (organization == null)
        {
            return NotFound();
        }

        return Ok(new OrganizationDto(organization.Id, organization.Name));
    }
}
