using ServerForge.Agent.Configuration;
using Xunit;

namespace ServerForge.AgentTests;

public sealed class AgentIdentityAndPairingTests
{
    [Fact]
    public void AgentIdentityStore_PersistsCertificate_CompletesPairing_AndRotatesDesktopCert()
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

            // Certificate rotation preserves previous thumbprint and updates active pinned identity
            var rotated = store.RotateDesktopCertificate("desktop-01", "11:22:33:44:55:66");
            Assert.Equal("112233445566", rotated);
            Assert.Equal("AABBCCDDEEFF", store.State.PreviousDesktopCertThumbprint);

            // Reject rotation from wrong Desktop instance ID
            Assert.Throws<UnauthorizedAccessException>(() =>
                store.RotateDesktopCertificate("rogue-desktop", "99:88:77"));

            // Re-open from disk and confirm persistence across restarts
            var reloaded = new AgentIdentityStore(opts);
            Assert.True(reloaded.State.IsPaired);
            Assert.Equal("112233445566", reloaded.State.PairedDesktopCertThumbprint);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
