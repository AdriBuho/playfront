using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Playfront.Helper;

// Servidor de la tuberia con nombre por la que la interfaz de Playfront (sin permisos) le pide cosas al
// ayudante (SYSTEM). Reglas de seguridad:
//  - Tuberia LOCAL (\\.\pipe\PlayfrontHelper); las named pipes no salen a la red salvo que se pida, y no se
//    pide. Con ACL que permite conectarse a usuarios locales autenticados (la UI de Playfront).
//  - Solo VERBOS FIJOS Y SEGUROS (ver Dispatch). No existe "ejecuta esto": el llamante no puede hacer
//    que el ayudante corra codigo arbitrario, solo disparar acciones concretas ya escritas aqui.
// Protocolo: una linea JSON de peticion { "command": "...", ... } -> una linea JSON de respuesta
// { "ok": bool, "message": "..." }.
public sealed class PipeServer : BackgroundService
{
    public const string PipeName = "PlayfrontHelper";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<PipeServer> _log;

    public PipeServer(ILogger<PipeServer> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Playfront Helper: servidor de tuberia escuchando en \\\\.\\pipe\\{Pipe}", PipeName);
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
                _log.LogError(e, "Error atendiendo la tuberia");
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        // Usuarios locales autenticados (la interfaz de Playfront corre como el usuario) pueden conectarse.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // El propio SYSTEM y los administradores, control total.
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
            _log.LogError(e, "Peticion invalida");
            response = new Response(false, $"peticion invalida: {e.Message}");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), ct);
    }

    // El unico sitio donde se decide QUE puede hacer el ayudante. Verbos concretos y seguros (mas
    // adelante: set-tdp, ...). NUNCA un verbo generico de "ejecuta esto".
    private async Task<Response> DispatchAsync(Request request)
    {
        switch (request.Command?.ToLowerInvariant())
        {
            case "ping":
                var identity = WindowsIdentity.GetCurrent().Name;
                _log.LogInformation("ping atendido (identidad: {Identity})", identity);
                return new Response(true, $"pong — el ayudante corre como {identity}");

            case "steam-status":
            {
                var (installed, path) = SteamInstaller.Detect();
                return new Response(true, installed ? $"Steam instalado en {path}" : "Steam NO instalado");
            }

            case "install-steam":
            {
                _log.LogInformation("install-steam (dryRun={Dry}, force={Force})", request.DryRun, request.Force);
                var (ok, msg) = await SteamInstaller.InstallAsync(request.DryRun, request.Force, _log);
                return new Response(ok, msg);
            }

            default:
                return new Response(false, $"comando desconocido: '{request.Command}'");
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
