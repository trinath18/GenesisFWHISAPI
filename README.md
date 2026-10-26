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
