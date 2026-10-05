using FieldOps.Modules.AuditLogs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Modules.AuditLogs;

public static class AuditLogsModule
{
    public static IServiceCollection AddAuditLogsModule(this IServiceCollection services, string connectionString)
    {
        // Day 111: transient-fault retries — see OrganizationsModule.
        services.AddDbContext<AuditLogsDbContext>(options => options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        return services.AddScoped<IAuditLogWriter, EfAuditLogWriter>();
    }
}
