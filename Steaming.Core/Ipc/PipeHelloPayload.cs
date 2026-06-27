using System.Reflection;
using System.Text.Json;

namespace Steaming.Core.Ipc;

public sealed class PipeHelloPayload
{
    public const int CurrentProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Role { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string Version { get; init; } = "";
    public string[] Capabilities { get; init; } = [];

    public static PipeHelloPayload CreateAppHello() => new()
    {
        Role = "app",
        ProtocolVersion = CurrentProtocolVersion,
        Version = ResolveAppVersion(),
        Capabilities =
        [
            "render_alert_v2",
            "render_chat",
            "label_layouts",
            "goal_layouts",
            "emoji_rain",
            "chat_source_list"
        ]
    };

    public byte[] Serialize()
        => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static PipeHelloPayload? Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PipeHelloPayload>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
