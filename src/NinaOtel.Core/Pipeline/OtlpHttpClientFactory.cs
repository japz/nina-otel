using NinaOtel.Core.Options;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpHttpClientFactory
{
    public static HttpClient Create(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = CreateHandler(options);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = NormalizeTimeout(options.Timeout),
        };
        return client;
    }

    internal static HttpClientHandler CreateHandler(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler();
        var auth = options.Auth;
        if (!string.IsNullOrWhiteSpace(auth.CaCertificatePemPath))
        {
            var caCertificate = LoadCertificate(auth.CaCertificatePemPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
                ValidateCertificateWithCustomRoot(certificate, caCertificate, sslPolicyErrors);
        }

        if (!string.IsNullOrWhiteSpace(auth.ClientCertificatePemPath))
        {
            var clientCertificate = string.IsNullOrWhiteSpace(auth.ClientPrivateKeyPemPath)
                ? LoadCertificate(auth.ClientCertificatePemPath)
                : X509Certificate2.CreateFromPemFile(auth.ClientCertificatePemPath, auth.ClientPrivateKeyPemPath);
            handler.ClientCertificates.Add(clientCertificate);
        }

        return handler;
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Certificate file was not found.", path);
        }

        return X509Certificate2.CreateFromPemFile(path);
    }

    private static bool ValidateCertificateWithCustomRoot(
        X509Certificate? certificate,
        X509Certificate2 caCertificate,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            return false;
        }

        var disallowedErrors = sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors;
        if (disallowedErrors != SslPolicyErrors.None)
        {
            return false;
        }

        using var serverCertificate = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(serverCertificate);
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
}
