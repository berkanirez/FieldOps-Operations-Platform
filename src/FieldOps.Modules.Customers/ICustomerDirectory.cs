namespace FieldOps.Modules.Customers;

public interface ICustomerDirectory
{
    IReadOnlyList<CustomerSummary> GetAll();
    CustomerSummary? GetById(int id);

    // Day 119: used by the async Create (customer check). Approve still uses
    // the synchronous GetById until its own conversion.
    Task<CustomerSummary?> GetByIdAsync(int id, CancellationToken cancellationToken);
}
