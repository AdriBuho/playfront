using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Playfront.App.System;

/// <summary>Respuesta del servicio ayudante a un comando.</summary>
public sealed record HelperResponse(bool Ok, string? Message);

/// <summary>
/// Cliente de la interfaz de Playfront (que corre SIN permisos) para pedirle acciones privilegiadas al
/// servicio ayudante de Playfront (que corre como SYSTEM), por una tuberia con nombre segura. Mismo patron
/// que la app de Xbox hablando con el servicio GamingServices: la UI pide, el ayudante ejecuta.
/// La UI nunca hace el trabajo privilegiado ella misma; solo envia comandos concretos (ping, y mas
/// adelante install-steam, set-tdp...).
/// </summary>
public static class HelperClient
{
    private const string PipeName = "PlayfrontHelper";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<HelperResponse> SendAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var connectMs = (int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectMs, ct);

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(pipe, Encoding.UTF8);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { Command = command }).AsMemory(), ct);
        var line = await reader.ReadLineAsync(ct);
        if (line == null)
        {
            return new HelperResponse(false, "sin respuesta del ayudante");
        }

        return JsonSerializer.Deserialize<HelperResponse>(line, JsonOptions)
               ?? new HelperResponse(false, "respuesta invalida del ayudante");
    }

    /// <summary>¿Esta el servicio ayudante instalado y en marcha? (un ping rapido, sin lanzar excepciones).</summary>
    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            return (await SendAsync("ping", TimeSpan.FromMilliseconds(800))).Ok;
        }
        catch
        {
            return false;
        }
    }
}
