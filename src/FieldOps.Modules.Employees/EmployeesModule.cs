using FieldOps.Modules.Employees.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Modules.Employees;

// Day 48/ADR 0003: same shape as OrganizationsModule — the host hands over
// a connection string, never sees EmployeesDbContext or EfEmployeeDirectory.
public static class EmployeesModule
{
    public static IServiceCollection AddEmployeesModule(this IServiceCollection services, string connectionString)
    {
        // Day 111: transient-fault retries — see OrganizationsModule.
        services.AddDbContext<EmployeesDbContext>(options => options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        return services.AddScoped<IEmployeeDirectory, EfEmployeeDirectory>();
    }
}
