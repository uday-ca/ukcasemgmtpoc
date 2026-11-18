using System.Net;
using Azure.Core;
using Azure.Identity;
using CaseMgmt.Web.Data;
using CaseMgmt.Web.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// Case Management - platform test app.
// Uses only what the platform provides (no secrets): managed identity, private endpoints, app settings.
var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// In Azure: the web app's (or slot's) system-assigned managed identity. Locally: your Azure CLI / VS sign-in.
TokenCredential credential = new DefaultAzureCredential();
builder.Services.AddSingleton(credential);
builder.Services.AddSingleton<AppInfo>();
builder.Services.AddScoped<PlatformChecks>();

// SQL: connection string from App Service (Authentication=Active Directory Managed Identity - no password).
var sql = cfg.GetConnectionString("Sql");
if (!string.IsNullOrWhiteSpace(sql))
{
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(sql, s => s.EnableRetryOnFailure()));
}

// Data Protection key ring in Blob Storage, protected by the Key Vault key: sign-in cookies survive restarts,
// scale-out and slot swaps.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("casemgmt");
if (cfg["DataProtection:BlobUri"] is { Length: > 0 } dpBlob && cfg["DataProtection:KeyId"] is { Length: > 0 } dpKey)
{
    dataProtection.PersistKeysToAzureBlobStorage(new Uri(dpBlob), credential)
                  .ProtectKeysWithAzureKeyVault(new Uri(dpKey), credential);
}

// Behind Cloudflare: the real client IP comes in CF-Connecting-IP.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (cfg["ForwardedHeaders:ForwardedForHeaderName"] is { Length: > 0 } header) o.ForwardedForHeaderName = header;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseForwardedHeaders();

// Liveness only (App Service health check + pipeline smoke test) - no dependencies, so a slow database never
// takes the app out of rotation.
app.MapHealthChecks("/healthz");

// Home page: what is running + a visit counter stored in SQL.
app.MapGet("/", async (AppInfo info, IServiceProvider sp, CancellationToken ct) =>
{
    string visits;
    var db = sp.GetService<AppDbContext>();
    if (db is null)
    {
        visits = "SQL not configured";
    }
    else
    {
        try
        {
            db.Visits.Add(new Visit { VisitedUtc = DateTime.UtcNow, Slot = info.Slot });
            await db.SaveChangesAsync(ct);
            visits = $"{await db.Visits.CountAsync(ct)} visits stored in Azure SQL";
        }
        catch (Exception ex)
        {
            visits = $"SQL error: {ex.GetType().Name} - {ex.Message}";
        }
    }

    var html = $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Case Management - platform test</title>
        <style>body{font-family:Segoe UI,Arial,sans-serif;margin:40px;color:#1f2937}h1{color:#1f3a5f}
        table{border-collapse:collapse}td{padding:6px 14px;border-bottom:1px solid #e5e7eb}td:first-child{color:#6b7280}</style>
        </head><body>
        <h1>Case Management - platform test</h1>
        <table>
          <tr><td>Environment</td><td>{{WebUtility.HtmlEncode(info.Environment)}}</td></tr>
          <tr><td>Slot</td><td>{{WebUtility.HtmlEncode(info.Slot)}}</td></tr>
          <tr><td>Version</td><td>{{WebUtility.HtmlEncode(info.Version)}}</td></tr>
          <tr><td>Instance</td><td>{{WebUtility.HtmlEncode(info.Instance)}}</td></tr>
          <tr><td>Database</td><td>{{WebUtility.HtmlEncode(visits)}}</td></tr>
        </table>
        <p><a href="/diag">/diag</a> - checks SQL, Blob Storage, Key Vault and the Data Protection key &middot; <a href="/healthz">/healthz</a></p>
        </body></html>
        """;
    return Results.Content(html, "text/html");
});

// End-to-end platform checks (JSON). 200 = everything OK, 503 = at least one check failed.
app.MapGet("/diag", async (PlatformChecks checks, AppInfo info, CancellationToken ct) =>
{
    var results = await checks.RunAsync(ct);
    var ok = results.All(r => r.Ok);
    return Results.Json(new
    {
        status = ok ? "ok" : "failed",
        info.Environment,
        info.Slot,
        info.Version,
        info.Instance,
        checks = results,
    }, statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.Run();

// Lets the integration tests start the app (WebApplicationFactory<Program>).
public partial class Program;
