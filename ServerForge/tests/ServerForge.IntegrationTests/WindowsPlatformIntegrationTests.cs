using System.Security.Cryptography;
using ServerForge.Security.Secrets;
using Xunit;

namespace ServerForge.IntegrationTests;

/// <summary>
/// Windows-specific integration tests designed to execute on GitHub Actions windows-latest
/// or a disposable Windows Server test VM.
/// </summary>
public sealed class WindowsPlatformIntegrationTests
{
    [Fact]
    public void DpapiSecretProtector_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // Guarded for non-Windows hosts; runs on windows-latest CI

        var protector = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        const string secret = "ServerForge_Live_Secret_Token_2026!";
        var encrypted = protector.Protect(secret, "integration:test");

        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, protector.Unprotect(encrypted, "integration:test"));
    }
}
