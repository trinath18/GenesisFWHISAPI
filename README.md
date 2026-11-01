# GenesisFIS API

Lives in `C:\GenesisFIS\FISMigration\FISAPI\GenesisFISAPI`; the web front end is `..\..\FISUI\GenesisFISUI`. Solution: `GenesisFIS.slnx`.

ASP.NET Core (.NET 10) API for the Genesis FIS migration. Talks to the existing SQL Server databases unchanged.

## Layout
| Project | Purpose |
|---|---|
| `src/GenesisFIS.Api` | HTTP host. One folder per feature under `Features/` (endpoints + request/response models). |
| `src/GenesisFIS.Core` | Models, repository interfaces, legacy security helpers. No framework dependencies. |
| `src/GenesisFIS.Infrastructure` | Dapper repositories, connection factory for the 5 databases. |
| `src/GenesisFIS.Reports` | Reports built in code: QuestPDF (PDF) and ClosedXML (Excel). |
| `tests/GenesisFIS.Tests` | xUnit tests. |

## Run locally
```
dotnet user-secrets set "Jwt:Key" "<random string, 32+ chars>" --project src/GenesisFIS.Api
dotnet run --project src/GenesisFIS.Api --launch-profile http     # http://localhost:5000
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

### Case menu: remaining screens
Lifetime Lmt Upgrade, Case Audit Review, Send to Bordx Unit, Non-Disclosure Info, Send to Claims (Rep), CRU Send to Claims, Claim ASCII and Case ASCII
(`Features/Cases/CaseLifetimeAuditBordxEndpoints.cs`, `CaseMenuEndpoints.cs`). Details and differences from the legacy forms are in `docs/inventory/migration_status.md`.
- **Legacy dynamic-SQL procedures.** Send to Claims (Rep), CRU and the ASCII screens call the legacy procedures (`Case_Send2ClaimRep`, `CRU_Send2Claim`, `Case_ClaimAscii`, ...) that append a SQL fragment to their own query.
  The fragment is built only by `Infrastructure/Data/LegacyFilter.cs`: column names are constants, values are whitelisted (letters, digits, `* - _ . /` and space) and dates are formatted by the server; anything else is refused. Use it only for these procedures.
- **ASCII files.** Layout in `GenesisFIS.Core/Cases/AsciiFiles.cs`. Set `FileStore:AsciiExportRoot` to keep a server copy of every generated file (sub-folders `ClaimASCII`, `CaseASCII`); unset = download only.
- **Schema export.** `tools/inventory/dump_schema.ps1` (or double-click `run_dump_schema.cmd`) writes every procedure/view source and table column list to `docs/inventory/schema/`, read-only. Useful when porting a screen that calls stored procedures.

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
Most read-only legacy screens are "criteria -> dynamic SQL -> grid -> export" forms. They are described once in `GenesisFIS.Core/Tracking/TrackingCatalog.cs`
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
| Print outputs | `GenesisFIS.Reports` (QuestPDF, ClosedXML) |
| UI tables, fields, print buttons, downloads | `src/components/DataTables.tsx`, `PrintButton.tsx`, `src/lib/download.ts`, `format.ts` |

## Summary reports (group-by engine)
`GenesisFIS.Core/Tracking/SummaryCatalog.cs` describes reports where the user picks grouping columns and measures (counts, sums) from whitelists, with the same
filters as tracking screens. One definition replaces the ~120 hard-coded aggregate branches of the legacy ClaimReport/DCUReport/BordxTracking procedures.
`SummaryQueryBuilder` only emits expressions from the catalog and parameterises every filter value. API: `/api/summary`, `/{id}`, `/{id}/run`, `/{id}/export.xlsx`
(`dims=` absent = defaults, `dims=` empty = grand total, `top=N` = biggest N groups).
Note: in the restored snapshot `VIEW_ClaimRpt_Simple` has no case numbers, so claim reports use `VIEW_NEWCLMReport` / `VIEW_CLMReportDCU`, which hold the data.

## Write flows: conventions
Write screens follow one pattern so each new one is small and safe:
1. **Contract in `GenesisFIS.Core`**: request/response records, validation rules, a repository interface.
2. **Repository in `GenesisFIS.Infrastructure`** derived from `SqlRepository`. Reads use `QueryAsync`; every write goes through `Db.BeginSessionAsync(db)` which returns an `IDbSession` (one transaction; disposing without `CommitAsync` rolls back). All values are parameters.
3. **Rules throw `BusinessRuleException`** (`Invalid` 400, `NotFound` 404, `Conflict` 409); `UseBusinessRuleErrors` turns them into `{ error, field }` JSON.
4. **Audit**: after a successful commit call `IAuditLog.Record(...)` with action, entity, key, user and details. Entries go to `logs/audit-YYYYMMDD.jsonl` (set `Audit:Path`), an append-only JSON file, so no table is added to the legacy databases. Legacy "last updated by/on" columns are still stamped with `user.UserCode()` exactly as before.
5. **Endpoint** uses `.RequireScreen(position)` with the legacy USRAccess position, and `user.UserCode()` for stamps.
6. **Tests** use `WriteApiFactory`: every database gets one connection inside an open transaction, sessions use savepoints, and `ResetAsync()` (call it in `DisposeAsync`) rolls everything back. Real data is never changed; `Test_changes_are_rolled_back_and_the_real_database_is_untouched` proves it with a row-level checksum.
7. **UI**: `ConfirmDialog` before saving, `useMutation` + invalidate queries, success/error `Alert`.
Browser write tests against the restored database must snapshot the touched table (count + CHECKSUM_AGG) before and after, and undo their own changes.

### Code-table engine (`LookupCatalog`)
Simple maintained tables are described once in `GenesisFIS.Core/Maintenance/LookupCatalog.cs` (table, fields with lengths/required/choices/money, legacy update-stamp columns, references that block deletion, generated keys). The generic repository, API (`/api/lookups`), audit and UI page (`/lookups/:id`) come for free; a schema test checks every column name and length against the real databases. Use it for the many small legacy maintenance forms.
