using Genesis.Core.Cases;
using Genesis.Core.Data;
using Genesis.Core.Hospitals;
using Genesis.Core.Fees;
using Genesis.Core.Maintenance;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Core.Tracking;
using Genesis.Core.Users;
using Genesis.Infrastructure.Cases;
using Genesis.Infrastructure.Data;
using Genesis.Infrastructure.Hospitals;
using Genesis.Infrastructure.Fees;
using Genesis.Infrastructure.Maintenance;
using Genesis.Infrastructure.Membership;
using Genesis.Infrastructure.Writes;
using Genesis.Infrastructure.Tracking;
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
        services.AddScoped<IMembershipEnquiryRepository, MembershipEnquiryRepository>();
        services.AddScoped<IMembershipTabsRepository, MembershipTabsRepository>();
        services.AddSingleton<IPolicyWordingStore, FileSystemPolicyWordingStore>();
        services.AddScoped<ICaseInquiryRepository, CaseInquiryRepository>();
        services.AddScoped<ITrackingService, TrackingService>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddScoped<IPanelHospitalRepository, PanelHospitalRepository>();
        services.AddScoped<ILookupRepository, LookupRepository>();
        services.AddScoped<ISuspensionRepository, SuspensionRepository>();
        services.AddScoped<ICardSendingRepository, CardSendingRepository>();
        services.AddScoped<IMcoPendingRepository, McoPendingRepository>();
        services.AddScoped<IMemberDeletionRepository, MemberDeletionRepository>();
        services.AddScoped<IFeeRateSource, FeeRateSource>();
        services.AddScoped<FeeService>();
        services.AddScoped<IReinstatementRepository, ReinstatementRepository>();
        services.AddScoped<IAdjustmentRepository, AdjustmentRepository>();
        services.AddScoped<ICancellationRepository, CancellationRepository>();
        services.AddScoped<IPlanChangeRepository, PlanChangeRepository>();
        services.AddScoped<IStatusEndorsementRepository, StatusEndorsementRepository>();
        services.AddScoped<ISupplementaryRepository, SupplementaryRepository>();
        services.AddScoped<ISupplementaryCancelRepository, SupplementaryCancelRepository>();
        services.AddScoped<ISupplementaryDeleteRepository, SupplementaryDeleteRepository>();
        services.AddScoped<IRegistrationRepository, RegistrationRepository>();
        services.AddScoped<IValidDateRangeRepository, ValidDateRangeRepository>();
        services.AddScoped<ISummaryService, SummaryService>();
        services.AddSingleton<ICaseDocumentStore, FileSystemCaseDocumentStore>();
        return services;
    }
}





















