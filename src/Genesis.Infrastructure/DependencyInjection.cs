using Genesis.Core.Data;
using Genesis.Core.Hospitals;
using Genesis.Core.Users;
using Genesis.Infrastructure.Data;
using Genesis.Infrastructure.Hospitals;
using Genesis.Infrastructure.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Genesis.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IHospitalRepository, HospitalRepository>();
        return services;
    }
}
