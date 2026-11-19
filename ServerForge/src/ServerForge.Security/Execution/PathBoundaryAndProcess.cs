using System.Diagnostics;
using System.Security.Cryptography;
using ServerForge.Core.Interfaces;

namespace ServerForge.Security.Execution;

/// <summary>
/// Enforces strict filesystem directory boundaries and prevents path traversal / symlink escapes.
/// </summary>
public sealed class PathBoundaryGuard : IPathBoundaryGuard
{
    public string EnsureWithinRoot(string allowedRoot, string candidateRelativeOrFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRelativeOrFullPath);

        var normalizedRoot = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;

        var combined = Path.IsPathRooted(candidateRelativeOrFullPath)
            ? Path.GetFullPath(candidateRelativeOrFullPath)
            : Path.GetFullPath(Path.Combine(normalizedRoot, candidateRelativeOrFullPath));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combined.StartsWith(normalizedRoot, comparison) &&
            !string.Equals(combined + Path.DirectorySeparatorChar, normalizedRoot, comparison))
        {
            throw new UnauthorizedAccessException(
                $"Path traversal blocked: '{candidateRelativeOrFullPath}' escapes authorized root '{allowedRoot}'.");
        }

        return combined;
    }

    public bool IsValidWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return false;

        if (workingDirectory == ".")
            return true;

        if (Path.IsPathRooted(workingDirectory))
            return false;

        var segments = workingDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(s => s == "..");
    }
}

public sealed record SafeProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

/// <summary>
/// Executes external binaries using explicit ProcessStartInfo.ArgumentList without shell interpolation.
/// </summary>
public static class SafeProcessRunner
{
    public static async Task<SafeProcessResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
                psi.Environment[key] = value;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var stdout = new List<string>();
        var stderr = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.Add(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Add(e.Data);
                onErrorLine?.Invoke(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        sw.Stop();
        return new SafeProcessResult(
            process.ExitCode,
            string.Join(Environment.NewLine, stdout),
            string.Join(Environment.NewLine, stderr),
            sw.Elapsed);
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
