# ukcasemgmtpoc

Case Management - **platform test app** (.NET 10). A small web app that proves the Azure platform end to end:
managed identity, private endpoints, SQL, Blob Storage, Key Vault, Data Protection key, deployment slot and the
GitHub pipeline through the self-hosted runner. No secrets anywhere.

## Endpoints

| Path | Shows |
|---|---|
| `/` | environment, slot, version (commit), instance, and a visit counter stored in Azure SQL |
| `/diag` | JSON checks: SQL (signed-in identity), Blob (write/read in `documents`), Key Vault (list secrets), Data Protection key (wrap/unwrap). 200 = all OK, 503 = a check failed |
| `/healthz` | liveness only (App Service health check, pipeline smoke test) |

## Layout

```
CaseMgmt.sln
src/CaseMgmt.Web/                Program.cs, Data/ (DbContext + EF Core migrations), Diagnostics/
tests/CaseMgmt.Web.Tests/        /healthz and home page tests (run in the build)
.github/workflows/deploy-dev.yml build -> test -> migrate -> staging -> smoke test -> swap -> check
nuget.config, dotnet-tools.json  package source (nuget.org) and the dotnet-ef tool
```

## Deploy

Push to the `dev` branch (or Actions -> build-and-deploy-dev -> Run workflow). The workflow uses only the variables
of the GitHub environment `dev`, created by the platform's step 2.

1. **build** (GitHub-hosted): restore, build, test, publish, EF Core migration bundle
2. **deploy-dev** (runner `casemgmt-dev` inside the VNet): OIDC sign-in -> migrations with the deploy identity ->
   deploy to `staging` -> `/healthz` and `/diag` on staging -> swap -> `/healthz` and `/diag` on production ->
   public URL (optional, needs the Cloudflare DNS record)

Without deployment slots (`AZURE_WEBAPP_SLOT` empty) it deploys straight to production.

## Run locally

```powershell
dotnet test
dotnet run --project src/CaseMgmt.Web     # /healthz and / work without Azure; /diag needs the Azure settings
```

New migration: `dotnet tool restore` then `dotnet ef migrations add <Name> --project src/CaseMgmt.Web --output-dir Data/Migrations`.
