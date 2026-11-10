using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetScope.Core.Diagnostics;

namespace AssetScope.Core.Sidecar;

public sealed record SidecarHandshake(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("dataDir")] string DataDir,
    [property: JsonPropertyName("mode")] string Mode);

public sealed class SidecarException : Exception
{
    public SidecarException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Starts the Node.js native-api sidecar, reads the single-line JSON handshake
/// from stdout (SPEC §6.2), and guarantees the child dies with the app: the
/// sidecar watches our PID, and Dispose kills it as a belt-and-braces measure.
/// </summary>
public sealed class SidecarLauncher : IDisposable
{
    public const string ExpectedProtocolVersion = "1.0";

    private Process? _process;
    private readonly JobObject _job = new();
    public SidecarHandshake? Handshake { get; private set; }
    public bool HasExited => _process?.HasExited ?? true;

    /// <summary>
    /// Installed layout: a bundled sidecar + private Node runtime shipped next
    /// to the app exe (SPEC §5.5). Present after build/install-winui.ps1.
    /// </summary>
    public static (string NodeExe, string Bundle)? FindInstalledSidecar()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "sidecar");
        var nodeExe = Path.Combine(dir, "node.exe");
        var bundle = Path.Combine(dir, "sidecar.mjs");
        return File.Exists(nodeExe) && File.Exists(bundle) ? (nodeExe, bundle) : null;
    }

    /// <summary>
    /// Locates the assetscope repository by walking up from the app binary,
    /// then from an explicit override. Development launches need only this
    /// repository's product-owned sidecar and package manifest.
    /// </summary>
    public static string? FindRepoRoot(string? overridePath = null)
    {
        static bool IsProductRoot(string dir) =>
            File.Exists(Path.Combine(dir, "package.json")) &&
            Directory.Exists(Path.Combine(dir, "native-api")) &&
            File.Exists(Path.Combine(dir, "native-api", "src", "main.ts"));

        if (!string.IsNullOrEmpty(overridePath) && IsProductRoot(overridePath)) return overridePath;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (IsProductRoot(dir)) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    public async Task<SidecarHandshake> StartAsync(
        string? repoRoot,
        string mode,
        string? dataDir,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        AppLog.Breadcrumb("Sidecar", $"Starting; mode={mode}, installed={FindInstalledSidecar() is not null}.");
        var installed = FindInstalledSidecar();
        if (installed is null && repoRoot is null)
            throw new SidecarException(
                "Neither an installed sidecar nor a complete assetscope checkout was found. " +
                "Reinstall the app, or set \"repoRoot\" in " +
                AppSettings.FilePath);

        var args = new List<string>();
        if (installed is null)
            args.AddRange(["--import", "tsx/esm", "native-api/src/main.ts"]);
        else
            args.Add(installed.Value.Bundle);
        args.AddRange(["--port", "0", "--parent-pid", Environment.ProcessId.ToString(), "--mode", mode]);
        if (!string.IsNullOrEmpty(dataDir))
        {
            args.Add("--data-dir");
            args.Add(dataDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName = installed?.NodeExe ?? "node",
            WorkingDirectory = installed is null ? repoRoot! : Path.GetDirectoryName(installed.Value.Bundle)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Credentials travel via the child's environment, never the command line
        // (SPEC §13.1/§23.2 — command lines are visible in process listings).
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new SidecarException(
                "Could not start Node.js. Install Node 22+ and make sure 'node' is on PATH.", ex);
        }
        if (_process is null) throw new SidecarException("Failed to start the sidecar process.");
        AppLog.Info("Sidecar", $"Process started; pid={_process.Id}.");
        _job.Assign(_process); // OS-level cleanup even if this process crashes (SPEC §6.3)

        // Drain stderr in the background so the pipe can't fill and block the child.
        var stderrTail = new Queue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    AppLog.Info("Sidecar.stderr", line);
                    lock (stderrTail)
                    {
                        stderrTail.Enqueue(line);
                        while (stderrTail.Count > 20) stderrTail.Dequeue();
                    }
                }
            }
            catch { /* process ended */ }
        }, CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10)); // SPEC §6.2 default

        string? handshakeLine;
        try
        {
            handshakeLine = await _process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            string tail;
            lock (stderrTail) tail = string.Join('\n', stderrTail);
            Kill();
            throw new SidecarException($"The local data engine did not respond in time.\n{tail}");
        }

        if (handshakeLine is null)
        {
            string tail;
            lock (stderrTail) tail = string.Join('\n', stderrTail);
            Kill();
            throw new SidecarException($"The local data engine exited during startup.\n{tail}");
        }

        SidecarHandshake? handshake;
        try
        {
            handshake = JsonSerializer.Deserialize<SidecarHandshake>(handshakeLine);
        }
        catch (JsonException ex)
        {
            Kill();
            throw new SidecarException("Unrecognized handshake from the local data engine.", ex);
        }

        if (handshake is null || handshake.Event != "ready")
        {
            Kill();
            throw new SidecarException($"Sidecar reported: {handshakeLine}");
        }
        if (handshake.ProtocolVersion != ExpectedProtocolVersion)
        {
            Kill();
            throw new SidecarException(
                $"Protocol mismatch: app expects {ExpectedProtocolVersion}, sidecar speaks {handshake.ProtocolVersion}.");
        }

        // The handshake line (which carries the token) is never logged; stop
        // reading stdout entirely after this point (SPEC §6.2 step 6).
        Handshake = handshake;
        AppLog.Info("Sidecar", $"Handshake accepted; pid={handshake.Pid}, mode={handshake.Mode}, protocol={handshake.ProtocolVersion}.");
        return handshake;
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                AppLog.Breadcrumb("Sidecar", $"Stopping process; pid={_process.Id}.");
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
    }

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
        _process = null;
        _job.Dispose();
    }
}
