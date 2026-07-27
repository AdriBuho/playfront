using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Playfront.App.System;

/// <summary>Reply from the helper service to a command.</summary>
public sealed record HelperResponse(bool Ok, string? Message);

/// <summary>
/// Client used by the Playfront UI (which runs unprivileged) to ask the Playfront helper service
/// (which runs as SYSTEM) to perform privileged actions, over a named pipe.
///
/// The UI never does privileged work itself; it only sends specific commands (ping, and later
/// install-steam, set-tdp...). That split is why the shell itself never needs to be elevated.
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
            return new HelperResponse(false, "no reply from the helper service");
        }

        return JsonSerializer.Deserialize<HelperResponse>(line, JsonOptions)
               ?? new HelperResponse(false, "invalid reply from the helper service");
    }

    /// <summary>Is the helper service installed and running? A quick ping that never throws.</summary>
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
