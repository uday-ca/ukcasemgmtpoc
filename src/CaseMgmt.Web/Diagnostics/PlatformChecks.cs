using System.Diagnostics;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;

namespace CaseMgmt.Web.Diagnostics;

public record CheckResult(string Name, bool Ok, string Detail, long Ms);

/// <summary>
/// End-to-end checks of the platform: every call uses the web app's managed identity over a private endpoint.
/// </summary>
public class PlatformChecks(IConfiguration cfg, TokenCredential credential, AppInfo info)
{
    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct) =>
        await Task.WhenAll(
            Run("sql", SqlAsync, ct),
            Run("blob", BlobAsync, ct),
            Run("keyVault", KeyVaultAsync, ct),
            Run("dataProtectionKey", DataProtectionKeyAsync, ct));

    private static async Task<CheckResult> Run(string name, Func<CancellationToken, Task<string>> check, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90)); // serverless SQL may need ~1 minute to resume
            var detail = await check(timeout.Token);
            return new CheckResult(name, true, detail, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, $"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    // SQL: who am I (the app's managed identity) + the migrated table exists.
    private async Task<string> SqlAsync(CancellationToken ct)
    {
        var cs = cfg.GetConnectionString("Sql") ?? throw new InvalidOperationException("ConnectionStrings:Sql is not set");
        await using var con = new SqlConnection(cs);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT SUSER_SNAME(), DB_NAME(), (SELECT COUNT(*) FROM dbo.Visits)", con);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return $"signed in as {r.GetString(0)} to {r.GetString(1)}; Visits rows: {r.GetInt32(2)}";
    }

    // Blob: write and read back a small file in the documents container.
    private async Task<string> BlobAsync(CancellationToken ct)
    {
        var endpoint = cfg["Storage:BlobEndpoint"] ?? throw new InvalidOperationException("Storage:BlobEndpoint is not set");
        var blob = new BlobServiceClient(new Uri(endpoint), credential)
            .GetBlobContainerClient("documents")
            .GetBlobClient($"diagnostics/{info.Slot}.txt");
        var text = $"written {DateTime.UtcNow:O} by {info.Slot} ({info.Version})";
        await blob.UploadAsync(BinaryData.FromString(text), overwrite: true, ct);
        var back = (await blob.DownloadContentAsync(ct)).Value.Content.ToString();
        if (back != text) throw new InvalidOperationException("read-back differs");
        return $"wrote and read {blob.Name}";
    }

    // Key Vault: list secret names (Key Vault Secrets User).
    private async Task<string> KeyVaultAsync(CancellationToken ct)
    {
        var uri = cfg["KeyVault:Uri"] ?? throw new InvalidOperationException("KeyVault:Uri is not set");
        var client = new SecretClient(new Uri(uri), credential);
        var count = 0;
        await foreach (var _ in client.GetPropertiesOfSecretsAsync(ct)) count++;
        return $"listed {count} secret(s)";
    }

    // Data Protection key: wrap + unwrap 32 random bytes (Key Vault Crypto User on the key).
    private async Task<string> DataProtectionKeyAsync(CancellationToken ct)
    {
        var keyId = cfg["DataProtection:KeyId"] ?? throw new InvalidOperationException("DataProtection:KeyId is not set");
        var crypto = new CryptographyClient(new Uri(keyId), credential);
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var wrapped = await crypto.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, data, ct);
        var unwrapped = await crypto.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrapped.EncryptedKey, ct);
        if (!unwrapped.Key.AsSpan().SequenceEqual(data)) throw new InvalidOperationException("unwrap returned different bytes");
        return "wrap/unwrap OK";
    }
}

/// <summary>What is running where: version (commit), slot, instance.</summary>
public class AppInfo(IConfiguration cfg, IHostEnvironment env)
{
    public string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "local";

    // OTEL_SERVICE_NAME is a slot-sticky setting: "<app>" in production, "<app>-staging" in the staging slot.
    public string Slot { get; } = cfg["OTEL_SERVICE_NAME"] ?? "local";
    public string Environment { get; } = env.EnvironmentName;
    public string Instance { get; } = System.Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")?[..8] ?? System.Environment.MachineName;
}
