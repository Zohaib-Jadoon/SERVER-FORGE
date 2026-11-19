using ServerForge.Agent.Configuration;
using Xunit;

namespace ServerForge.AgentTests;

public sealed class AgentIdentityAndPairingTests
{
    [Fact]
    public void AgentIdentityStore_PersistsCertificateAndCompletesPairingState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sf-agent-test-{Guid.NewGuid():N}");
        try
        {
            var opts = new AgentOptions { DataRoot = tempDir, AppsRoot = Path.Combine(tempDir, "apps") };
            var store = new AgentIdentityStore(opts);
            Assert.False(store.State.IsPaired);

            using var cert = store.LoadOrCreateServerCertificate();
            Assert.False(string.IsNullOrWhiteSpace(cert.Thumbprint));

            store.CompletePairing("desktop-01", "AA:BB:CC:DD:EE:FF");
            Assert.True(store.State.IsPaired);
            Assert.Equal("AABBCCDDEEFF", store.State.PairedDesktopCertThumbprint);

            // Re-open from disk and confirm persistence across restarts
            var reloaded = new AgentIdentityStore(opts);
            Assert.True(reloaded.State.IsPaired);
            Assert.Equal("desktop-01", reloaded.State.PairedDesktopInstanceId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
