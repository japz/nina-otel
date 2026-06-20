using NinaOtel.Core.Options;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.ExceptionServices;
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

    public static HttpClient CreateForSdkExporter(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return Create(options);
        }
        catch (Exception ex)
        {
            return CreateFailingClient(options, ex);
        }
    }

    internal static HttpClientHandler CreateHandler(OtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new CertificateDisposingHttpClientHandler
        {
            UseCookies = false,
        };
        try
        {
            var auth = options.Auth;
            if (!string.IsNullOrWhiteSpace(auth.CaCertificatePemPath))
            {
                var caCertificate = LoadCertificate(auth.CaCertificatePemPath);
                handler.Own(caCertificate);
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
                    ValidateCertificateWithCustomRoot(certificate, caCertificate, sslPolicyErrors);
            }

            if (!string.IsNullOrWhiteSpace(auth.ClientCertificatePemPath))
            {
                var clientCertificate = LoadClientCertificate(
                    auth.ClientCertificatePemPath,
                    auth.ClientPrivateKeyPemPath);
                handler.Own(clientCertificate);
                handler.ClientCertificates.Add(clientCertificate);
            }
            else if (!string.IsNullOrWhiteSpace(auth.ClientPrivateKeyPemPath))
            {
                throw new InvalidOperationException(
                    "Client certificate PEM path is required when a client private key PEM path is configured.");
            }

            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Certificate file was not found.", path);
        }

        return X509Certificate2.CreateFromPemFile(path);
    }

    private static X509Certificate2 LoadClientCertificate(string certificatePath, string? privateKeyPath)
    {
        if (!File.Exists(certificatePath))
        {
            throw new FileNotFoundException("Client certificate file was not found.", certificatePath);
        }

        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            return X509Certificate2.CreateFromPemFile(certificatePath);
        }

        if (!File.Exists(privateKeyPath))
        {
            throw new FileNotFoundException("Client private key file was not found.", privateKeyPath);
        }

        return X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
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

    private static HttpClient CreateFailingClient(OtlpOptions options, Exception exception) =>
        new(new FailingHttpMessageHandler(exception), disposeHandler: true)
        {
            Timeout = NormalizeTimeout(options.Timeout),
        };

    private sealed class FailingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        private readonly ExceptionDispatchInfo failure = ExceptionDispatchInfo.Capture(exception);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            try
            {
                failure.Throw();
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }

            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Unreachable failing OTLP HTTP client state."));
        }
    }

    private sealed class CertificateDisposingHttpClientHandler : HttpClientHandler
    {
        private readonly List<X509Certificate2> ownedCertificates = [];

        public void Own(X509Certificate2 certificate) => ownedCertificates.Add(certificate);

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    foreach (var certificate in ownedCertificates)
                    {
                        certificate.Dispose();
                    }
                }
            }
        }
    }
}
