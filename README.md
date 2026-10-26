# Genesis API

ASP.NET Core (.NET 10) API for the Genesis FIS migration. Talks to the existing SQL Server databases unchanged.

## Layout
| Project | Purpose |
|---|---|
| `src/Genesis.Api` | HTTP host. One folder per feature under `Features/` (endpoints + request/response models). |
| `src/Genesis.Core` | Models, repository interfaces, legacy security helpers. No framework dependencies. |
| `src/Genesis.Infrastructure` | Dapper repositories, connection factory for the 5 databases. |
| `src/Genesis.Reports` | Reports built in code: QuestPDF (PDF) and ClosedXML (Excel). |
| `tests/Genesis.Tests` | xUnit tests. |

## Run locally
```
dotnet user-secrets set "Jwt:Key" "<random string, 32+ chars>" --project src/Genesis.Api
dotnet run --project src/Genesis.Api --launch-profile http     # http://localhost:5000
dotnet test
```
Connection strings (Windows auth to `localhost`) are in `appsettings.json`; override with `ConnectionStrings__Main` etc. Never commit secrets.
API docs (Development only): `/scalar/v1`. Health: `/api/health`.

## Conventions
- New screen = new folder in `Features/` + a repository in Core/Infrastructure. Prefer calling the existing stored procedures through Dapper; always parameterize SQL.
- Permissions: `.RequireScreen(position)` uses the legacy `USR.USRAccess` flag position (1-based).
- Auth today: legacy password check (`LegacyPasswordCipher`) + JWT. Replace with SSO/OIDC later without touching feature code.

## Migration inventory
`tools/inventory/build_inventory.py` scans the legacy VB6 source and writes `docs/inventory/` (forms, stored procedures, tables,
reports, menu-to-form map with `USRAccess` positions, clone groups, `summary.md`). Re-run it after changing the catalog TSVs
(`tools/inventory/catalog`, one per database). Python 3 only, no packages needed.

## Migrated screens
| Legacy screen | API | Status |
|---|---|---|
| Membership > Enquiry (`frmNewMBMEnquiry`) | `/api/membership/members` | Complete: search and all 16 tabs (details, supplementary, adjust history, benefits, current/historic cases, member history, accounts, fees, non-disclosure, policy terms, lifetime limit, policy wording, takeover claims, notes) plus the three print outputs. |
| Case > Case Inquiry (`frmCaseInquiry`) | `/api/cases` | Complete: search by 7 criteria and all 19 tabs (case, others, take-over, notes, LG, worksheets, invoices, payments and receipts, reminders, non-disclosure, benefits, exclusions, policy terms, surgical schedule, lifetime limit, documents, policy wording, cartons, e-mails) plus 4 print outputs. Needs `USRAccess` position 13; payment print needs 178. |

Integration tests (`DatabaseFact`) run against the local restored databases and skip themselves when SQL Server is unavailable.

### Membership Enquiry notes
- Print outputs are built in code (no Crystal or Office): `print/enquiry.pdf` (replaces `RptMbmEnquiry1.rpt`), `print/exclusion-form.xlsx` (replaces `MBMExclusion.xlt`), `print/history.xlsx` (replaces `PrintHistory.xlt`).
- The legacy form passed raw SQL fragments to `SearchMBMHistory06`, `Case_SearchIllness`, `SP_SearchLTLmt` and `Case_Registration`. Those are replaced by parameterised queries on the same views/tables.
- Policy wording documents are read from `FileStore:PolicyWordingRoot` (one sub-folder per plan, taken from `PLN.PLNPolicyWording`). Leave it unset to disable; swap `IPolicyWordingStore` for blob storage in the cloud.
- The restored 2021 snapshot has no rows for covered persons, lifetime-limit views, non-disclosure or most account views, so those tabs are covered by "empty state" tests only.

### Case Inquiry notes
- Case record: `SearchCasAdjustment` (parameterised). The tabs read from four databases: Main, Worksheet (worksheets, invoices, reminders, cartons, account summary), Payment (payments and receipts), Maintenance (letters of guarantee).
- The legacy `Case_Inquiry`, `Case_Adjustment` and `Case_SearchIllness` procedures took raw SQL fragments; they are replaced by parameterised queries on the same tables and views.
- Benefits: the legacy form listed every effective-date version of the plan; this port shows the latest version on or before the admission date (same rule as Membership Enquiry).
- Documents (scanned, internal, e-mails, policy wording) come from `FileStore:ScannedDocsRoot`, `InternalDocsRoot`, `EmailsRoot`, `PolicyWordingRoot` (one sub-folder per case or plan). Unset = tab says "not configured". Swap `ICaseDocumentStore` for blob storage in the cloud.
- Print outputs replace `CaseEnq.xlt`, `CasePayment.xlt` and `CaseRemark.DOT`: `print/enquiry.xlsx`, `print/payments.xlsx`, `print/notes.pdf[?kind=internal]`.

## Tracking screens (generic framework)
Most read-only legacy screens are "criteria -> dynamic SQL -> grid -> export" forms. They are described once in `Genesis.Core/Tracking/TrackingCatalog.cs`
(view, whitelisted columns, filters, legacy `USRAccess` position) and served by generic code:
- `TrackingQueryBuilder` builds a parameterised query (column names only from the definition, values always parameters, wildcards escaped, unknown filter keys rejected).
- `/api/tracking`, `/api/tracking/{id}`, `/search`, `/export.xlsx`; the UI page and the sidebar menu are generated from the definition.
- To add a screen, add one `TrackingDefinition`. `Every_definition_matches_the_real_schema` fails if a column or view name is wrong.
- `RequiresFilter: true` guards very large views (membership tracking, claim report).

## Building blocks for write flows (reuse these)
| Need | Use |
|---|---|
| Run SQL / procedures against the right database | `SqlRepository` base class (`QueryAsync`, `ScalarAsync`), `IDbConnectionFactory`, `RowReader` |
| Screen permission | `.RequireScreen(position)` (endpoint) and `ClaimsPrincipal.CanAccess(position)` |
| Input validation | `MemberNumber`, `CaseNumber`, and the tracking filter validation pattern |
| Search + grid + Excel export | Tracking framework |
| Documents (scanned, internal, e-mails, policy wording) | `ICaseDocumentStore`, `IPolicyWordingStore` |
| Print outputs | `Genesis.Reports` (QuestPDF, ClosedXML) |
| UI tables, fields, print buttons, downloads | `src/components/DataTables.tsx`, `PrintButton.tsx`, `src/lib/download.ts`, `format.ts` |
