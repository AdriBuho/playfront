using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Playfront.Helper;

// Named-pipe server through which the unprivileged Playfront UI asks the SYSTEM helper for work.
// Two security rules hold the whole design together:
//  - The pipe is LOCAL (\\.\pipe\PlayfrontHelper). Named pipes are not exposed over the network
//    unless asked for, and this does not ask. Its ACL lets locally authenticated users connect.
//  - Only FIXED, SPECIFIC verbs are accepted (see DispatchAsync). There is deliberately no "run this"
//    command: a caller cannot make the helper execute arbitrary code, only trigger actions written
//    here.
// Protocol: one JSON request line { "command": "...", ... } -> one JSON reply line
// { "ok": bool, "message": "..." }.
public sealed class PipeServer : BackgroundService
{
    public const string PipeName = "PlayfrontHelper";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<PipeServer> _log;

    public PipeServer(ILogger<PipeServer> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Playfront Helper: pipe server listening on \\\\.\\pipe\\{Pipe}", PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreatePipe();
                await server.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Error serving the pipe");
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        // Locally authenticated users may connect: the Playfront UI runs as the logged-in user.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // SYSTEM itself and administrators get full control.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        Response response;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(line, JsonOptions) ?? new Request();
            response = await DispatchAsync(request);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Invalid request");
            response = new Response(false, $"invalid request: {e.Message}");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), ct);
    }

    // The only place that decides WHAT the helper is allowed to do. Specific, safe verbs (later:
    // set-tdp, ...). Never a generic "execute this".
    private async Task<Response> DispatchAsync(Request request)
    {
        switch (request.Command?.ToLowerInvariant())
        {
            case "ping":
                var identity = WindowsIdentity.GetCurrent().Name;
                _log.LogInformation("ping served (identity: {Identity})", identity);
                return new Response(true, $"pong — helper running as {identity}");

            case "steam-status":
            {
                var (installed, path) = SteamInstaller.Detect();
                return new Response(true, installed ? $"Steam installed at {path}" : "Steam NOT installed");
            }

            case "install-steam":
            {
                _log.LogInformation("install-steam (dryRun={Dry}, force={Force})", request.DryRun, request.Force);
                var (ok, msg) = await SteamInstaller.InstallAsync(request.DryRun, request.Force, _log);
                return new Response(ok, msg);
            }

            default:
                return new Response(false, $"unknown command: '{request.Command}'");
        }
    }

    private sealed record Request
    {
        public string? Command { get; init; }
        public bool DryRun { get; init; }
        public bool Force { get; init; }
    }

    private sealed record Response(bool Ok, string Message);
}
