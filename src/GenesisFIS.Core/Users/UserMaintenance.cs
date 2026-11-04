using GenesisFIS.Core.Security;

namespace GenesisFIS.Core.Users;

/// <summary>
/// Maintenance > Security > User Permission (legacy frmUserPermissionMaintenance, USRAccess 112): users (FWHSHISMaintenance.USR) with their department,
/// service provider, status, authorised flag and access string (USR.USRAccess, one '0'/'1' per legacy screen position).
/// </summary>
public sealed record UserSummary(string Code, string Name, string? LgCode, string? Group, string? ServiceProvider, string? Status, string? Authorised);

public sealed record UserDetail(
    string Code, string Name, string? LgCode, DateOnly? DateCreated, string? Status, string? Authorised, string? Group, string? ServiceProvider, string Access,
    bool MustChangePassword);

/// <summary>A user to add or update; a null or empty <see cref="Password"/> keeps the current one on update (it is required for a new user).</summary>
public sealed record UserSaveRequest(
    string Code, string? Name, string? LgCode, string? Password, string? Group, string? ServiceProvider, string? Status, string? Authorised, string? Access);

public sealed record UserOption(string Code, string Name);

/// <summary>One legacy check box: its access position, caption, the module it sits under and the menu item that checks it.</summary>
public sealed record AccessPosition(int Position, string Caption, string Module, string UsedBy);

public sealed record UserMaintenanceOptions(IReadOnlyList<UserOption> Groups, IReadOnlyList<UserOption> ServiceProviders, IReadOnlyList<AccessPosition> Positions);

public static class UserMaintenanceRules
{
    /// <summary>USR.USRPassword is nvarchar(10) and the legacy cipher keeps the length; legacy refused passwords shorter than 7 (its message said 8).</summary>
    public const int MinPassword = 8, MaxPassword = 10;

    /// <summary>The legacy cmdSave_Click checks, in its order, plus the column sizes.</summary>
    public static (string Field, string Message)? Validate(UserSaveRequest r, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(r.Code)) return ("code", "Please Enter User Code");
        if (r.Code.Trim().Length > 10) return ("code", "User Code is at most 10 characters.");
        if (string.IsNullOrWhiteSpace(r.Name)) return ("name", "Please Enter User Name");
        if (r.Name.Trim().Length > 60) return ("name", "User Name is at most 60 characters.");
        if (isNew && string.IsNullOrEmpty(r.Password)) return ("password", "Please Enter Password");
        if (string.IsNullOrWhiteSpace(r.Group)) return ("group", "Please Enter Department");
        if (string.IsNullOrWhiteSpace(r.ServiceProvider)) return ("serviceProvider", "Please Enter Service Provider");
        if (r.Status?.Trim() is not ("A" or "S")) return ("status", "Please Enter Status");
        if (r.Authorised?.Trim() is not ("Y" or "N")) return ("authorised", "Please Enter Authorised Status");
        if (!string.IsNullOrEmpty(r.Password) && r.Password.Length is < MinPassword or > MaxPassword)
            return ("password", $"Password must be {MinPassword} to {MaxPassword} characters.");
        if (r.LgCode?.Trim().Length > 10) return ("lgCode", "LG Code is at most 10 characters.");
        if (r.Access is { } a && a.Any(c => c is not ('0' or '1'))) return ("access", "Access must be a string of 0 and 1.");
        return null;
    }

    /// <summary>
    /// The access string to store: the legacy form wrote one flag per check box (178); flags beyond the requested ones are kept from the current value
    /// (some users have 180), so nothing the form does not show is lost.
    /// </summary>
    public static string MergeAccess(string? requested, string? current)
    {
        var flags = (requested ?? "").PadRight(Positions.Count, '0').ToCharArray();
        var tail = current is { Length: > 0 } c && c.Length > flags.Length ? c[flags.Length..] : "";
        return new string(flags) + tail;
    }

    /// <summary>The positions a user may open (for the page's counts).</summary>
    public static int Granted(string access) => Positions.Count(p => new AccessString(access).IsAllowed(p.Position));

    // Generated from the legacy frmUserPermissionMaintenance check boxes (ACS(1..178) captions) and the menu that checks each position.
    public static readonly IReadOnlyList<AccessPosition> Positions =
    [
        new(1, "Registration", "Membership", "Membership > Registration"),
        new(2, "Adjustment", "Membership", "Membership > Adjustment"),
        new(3, "Enquiry", "Membership", "Membership > Enquiry"),
        new(4, "Leaflet & Card", "Membership", "Membership > Leaflet Card"),
        new(5, "Deletion", "Membership", "Membership > Deletion"),
        new(6, "Tracking", "Membership", "Membership > Tracking"),
        new(7, "Tracking (Jupiter)", "Membership", "Membership > Tracking (Jupiter)"),
        new(8, "Upload Bordx", "Membership", "Membership > Upload Bordx"),
        new(9, "Generate ASCII", "Membership", "Membership > Generate Ascii"),
        new(10, "Upload Bordx by IC", "Other", ""),
        new(11, "Registration", "Case", "Case > Case Registration"),
        new(12, "Adjustment", "Case", "Case > Case Adjustment"),
        new(13, "Inquiry", "Case", "Case > Case Inquiry"),
        new(14, "Tracking", "Case", "Case > Case Tracking"),
        new(15, "Tracking (Jupiter)", "Case", "Case > Case Tracking (Jupiter)"),
        new(16, "Audit Review", "Case", "Case > Case Audit Review"),
        new(17, "LG Tracking", "Case", "Case > LG Tracking"),
        new(18, "Claims ASCII", "Case", "Case > Claim ASCII"),
        new(19, "Invoice", "MCOFees", "MCOFees > Receipt MCO/Others"),
        new(20, "Fee Listing", "MCOFees", "MCOFees > MCO Inv/Listing"),
        new(21, "Refund Listing", "MCOFees", "MCOFees > Refund Listing"),
        new(22, "Processing", "Invoice", "Invoice > Invoice Processing"),
        new(23, "MBM Accounting", "Invoice", "Invoice > Invoice MBM Accounting"),
        new(24, "HOS Accounting", "Invoice", "Invoice > Invoice HOS Accounting"),
        new(25, "HOS (DR to MBM)", "Invoice", "Invoice > Invoice HOS (DR to MBM)"),
        new(26, "HOS (CR to MBM)", "Invoice", "Invoice > Invoice HOS (CR to MBM)"),
        new(27, "Tracking", "Invoice", "Invoice > Invoice Tracking"),
        new(28, "Tracking (Jupiter)", "Invoice", "Invoice > Invoice Tracking (Jupiter)"),
        new(29, "Main DCN Checking", "Invoice", "Invoice > Main DCN Checking"),
        new(30, "Insert Worksheet", "Claim Bordx", "Claim Bordx > Worksheet > Insert Worksheet"),
        new(31, "Remove Worksheet", "Claim Bordx", "Claim Bordx > Worksheet > Remove Worksheet"),
        new(32, "Print Bordx", "Claim Bordx", "Claim Bordx > Print Bordx"),
        new(33, "Print Bordx Excess", "Claim Bordx", "Claim Bordx > Print Bordx Excess"),
        new(34, "Update Bordx", "Claim Bordx", "Claim Bordx > Update Bordx"),
        new(35, "Send to Payor", "Claim Bordx", "Claim Bordx > Bor Delivery > Bordx Send Date"),
        new(36, "Returned Fr Payor", "Claim Bordx", "Claim Bordx > Bor Delivery > Returned From Payor"),
        new(37, "Send to MNRB", "Claim Bordx", "Claim Bordx > Bor Delivery > Send to MNRB"),
        new(38, "Send to Accounts", "Claim Bordx", "Claim Bordx > Bor Delivery > Send to A/C"),
        new(39, "A/C Period", "Claim Bordx", "Claim Bordx > Bor Acc Period"),
        new(40, "Bordx Control", "Claim Bordx", "Claim Bordx > Monitoring > Bordx Control > Normal"),
        new(41, "Outstanding Bordx", "Claim Bordx", "Claim Bordx > Monitoring > Outstanding Bordx"),
        new(42, "Bordx & Worksheet", "Claim Bordx", "Claim Bordx > Monitoring > Bordx Worksheet"),
        new(43, "Bordx Tracking", "Claim Bordx", "Claim Bordx > Bor Tracking > Bordx Tracking"),
        new(44, "CLM Tracking 1", "Claim", "Claim > Claim Tracking (Sat.) > Claim Report 1"),
        new(45, "CLM Tracking 2", "Claim", "Claim > Claim Tracking (Sat.) > Claim Report 2 > Actual"),
        new(46, "CLM Tracking 3", "Claim", "Claim > Claim Tracking (Sat.) > Claim Report 3"),
        new(47, "CLM Tracking 4", "Claim", "Claim > Claim Tracking (Sat.) > Claim Report 4"),
        new(48, "Storage Entry", "Claim Bordx", "Claim Bordx > Storage > Claim files"),
        new(49, "Storage Enquiry", "Claim Bordx", "Claim Bordx > Storage > Enquiry"),
        new(50, "Tracking 1 (Jup.)", "Claim", "Claim > Claim Tracking (Jup.) > Claim Report 1"),
        new(51, "Tracking 2 (Jup.)", "Claim", "Claim > Claim Tracking (Jup.) > Claim Report 2"),
        new(52, "Tracking 3 (Jup.)", "Claim", "Claim > Claim Tracking (Jup.) > Claim Report 3"),
        new(53, "Tracking 4 (Jup.)", "Claim", "Claim > Claim Tracking (Jup.) > Claim Report 4"),
        new(54, "Date Selection", "Statistic", "Statistic > Date Selection"),
        new(55, "Payment to HOS", "Payment", "Payment > To Hospital"),
        new(56, "Payment to MBM", "Payment", "Payment > To Member > Pay2MBM(Auto)"),
        new(57, "Update Invoice", "Payment", "Payment > Update Invoice"),
        new(58, "Payment Tracking", "Payment", "Payment > Payment Tracking"),
        new(59, "Receipt fr MBM", "Worksheet", "Worksheet > Claim Write Off"),
        new(60, "Receipt fr Others", "Receipt", "Receipt > Others"),
        new(61, "Receipt fr Payor", "Receipt", "Receipt > Payor"),
        new(62, "Receipt fr MNRB", "Receipt", "Receipt > MNRB"),
        new(63, "Receipt Tracking", "Receipt", "Receipt > Receipt Tracking > From Member"),
        new(64, "Registration", "Worksheet", "Worksheet > Worksheet Registration"),
        new(65, "Adjustment", "Worksheet", "Worksheet > WorkSheet Adjustment"),
        new(66, "Send to A/C", "Worksheet", "Worksheet > Worksheet Send to A/C"),
        new(67, "Tracking", "Worksheet", "Worksheet > Worksheet Tracking"),
        new(68, "Tracking (Jupiter)", "Worksheet", "Worksheet > Worksheet Tracking (Jupiter)"),
        new(69, "Access", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(70, "Hospital Info", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(71, "Clinic Info", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(72, "Payor Info", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(73, "Payor Branch", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(74, "Service Provider", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(75, "Hospital Group", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(76, "Clinic Group", "Maintenance", "Maintenance > Entity > Group Company (Employer)"),
        new(77, "Payor Group", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(78, "Agent", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(79, "Special Approval", "Maintenance", "Maintenance > Entity > Other Entity Tabs"),
        new(80, "Health Type", "Maintenance", "Maintenance > Plan > Health Type"),
        new(81, "Insured Type", "Maintenance", "Maintenance > Plan > Insured Type"),
        new(82, "Plan Code", "Maintenance", "Maintenance > Plan > Plan Code / Special Note"),
        new(83, "Premium", "Maintenance", "Maintenance > Plan > Premium"),
        new(84, "Annual Limit", "Maintenance", "Maintenance > Plan > Annual Limit"),
        new(85, "MCO Fee", "Maintenance", "Maintenance > Plan > MCO Fee"),
        new(86, "Category", "Maintenance", "Maintenance > Benefits > Category"),
        new(87, "Sub Category", "Maintenance", "Maintenance > Benefits > Sub Category"),
        new(88, "Benefit Entry", "Maintenance", "Maintenance > Benefits > Benefit Entry"),
        new(89, "Benefit Listing", "Maintenance", "Maintenance > Benefits > Benefits Listing"),
        new(90, "ICD Selection", "Maintenance", "Maintenance > Case > ICD Selection"),
        new(91, "ICD Maintenance", "Maintenance", "Maintenance > Case > ICD Maintenance"),
        new(92, "ICD Category", "Maintenance", "Maintenance > Case > ICD Category"),
        new(93, "Repudiation Code", "Maintenance", "Maintenance > Case > Repudiation Code"),
        new(94, "Repudiation Category", "Maintenance", "Maintenance > Case > Repudiation Category"),
        new(95, "Nature of Claim", "Maintenance", "Maintenance > Case > Nature of Claim"),
        new(96, "Treatment Modality", "Maintenance", "Maintenance > Case > Treatment Modality"),
        new(97, "Hospitalisation Type", "Maintenance", "Maintenance > Case > Hospitalisation Type"),
        new(98, "Age Band", "Maintenance", "Maintenance > Member > CPT Codes"),
        new(99, "Occupation", "Maintenance", "Maintenance > Member > Occupation"),
        new(100, "Relationship", "Maintenance", "Maintenance > Member > Relationship"),
        new(101, "Nationality", "Maintenance", "Maintenance > Member > Nationality"),
        new(102, "Race", "Maintenance", "Maintenance > Member > Race"),
        new(103, "Language", "Maintenance", "Maintenance > Member > Language"),
        new(104, "City", "Maintenance", "Maintenance > Member > City"),
        new(105, "State", "Maintenance", "Maintenance > Member > State"),
        new(106, "Work Nature", "Maintenance", "Maintenance > Member > Work Nature"),
        new(107, "Endorsement Status", "Maintenance", "Maintenance > Member > Endorsement Status"),
        new(108, "Policy Status", "Maintenance", "Maintenance > Member > Policy Status"),
        new(109, "Valid Date Range", "Maintenance", "Maintenance > Date"),
        new(110, "Suspension", "Maintenance", "Maintenance > Suspension > Suspension"),
        new(111, "Department", "Maintenance", "Maintenance > Security > Department"),
        new(112, "User Permission", "Maintenance", "Maintenance > Security > User Permission"),
        new(113, "Enquiry", "Access Control (CIS)", ""),
        new(114, "Tracking", "Access Control (CIS)", ""),
        new(115, "MCO Invoice", "Access Control (CIS)", ""),
        new(116, "Fee Listing", "Access Control (CIS)", ""),
        new(117, "Clinic", "Access Control (CIS)", ""),
        new(118, "Member", "Access Control (CIS)", ""),
        new(119, "Adjustment", "Access Control (CIS)", ""),
        new(120, "Claim Tracking 2", "Access Control (CIS)", ""),
        new(121, "To Clinic - Batch", "Access Control (CIS)", ""),
        new(122, "Insert Worksheet", "Access Control (CIS)", ""),
        new(123, "Remove Worksheet", "Access Control (CIS)", ""),
        new(124, "Print Bordx", "Access Control (CIS)", ""),
        new(125, "Send to Payor", "Access Control (CIS)", ""),
        new(126, "Returned Fr Payor", "Access Control (CIS)", ""),
        new(127, "Send to Accounts", "Access Control (CIS)", ""),
        new(128, "A/C Period", "Access Control (CIS)", ""),
        new(129, "Bordx Control", "Access Control (CIS)", ""),
        new(130, "Outstanding Bordx", "Access Control (CIS)", ""),
        new(131, "Bordx & Worksheet", "Access Control (CIS)", ""),
        new(132, "Claim Tracking", "Access Control (CIS)", ""),
        new(133, "To Clinic", "Access Control (CIS)", ""),
        new(134, "To Member", "Access Control (CIS)", ""),
        new(135, "Fr Member", "Access Control (CIS)", ""),
        new(136, "Fr Payor", "Access Control (CIS)", ""),
        new(137, "Entity", "Maintenance", ""),
        new(138, "Plan", "Maintenance", ""),
        new(139, "Case", "Maintenance", ""),
        new(140, "Calculation", "Maintenance", ""),
        new(141, "Benefit - Member", "Maintenance", ""),
        new(142, "Benefit - Clinic", "Maintenance", ""),
        new(143, "Check Item", "Maintenance", ""),
        new(144, "Clinic", "Access Control (CIS)", ""),
        new(145, "Enq -  Pay2Hos", "Payment", "Payment > Enquiry > Payment To Hospital"),
        new(146, "Enq -  Pay2MBM", "Payment", "Payment > Enquiry > Payment To Member"),
        new(147, "MMA", "Maintenance", "Maintenance > MMA > Procedures"),
        new(148, "Invoice Manual Upd", "MCOFees", "MCOFees > Invoice Manual Upd"),
        new(149, "NIAM Report", "Membership", "Membership > NIAM Report"),
        new(150, "MMA Fees", "Case", "Case > MMA/CPT"),
        new(151, "Storage Rep Case", "Claim Bordx", "Claim Bordx > Storage > Repudiated Case"),
        new(152, "Storage Rep Enq", "Claim Bordx", "Claim Bordx > Storage > Repudiated Enquiry"),
        new(153, "To Member - Batch", "Access Control (CIS)", ""),
        new(154, "Send to Claims", "Case", "Case > Send to Bordx Unit"),
        new(155, "DCU Report 1", "Claim", "Claim > DCU Tracking > Claim Report"),
        new(156, "DCU Report 2", "Claim", "Claim > DCU Tracking > Claim Report (Jup.)"),
        new(157, "Trust Account", "Payment", "Payment > Reconciliation"),
        new(158, "Listing", "Access Control (CIS)", ""),
        new(159, "Tracking", "Tracking (other databases)", ""),
        new(160, "Tracking (Jupiter)", "Tracking (other databases)", ""),
        new(161, "Tracking", "Tracking (other databases)", ""),
        new(162, "Tracking (Jupiter)", "Tracking (other databases)", ""),
        new(163, "Claim Tracking (Jupiter)", "Tracking (other databases)", ""),
        new(164, "Claim Tracking 2 (Jupiter)", "Tracking (other databases)", ""),
        new(165, "Tracking", "Tracking (other databases)", ""),
        new(166, "Claim Tracking (Jupiter)", "Tracking (other databases)", ""),
        new(167, "Tracking (Jupiter)", "Tracking (other databases)", ""),
        new(168, "Data Transfer", "Maintenance", "Maintenance > Data Transfer > Member"),
        new(169, "Invoice - HOS", "A/C Export", "A/C Export > Invoice > Hospital"),
        new(170, "Invoice - MBM", "A/C Export", "A/C Export > Invoice > Member"),
        new(171, "Receipt - MNRB", "A/C Export", "A/C Export > Receipt > MNRB"),
        new(172, "Receipt - Payor", "A/C Export", "A/C Export > Receipt > Payor"),
        new(173, "Payment - HOS", "A/C Export", "A/C Export > Payment > Hospital"),
        new(174, "Payment - MBM", "A/C Export", "A/C Export > Payment > Member"),
        new(175, "Bordx - MNRB", "A/C Export", "A/C Export > Bordx > MNRB"),
        new(176, "Bordx - Payor", "A/C Export", "A/C Export > Bordx > Payor"),
        new(177, "Sales - Invoice", "A/C Export", "A/C Export > Sales > Invoice"),
        new(178, "Print Payment", "Other", ""),
    ];
}

public interface IUserMaintenanceRepository
{
    Task<UserMaintenanceOptions> OptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken ct = default);
    Task<UserDetail?> GetAsync(string code, CancellationToken ct = default);
    Task<UserDetail> SaveAsync(UserSaveRequest request, bool isNew, string user, CancellationToken ct = default);
    Task DeleteAsync(string code, string user, CancellationToken ct = default);
}
