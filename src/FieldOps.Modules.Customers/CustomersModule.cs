using FieldOps.Modules.Customers.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Modules.Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, string connectionString)
    {
        // Day 111: transient-fault retries — see OrganizationsModule.
        services.AddDbContext<CustomersDbContext>(options => options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        return services.AddScoped<ICustomerDirectory, EfCustomerDirectory>();
    }
}
