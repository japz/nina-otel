using FluentAssertions;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpHttpClientFactoryTests
{
    [Fact]
    public void Create_WhenNoTlsOptionsAreConfigured_ReturnsHttpClient()
    {
        using var client = OtlpHttpClientFactory.Create(new OtlpOptions());

        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Create_WhenCaCertificatePathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                CaCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };

        Action create = () => OtlpHttpClientFactory.Create(options).Dispose();

        create.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Create_WhenClientCertificatePathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                ClientCertificatePemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };

        Action create = () => OtlpHttpClientFactory.Create(options).Dispose();

        create.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Create_WhenClientPrivateKeyIsConfiguredWithoutClientCertificate_ThrowsInvalidOperationException()
    {
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                ClientPrivateKeyPemPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"),
            },
        };

        Action create = () => OtlpHttpClientFactory.Create(options).Dispose();

        create.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Client certificate PEM path is required*");
    }

    [Fact]
    public void CreateHandler_WhenClientCertificateAndPrivateKeyPathsAreConfigured_LoadsClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=nina-otel-test-client",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("nina-otel-client-cert-");
        var certificatePath = Path.Combine(directory.FullName, "client.pem");
        var privateKeyPath = Path.Combine(directory.FullName, "client-key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        var options = new OtlpOptions
        {
            Auth = new OtlpAuthOptions
            {
                ClientCertificatePemPath = certificatePath,
                ClientPrivateKeyPemPath = privateKeyPath,
            },
        };

        using var handler = OtlpHttpClientFactory.CreateHandler(options);

        handler.ClientCertificates.Count.Should().Be(1);
        var loadedCertificate = Assert.IsType<X509Certificate2>(handler.ClientCertificates[0]);
        loadedCertificate.HasPrivateKey.Should().BeTrue();
        directory.Delete(recursive: true);
    }
}
