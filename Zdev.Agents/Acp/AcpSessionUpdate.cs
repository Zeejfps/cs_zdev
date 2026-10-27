using System.Text.Json.Nodes;

namespace Zdev.Agents.Acp;

/// <summary>One block of a user turn.</summary>
public abstract record AcpContent
{
    public sealed record Prose(string Value) : AcpContent;

    /// <summary>A file's text attached as context; <paramref name="Uri"/> may name lines in its fragment.</summary>
    public sealed record Resource(Uri Uri, string Text) : AcpContent;

    /// <summary>A picture, its encoded bytes of the type <paramref name="MimeType"/> names, such as
    /// <c>image/png</c>.</summary>
    public sealed record Image(string MimeType, byte[] Data) : AcpContent;
}

/// <summary>What a <c>session/update</c> notification says, in the cases the app shows.</summary>
public abstract record AcpSessionUpdate
{
    /// <summary>A fragment of the agent's reply to the user.</summary>
    public sealed record MessageChunk(string Text) : AcpSessionUpdate;

    /// <summary>A fragment of a message the user sent, as an agent replays it while it loads a session.</summary>
    public sealed record UserMessageChunk(string Text) : AcpSessionUpdate;

    /// <summary>A picture in a message the user sent, as an agent replays it while it loads a session:
    /// its encoded bytes of the type <paramref name="MimeType"/> names.</summary>
    public sealed record UserImage(string MimeType, byte[] Data) : AcpSessionUpdate;

    /// <summary>A fragment of the agent's reasoning.</summary>
    public sealed record ThoughtChunk(string Text) : AcpSessionUpdate;

    /// <summary>A tool call started, or changed title or status.</summary>
    public sealed record ToolCall(string Id, string? Title, AcpToolKind? Kind, AcpToolCallStatus? Status) : AcpSessionUpdate;

    /// <summary>The agent's own plan, replaced whole.</summary>
    public sealed record Plan(IReadOnlyList<string> Entries) : AcpSessionUpdate;

    /// <summary>Anything else: usage, commands, session info.</summary>
    public sealed record Other(string Kind) : AcpSessionUpdate;

    public static AcpSessionUpdate? Parse(JsonNode? update)
    {
        if (update is not JsonObject body || StringOf(body["sessionUpdate"]) is not { } kind) return null;
        switch (kind)
        {
            case "agent_message_chunk":
                return new MessageChunk(ContentText(body["content"]));
            case "user_message_chunk":
                if (ContentImage(body["content"]) is { } image) return image;
                return new UserMessageChunk(ContentText(body["content"]));
            case "agent_thought_chunk":
                return new ThoughtChunk(ContentText(body["content"]));
            case "tool_call":
            case "tool_call_update":
                if (StringOf(body["toolCallId"]) is not { } id) return new Other(kind);
                return new ToolCall(
                    id,
                    StringOf(body["title"]),
                    StringOf(body["kind"]) is { } toolKind ? AcpPermissionPolicy.ParseToolKind(toolKind) : null,
                    ParseStatus(StringOf(body["status"])));
            case "plan":
                var entries = new List<string>();
                if (body["entries"] is JsonArray list)
                    foreach (var entry in list)
                        if (StringOf(entry?["content"]) is { } content)
                            entries.Add(content);
                return new Plan(entries);
            default:
                return new Other(kind);
        }
    }

    private static AcpToolCallStatus? ParseStatus(string? status) => status switch
    {
        "pending" => AcpToolCallStatus.Pending,
        "in_progress" => AcpToolCallStatus.InProgress,
        "completed" => AcpToolCallStatus.Completed,
        "failed" => AcpToolCallStatus.Failed,
        _ => null,
    };

    private static string ContentText(JsonNode? content) =>
        StringOf(content?["type"]) == "text" ? StringOf(content?["text"]) ?? string.Empty : string.Empty;

    /// <summary>The picture <paramref name="content"/> holds, or null where it is no image block or its
    /// data is no base64.</summary>
    private static UserImage? ContentImage(JsonNode? content)
    {
        if (StringOf(content?["type"]) != "image") return null;
        if (StringOf(content?["mimeType"]) is not { } mimeType || StringOf(content?["data"]) is not { } data) return null;
        var bytes = new byte[data.Length * 3 / 4];
        if (!Convert.TryFromBase64String(data, bytes, out var written)) return null;
        return new UserImage(mimeType, bytes[..written]);
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public enum AcpToolCallStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>Why a prompt turn ended.</summary>
public enum AcpStopReason
{
    EndTurn,
    MaxTokens,
    MaxTurnRequests,
    Refusal,
    Cancelled,
}
