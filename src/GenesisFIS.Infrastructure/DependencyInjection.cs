using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Hospitals;
using GenesisFIS.Core.Fees;
using GenesisFIS.Core.Maintenance;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Payments;
using GenesisFIS.Core.Posting;
using GenesisFIS.Core.Writes;
using GenesisFIS.Core.Tracking;
using GenesisFIS.Core.Users;
using GenesisFIS.Infrastructure.Cases;
using GenesisFIS.Infrastructure.Data;
using GenesisFIS.Infrastructure.Hospitals;
using GenesisFIS.Infrastructure.Fees;
using GenesisFIS.Infrastructure.Maintenance;
using GenesisFIS.Infrastructure.Membership;
using GenesisFIS.Infrastructure.Bordereaux;
using GenesisFIS.Infrastructure.Payments;
using GenesisFIS.Infrastructure.Posting;
using GenesisFIS.Infrastructure.Writes;
using GenesisFIS.Infrastructure.Tracking;
using GenesisFIS.Infrastructure.Users;
using Microsoft.Extensions.DependencyInjection;

namespace GenesisFIS.Infrastructure;

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
        services.AddScoped<ICaseRegistrationRepository, CaseRegistrationRepository>();
        services.AddScoped<ICaseAdjustmentRepository, CaseAdjustmentRepository>();
        services.AddScoped<ICaseAdjustmentTabsRepository, CaseAdjustmentTabsRepository>();
        services.AddScoped<ICaseInvoiceRepository, CaseInvoiceRepository>();
        services.AddScoped<IWorksheetRepository, WorksheetRepository>();
        services.AddScoped<ICaseTransferRepository, CaseTransferRepository>();
        services.AddScoped<ILifetimeLimitRepository, LifetimeLimitRepository>();
        services.AddScoped<ICaseAuditReviewRepository, CaseAuditReviewRepository>();
        services.AddScoped<ICaseSendToBordxRepository, CaseSendToBordxRepository>();
        services.AddScoped<ICasePayClaimRepository, CasePayClaimRepository>();
        services.AddScoped<ICaseNonDisclosureRepository, CaseNonDisclosureRepository>();
        services.AddScoped<ICaseSendToClaimsRepRepository, CaseSendToClaimsRepRepository>();
        services.AddScoped<ICaseCruSendToClaimsRepository, CaseCruSendToClaimsRepository>();
        services.AddScoped<ICaseAsciiRepository, CaseAsciiRepository>();
        services.AddScoped<ITrackingService, TrackingService>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddScoped<IPanelHospitalRepository, PanelHospitalRepository>();
        services.AddScoped<ILookupRepository, LookupRepository>();
        services.AddScoped<ISuspensionRepository, SuspensionRepository>();
        services.AddScoped<ICardSendingRepository, CardSendingRepository>();
        services.AddScoped<ILeafletRepository, LeafletRepository>();
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
        services.AddScoped<IBenefitRepository, BenefitRepository>();
        services.AddScoped<IIcdSelectionRepository, IcdSelectionRepository>();
        services.AddScoped<IMcoRefundRepository, McoRefundRepository>();
        services.AddScoped<IFwhsInvoiceRepository, FwhsInvoiceRepository>();
        services.AddScoped<IBordxUploadRepository, BordxUploadRepository>();
        services.AddScoped<IAccountPostingRepository, AccountPostingRepository>();
        services.AddScoped<IPayorReceiptRepository, PayorReceiptRepository>();
        services.AddScoped<IHospitalPaymentRepository, HospitalPaymentRepository>();
        services.AddScoped<IPaymentVoucherRepository, PaymentVoucherRepository>();
        services.AddScoped<IPaymentTrackingRepository, PaymentTrackingRepository>();
        services.AddScoped<IBordxCreationRepository, BordxCreationRepository>();
        services.AddScoped<IBordxPrintRepository, BordxPrintRepository>();
        services.AddScoped<ICreditDebitNoteRepository, CreditDebitNoteRepository>();
        services.AddScoped<IUserMaintenanceRepository, UserMaintenanceRepository>();
        services.AddScoped<ISppaClaimExportRepository, SppaClaimExportRepository>();
        services.AddScoped<ISppaClaimUploadRepository, SppaClaimUploadRepository>();
        services.AddScoped<IMcoManualInvoiceRepository, McoManualInvoiceRepository>();
        services.AddScoped<IMcoCorrectionRepository, McoCorrectionRepository>();
        services.AddScoped<IMcoReceiptRepository, McoReceiptRepository>();
        services.AddScoped<IMcoReinstatementRepository, McoReinstatementRepository>();
        services.AddScoped<IMcoDateCorrectionRepository, McoDateCorrectionRepository>();
        services.AddScoped<IValidDateRangeRepository, ValidDateRangeRepository>();
        services.AddScoped<ISummaryService, SummaryService>();
        services.AddSingleton<ICaseDocumentStore, FileSystemCaseDocumentStore>();
        return services;
    }
}


































