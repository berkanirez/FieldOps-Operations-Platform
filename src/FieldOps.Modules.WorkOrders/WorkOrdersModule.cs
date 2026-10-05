using FieldOps.Modules.WorkOrders.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Modules.WorkOrders;

public static class WorkOrdersModule
{
    public static IServiceCollection AddWorkOrdersModule(this IServiceCollection services, string connectionString)
    {
        // Day 111: transient-fault retries — see OrganizationsModule.
        services.AddDbContext<WorkOrdersDbContext>(options => options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        return services.AddScoped<IWorkOrderDirectory, EfWorkOrderDirectory>();
    }
}
