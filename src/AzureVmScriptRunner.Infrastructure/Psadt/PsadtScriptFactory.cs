using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Infrastructure.Azure;

namespace AzureVmScriptRunner.Infrastructure.Psadt;

public interface IPsadtScriptFactory
{
    Task<string> CreateScriptAsync(
        PsadtPayload payload, VmTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the PSADT deployment script with the per-VM download strategy:
/// the in-guest script always tries managed identity first; a user-delegation SAS
/// (1 hour, read-only, single blob, created under the signed-in admin's identity so
/// its issuance is auditable) is attached as fallback where possible. No long-lived
/// secrets are ever produced.
/// </summary>
public sealed class PsadtScriptFactory : IPsadtScriptFactory
{
    private static readonly TimeSpan SasLifetime = TimeSpan.FromHours(1);

    private readonly AzureSession _session;

    public PsadtScriptFactory(AzureSession session) => _session = session;

    public async Task<string> CreateScriptAsync(
        PsadtPayload payload, VmTarget target, CancellationToken cancellationToken)
    {
        var fallbackSas = await TryCreateUserDelegationSasAsync(payload.PackageUrl, cancellationToken);

        if (fallbackSas is null && !target.HasSystemAssignedIdentity)
        {
            throw new InvalidOperationException(
                $"VM '{target.Name}' has no system-assigned managed identity and a fallback SAS could not be " +
                "generated (you need 'Storage Blob Data Reader' or higher on the storage account to issue a " +
                "user-delegation SAS). Enable a managed identity on the VM or grant yourself blob access.");
        }

        return PsadtScriptBuilder.Build(payload, fallbackSas);
    }

    private async Task<Uri?> TryCreateUserDelegationSasAsync(
        Uri packageUrl, CancellationToken cancellationToken)
    {
        try
        {
            var blobUri = new BlobUriBuilder(packageUrl);
            var serviceClient = new BlobServiceClient(
                new Uri($"https://{blobUri.Host}"), _session.Credential);

            var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5); // clock-skew allowance
            var expiresOn = DateTimeOffset.UtcNow.Add(SasLifetime);

            var key = await serviceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = blobUri.BlobContainerName,
                BlobName = blobUri.BlobName,
                Resource = "b",
                StartsOn = startsOn,
                ExpiresOn = expiresOn
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            blobUri.Sas = sasBuilder.ToSasQueryParameters(key, serviceClient.AccountName);
            return blobUri.ToUri();
        }
        catch (RequestFailedException)
        {
            // Admin lacks data-plane rights on the storage account; managed identity
            // in-guest remains the only path.
            return null;
        }
    }
}
