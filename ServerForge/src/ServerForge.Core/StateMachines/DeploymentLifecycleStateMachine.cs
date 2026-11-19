using ServerForge.Contracts.Enums;

namespace ServerForge.Core.StateMachines;

/// <summary>
/// Enforces legal transitions across the 12-state ServerForge deployment lifecycle.
/// </summary>
public static class DeploymentLifecycleStateMachine
{
    private static readonly Dictionary<DeploymentState, HashSet<DeploymentState>> AllowedTransitions = new()
    {
        [DeploymentState.Queued] = [DeploymentState.Fetching, DeploymentState.Cancelled, DeploymentState.Failed],
        [DeploymentState.Fetching] = [DeploymentState.Building, DeploymentState.Staging, DeploymentState.Failed, DeploymentState.Cancelled],
        [DeploymentState.Building] = [DeploymentState.Testing, DeploymentState.Packaging, DeploymentState.Failed, DeploymentState.Cancelled],
        [DeploymentState.Testing] = [DeploymentState.Packaging, DeploymentState.Failed, DeploymentState.Cancelled],
        [DeploymentState.Packaging] = [DeploymentState.Staging, DeploymentState.Failed, DeploymentState.Cancelled],
        [DeploymentState.Staging] = [DeploymentState.Deploying, DeploymentState.Failed, DeploymentState.Cancelled],
        [DeploymentState.Deploying] = [DeploymentState.Verifying, DeploymentState.Failed, DeploymentState.RolledBack],
        [DeploymentState.Verifying] = [DeploymentState.Successful, DeploymentState.Failed, DeploymentState.RolledBack],
        [DeploymentState.Failed] = [DeploymentState.RolledBack],
        [DeploymentState.Successful] = [],
        [DeploymentState.RolledBack] = [],
        [DeploymentState.Cancelled] = []
    };

    public static bool CanTransition(DeploymentState current, DeploymentState next)
        => AllowedTransitions.TryGetValue(current, out var allowed) && allowed.Contains(next);

    public static DeploymentState TransitionOrThrow(DeploymentState current, DeploymentState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Illegal deployment state transition from '{current}' to '{next}'.");
        }
        return next;
    }

    public static bool IsTerminal(DeploymentState state)
        => state is DeploymentState.Successful or DeploymentState.Failed or DeploymentState.RolledBack or DeploymentState.Cancelled;
}
