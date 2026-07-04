using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;

namespace AzureVmScriptRunner.Infrastructure.Azure;

/// <summary>
/// Signed-in Azure context shared by all infrastructure services: one credential, one
/// <see cref="ArmClient"/>, and the administrator's UPN for audit stamping. No
/// credentials are ever stored — tokens live in the credential's in-memory/OS cache.
/// </summary>
public sealed class AzureSession
{
    public TokenCredential Credential { get; }
    public ArmClient ArmClient { get; }

    /// <summary>UPN of the signed-in administrator, used as ExecutionRequest.RequestedBy.</summary>
    public string UserPrincipalName { get; }

    private AzureSession(TokenCredential credential, ArmClient armClient, string upn)
    {
        Credential = credential;
        ArmClient = armClient;
        UserPrincipalName = upn;
    }

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AzureVmScriptRunner");

    private static readonly string AuthRecordPath = Path.Combine(AppDataDir, "auth-record.json");

    private static readonly TokenRequestContext ArmScope =
        new(new[] { "https://management.azure.com/.default" });

    /// <summary>
    /// Establishes a session with silent single sign-on across app restarts: the MSAL
    /// token cache is persisted by the OS data-protection APIs, and the
    /// <see cref="AuthenticationRecord"/> (which account to reuse, no secrets) is kept
    /// alongside it, so the browser prompt appears only on first use or after the
    /// refresh token expires.
    /// </summary>
    public static async Task<AzureSession> CreateAsync(
        TokenCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        if (credential is not null)
        {
            var explicitToken = await credential.GetTokenAsync(ArmScope, cancellationToken);
            return new AzureSession(credential, new ArmClient(credential), ExtractUpn(explicitToken.Token));
        }

        var options = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "AzureVmScriptRunner"
            },
            AuthenticationRecord = await LoadAuthRecordAsync(cancellationToken)
        };

        var interactive = new InteractiveBrowserCredential(options);

        if (options.AuthenticationRecord is null)
        {
            var record = await interactive.AuthenticateAsync(ArmScope, cancellationToken);
            await SaveAuthRecordAsync(record, cancellationToken);
        }

        var token = await interactive.GetTokenAsync(ArmScope, cancellationToken);
        return new AzureSession(interactive, new ArmClient(interactive), ExtractUpn(token.Token));
    }

    /// <summary>
    /// Explicit interactive sign-in: always shows the browser prompt and never
    /// persists the account. Used by the desktop UI, where the administrator wants a
    /// deliberate sign-in on every launch rather than ambient credentials.
    /// </summary>
    public static async Task<AzureSession> CreateInteractiveAsync(
        CancellationToken cancellationToken = default)
    {
        var interactive = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions());
        await interactive.AuthenticateAsync(ArmScope, cancellationToken);
        var token = await interactive.GetTokenAsync(ArmScope, cancellationToken);
        return new AzureSession(interactive, new ArmClient(interactive), ExtractUpn(token.Token));
    }

    /// <summary>Forgets the persisted account (sign out). Cached tokens age out on their own.</summary>
    public static void ClearPersistedAccount()
    {
        if (File.Exists(AuthRecordPath))
        {
            File.Delete(AuthRecordPath);
        }
    }

    private static async Task<AuthenticationRecord?> LoadAuthRecordAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AuthRecordPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(AuthRecordPath);
            return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null; // corrupt/stale record → fall back to interactive sign-in
        }
    }

    private static async Task SaveAuthRecordAsync(
        AuthenticationRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppDataDir);
        await using var stream = File.Create(AuthRecordPath);
        await record.SerializeAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Reads the signed-in identity from the ARM access token claims, avoiding a
    /// Microsoft Graph dependency purely for display/audit purposes.
    /// </summary>
    private static string ExtractUpn(string accessToken)
    {
        try
        {
            var payload = accessToken.Split('.')[1];
            var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
                .Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));

            foreach (var claim in new[] { "upn", "unique_name", "preferred_username", "oid" })
            {
                if (json.RootElement.TryGetProperty(claim, out var value) &&
                    value.GetString() is { Length: > 0 } upn)
                {
                    return upn;
                }
            }
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }
}
