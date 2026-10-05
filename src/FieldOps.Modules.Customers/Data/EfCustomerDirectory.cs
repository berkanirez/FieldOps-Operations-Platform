using Microsoft.EntityFrameworkCore;

namespace FieldOps.Modules.Customers.Data;

// internal, replacing InMemoryCustomerDirectory (Day 47) as
// ICustomerDirectory's real implementation. No Create yet — same
// deliberate scope limit as before.
internal class EfCustomerDirectory : ICustomerDirectory
{
    private readonly CustomersDbContext _dbContext;

    public EfCustomerDirectory(CustomersDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IReadOnlyList<CustomerSummary> GetAll()
    {
        return _dbContext.Customers
            .Select(c => new CustomerSummary(c.Id, c.Name, c.OrganizationId))
            .ToList();
    }

    public CustomerSummary? GetById(int id)
    {
        var customer = _dbContext.Customers.FirstOrDefault(c => c.Id == id);
        return customer is null ? null : new CustomerSummary(customer.Id, customer.Name, customer.OrganizationId);
    }

    public async Task<CustomerSummary?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return customer is null ? null : new CustomerSummary(customer.Id, customer.Name, customer.OrganizationId);
    }
}
