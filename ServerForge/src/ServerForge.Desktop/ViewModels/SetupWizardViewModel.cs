using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Desktop.Services;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Secrets;

namespace ServerForge.Desktop.ViewModels;

/// <summary>
/// 10-step First-Launch Setup Wizard ViewModel (no cloud ServerForge account required).
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly ServerForgeStore _store;
    private readonly ISecretProtector _secretProtector;
    private readonly IGitHubClient _githubClient;
    private readonly AgentTransportService _agentTransport;

    public SetupWizardViewModel(
        ServerForgeStore store,
        ISecretProtector secretProtector,
        IGitHubClient githubClient,
        AgentTransportService agentTransport)
    {
        _store = store;
        _secretProtector = secretProtector;
        _githubClient = githubClient;
        _agentTransport = agentTransport;
    }

    public event EventHandler<bool>? WizardCompleted;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _statusBanner = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // Step 2: Local Administrator Profile
    [ObservableProperty] private string _adminDisplayName = Environment.UserName;
    [ObservableProperty] private string _adminUsername = Environment.UserName;
    [ObservableProperty] private string _adminPassword = string.Empty;

    // Step 3: DPAPI Credential Store
    [ObservableProperty] private bool _dpapiVerified;

    // Step 4: GitHub Fine-Grained PAT
    [ObservableProperty] private string _gitHubToken = string.Empty;
    [ObservableProperty] private string _gitHubValidationSummary = "Not connected yet (optional — can be configured later in Settings).";

    // Step 5–7: Windows Server + Agent Pairing
    [ObservableProperty] private bool _addServerNow = true;
    [ObservableProperty] private string _serverDisplayName = "Primary Windows Server";
    [ObservableProperty] private string _serverHostAddress = "10.0.0.10";
    [ObservableProperty] private int _serverAgentPort = 9443;
    [ObservableProperty] private string _agentPairingCode = string.Empty;
    [ObservableProperty] private string _agentCertThumbprint = string.Empty;
    [ObservableProperty] private string _pairingStatus = "Pending pairing ceremony.";
    [ObservableProperty] private ManagedServer? _pairedServer;

    // Step 8–9: Read-Only Server Diagnostics
    [ObservableProperty] private ServerDiagnosticsSnapshot? _diagnosticsSnapshot;

    public string StepTitle => CurrentStep switch
    {
        1 => "1. Welcome to ServerForge",
        2 => "2. Create Local Administrator Profile",
        3 => "3. Configure Windows DPAPI Credential Store",
        4 => "4. Connect GitHub (Fine-Grained PAT)",
        5 => "5. Add a Windows Server",
        6 => "6. Install ServerForge Agent on Target Server",
        7 => "7. Secure Mutual-TLS Pairing Ceremony",
        8 => "8. Read-Only Server Diagnostics Check",
        9 => "9. Discovered Runtimes, IIS & Databases",
        10 => "10. Ready to Open ServerForge Dashboard",
        _ => "ServerForge Setup"
    };

    partial void OnCurrentStepChanged(int value) => OnPropertyChanged(nameof(StepTitle));

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 1)
            CurrentStep--;
    }

    [RelayCommand]
    private async Task NextStepAsync()
    {
        StatusBanner = string.Empty;

        if (CurrentStep == 2)
        {
            if (string.IsNullOrWhiteSpace(AdminUsername) || AdminPassword.Length < 8)
            {
                StatusBanner = "Enter a local username and a password of at least 8 characters.";
                return;
            }
        }
        else if (CurrentStep == 3 && !DpapiVerified)
        {
            VerifyDpapiStore();
        }
        else if (CurrentStep == 5 && !AddServerNow)
        {
            CurrentStep = 10;
            return;
        }
        else if (CurrentStep == 10)
        {
            await CompleteSetupAsync();
            return;
        }

        if (CurrentStep < 10)
            CurrentStep++;
    }

    [RelayCommand]
    private void VerifyDpapiStore()
    {
        try
        {
            const string probe = "serverforge-dpapi-selftest";
            var cipher = _secretProtector.Protect(probe, "setup:selftest");
            var clear = _secretProtector.Unprotect(cipher, "setup:selftest");
            DpapiVerified = string.Equals(probe, clear, StringComparison.Ordinal);
            StatusBanner = DpapiVerified
                ? "Windows DPAPI CurrentUser encryption verified. Secrets are never stored in plaintext."
                : "DPAPI round-trip check failed.";
        }
        catch (Exception ex)
        {
            DpapiVerified = false;
            StatusBanner = $"DPAPI check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ValidateGitHubTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubToken))
        {
            GitHubValidationSummary = "Enter a GitHub Fine-Grained Personal Access Token to validate.";
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _githubClient.ValidateTokenAsync(GitHubToken.Trim(), CancellationToken.None);
            GitHubValidationSummary = res.IsValid
                ? $"Connected as @{res.Login} ({res.AccountName}) · Rate Limit Remaining: {res.RateLimitRemaining}"
                : $"Validation failed: {res.ErrorMessage}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PairAgentAsync()
    {
        IsBusy = true;
        try
        {
            var resp = await _agentTransport.PairWithAgentAsync(
                ServerHostAddress.Trim(),
                ServerAgentPort,
                AgentPairingCode,
                AgentCertThumbprint,
                CancellationToken.None);

            if (!resp.Accepted)
            {
                PairingStatus = $"Pairing rejected: {resp.FailureReason}";
                return;
            }

            PairedServer = new ManagedServer
            {
                AgentId = resp.AgentId,
                DisplayName = ServerDisplayName.Trim(),
                HostAddress = ServerHostAddress.Trim(),
                AgentPort = ServerAgentPort,
                PinnedServerCertThumbprint = resp.AgentCertificateThumbprint,
                Status = ServerConnectionStatus.Connected,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            PairingStatus = $"Mutual-TLS pairing succeeded with {resp.Hostname} (Agent {resp.AgentId[..8]}).";
        }
        catch (Exception ex)
        {
            PairingStatus = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunReadOnlyDiagnosticsAsync()
    {
        if (PairedServer is null)
        {
            StatusBanner = "Pair with a ServerForge Agent first to run live read-only diagnostics.";
            return;
        }

        IsBusy = true;
        try
        {
            DiagnosticsSnapshot = await _agentTransport.FetchDiagnosticsAsync(PairedServer, CancellationToken.None);
            StatusBanner = DiagnosticsSnapshot is not null
                ? $"Diagnostics captured from {DiagnosticsSnapshot.Hostname}: {DiagnosticsSnapshot.ExistingIisSites.Count} existing IIS sites marked read-only."
                : "Agent returned no diagnostic payload.";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Diagnostics check failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompleteSetupAsync()
    {
        IsBusy = true;
        try
        {
            var (hash, salt) = LocalPasswordHasher.HashPassword(
                string.IsNullOrWhiteSpace(AdminPassword) ? Guid.NewGuid().ToString("N") : AdminPassword);

            await _store.SaveLocalAdminAsync(new LocalAdminProfile
            {
                DisplayName = AdminDisplayName.Trim(),
                Username = AdminUsername.Trim(),
                PasswordHash = hash,
                PasswordSalt = salt,
                DarkThemeEnabled = true,
                DemoModeEnabled = false
            });

            WizardCompleted?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
