using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notipet.Shared;

// One source-generated context for the whole wire surface.
//
// The CLI publishes with NativeAOT, where reflection-based System.Text.Json
// trim-warns and can fail at runtime, so source generation is mandatory rather
// than an optimisation. The daemon uses the same context so both ends serialise
// identically.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NotifyRequest))]
[JsonSerializable(typeof(NotifyResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(AlertsRequest))]
[JsonSerializable(typeof(AlertsResponse))]
[JsonSerializable(typeof(AckRequest))]
[JsonSerializable(typeof(AckResponse))]
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(ResolveResponse))]
[JsonSerializable(typeof(MuteRequest))]
[JsonSerializable(typeof(MuteResponse))]
[JsonSerializable(typeof(PresenceRequest))]
[JsonSerializable(typeof(PresenceResponse))]
[JsonSerializable(typeof(ClearHistoryResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(ChannelsResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(RuntimeInfo))]
[JsonSerializable(typeof(AgentHookEvent))]
[JsonSerializable(typeof(CodexNotifyEvent))]
public sealed partial class NotipetJsonContext : JsonSerializerContext
{
}

public static class NotipetJson
{
    public static readonly NotipetJsonContext Compact = NotipetJsonContext.Default;

    // Indented output for runtime.json and settings.json, which humans read.
    public static readonly NotipetJsonContext Pretty =
        new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}
