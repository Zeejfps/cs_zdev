using System.Text.Json.Nodes;

namespace Zdev.Agents.Acp;

/// <summary>One option an agent offered on a permission request.</summary>
public sealed record AcpPermissionOption(string OptionId, string Name, AcpPermissionOptionKind Kind);

public enum AcpPermissionOptionKind
{
    AllowOnce,
    AllowAlways,
    RejectOnce,
    RejectAlways,
}

/// <summary>What a tool call does, as the agent classifies it.</summary>
public enum AcpToolKind
{
    Read,
    Edit,
    Delete,
    Move,
    Search,
    Execute,
    Think,
    Fetch,
    SwitchMode,
    Other,
}

/// <summary>
/// A <c>session/request_permission</c> as the policy reads it: the tool call it is about, the
/// options offered, and whatever the adapter says about which MCP server the tool belongs to.
/// Adapters name that differently — Claude on the tool call, Codex on the earlier
/// <c>tool_call</c> update, Gemini only in its "Always Allow &lt;server&gt;" option — so the parse
/// collects every marker into <see cref="McpServer"/>. <see cref="Command"/> is the command line
/// of a shell call, where the adapter passes it in the tool call's input. <see cref="Edits"/> are
/// the changes a file edit would make, and <see cref="Paths"/> every file the call names.
/// </summary>
public sealed record AcpPermissionRequest(
    string ToolCallId,
    string Title,
    AcpToolKind Kind,
    string? McpServer,
    bool IsMcpApproval,
    IReadOnlyList<AcpPermissionOption> Options,
    string? Command = null)
{
    public IReadOnlyList<AcpFileEdit> Edits { get; init; } = [];

    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>One change an edit would make to a file: the text it replaces, none when it writes the
/// whole file, and the text it puts there.</summary>
public sealed record AcpFileEdit(string Path, string? OldText, string NewText);

/// <summary>What the client answers a permission request with.</summary>
public abstract record AcpPermissionDecision
{
    public sealed record Select(string OptionId, AcpPermissionVerdict Verdict) : AcpPermissionDecision;

    /// <summary>The policy has no opinion: the user decides.</summary>
    public sealed record AskUser : AcpPermissionDecision;
}

public enum AcpPermissionVerdict
{
    Allowed,
    Rejected,
}

/// <summary>
/// The write guard for an agent run over ACP: reads are allowed, and shell commands, file edits and
/// MCP tools as the user's <see cref="AcpApprovalRules"/> say; the app's own MCP tools are allowed
/// each time they are asked about, and deletes, mode switches and the rest are put to the user. The
/// same rules for every harness, which is why the guard lives in the client rather than in each
/// CLI's flags.
/// </summary>
public static class AcpPermissionPolicy
{
    /// <summary>Decides with <see cref="AcpApprovalRules.AskingForEdits"/>: every edit put to the user.</summary>
    /// <param name="ownServer">The app's MCP server, whose tools are allowed; null when the session has none.</param>
    public static AcpPermissionDecision Decide(AcpPermissionRequest request, string? ownServer) =>
        Decide(request, ownServer, AcpApprovalRules.AskingForEdits, null);

    /// <param name="ownServer">The app's MCP server, whose tools are allowed; null when the session has none.</param>
    /// <param name="workspace">The session's folder, the edits inside which <paramref name="rules"/> may
    /// allow; null when it allows none.</param>
    public static AcpPermissionDecision Decide(AcpPermissionRequest request, string? ownServer, AcpApprovalRules rules, string? workspace)
    {
        // Once, every time, rather than always: Claude Code writes an "always" into the repository's
        // own .claude/settings.local.json, which is the user's file, not the session's.
        if (request.McpServer is null && request.IsMcpApproval) return new AcpPermissionDecision.AskUser();
        if (request.McpServer is { } server
            && (ownServer is not null && string.Equals(server, ownServer, StringComparison.OrdinalIgnoreCase) || rules.AllowsServer(server)))
            return AllowOnce(request);

        // Codex files another server's MCP tool calls as execute: only a shell command is the agent's own.
        return request.Kind switch
        {
            AcpToolKind.Execute when request.McpServer is not null => new AcpPermissionDecision.AskUser(),
            AcpToolKind.Execute => AllowsCommand(request.Command ?? request.Title, rules) ? AllowOnce(request) : new AcpPermissionDecision.AskUser(),
            AcpToolKind.Read or AcpToolKind.Search or AcpToolKind.Think or AcpToolKind.Fetch => AllowOnce(request),
            AcpToolKind.Edit or AcpToolKind.Move when EditsStayIn(request, rules, workspace) => AllowOnce(request),
            AcpToolKind.Edit or AcpToolKind.Delete or AcpToolKind.Move or AcpToolKind.SwitchMode or AcpToolKind.Other =>
                new AcpPermissionDecision.AskUser(),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown tool kind."),
        };
    }

    /// <summary>Whether <paramref name="rules"/> let <paramref name="command"/> run without asking:
    /// asking before wins over always allowing, and that over what other commands get.</summary>
    public static bool AllowsCommand(string command, AcpApprovalRules rules)
    {
        if (rules.AskBefore.Any(entry => AcpCommandLine.Runs(command, entry))) return false;
        if (AcpCommandLine.StartsEveryPart(command, rules.AlwaysAllow)) return true;
        return rules.OtherCommands == AcpCommandFallback.Allow;
    }

    /// <summary>Whether every file <paramref name="request"/> names is inside <paramref name="workspace"/>
    /// and protected by none of the rules' entries; never for a request that names none, which can't be told.</summary>
    private static bool EditsStayIn(AcpPermissionRequest request, AcpApprovalRules rules, string? workspace)
    {
        if (!rules.AllowsWorkspaceEdits || workspace is null || request.Paths.Count == 0) return false;
        foreach (var path in request.Paths)
        {
            if (AcpWorkspaceFiles.Inside(workspace, path) is not { } relative) return false;
            if (rules.ProtectedFiles.Any(entry => AcpWorkspaceFiles.Matches(relative, entry))) return false;
        }

        return true;
    }

    /// <summary>Whether a command line runs <c>git push</c> anywhere in it, global options between
    /// the two words included.</summary>
    public static bool Pushes(string command) => AcpCommandLine.Runs(command, "git push");

    private static AcpPermissionDecision AllowOnce(AcpPermissionRequest request)
    {
        foreach (var wanted in new[] { AcpPermissionOptionKind.AllowOnce, AcpPermissionOptionKind.AllowAlways })
            foreach (var option in request.Options)
                if (option.Kind == wanted)
                    return new AcpPermissionDecision.Select(option.OptionId, AcpPermissionVerdict.Allowed);
        return new AcpPermissionDecision.AskUser();
    }

    /// <summary>Parses a request's params. <paramref name="announcedServers"/> maps the tool call ids
    /// the agent announced in <c>tool_call</c> updates to the MCP server they named, if any.</summary>
    public static AcpPermissionRequest? Parse(JsonNode? parameters, IReadOnlyDictionary<string, string> announcedServers)
    {
        if (parameters is not JsonObject root || root["toolCall"] is not JsonObject toolCall) return null;
        if (Text(toolCall["toolCallId"]) is not { } id) return null;

        var options = new List<AcpPermissionOption>();
        if (root["options"] is JsonArray offered)
        {
            foreach (var node in offered)
            {
                if (node is not JsonObject option) continue;
                if (Text(option["optionId"]) is not { } optionId) continue;
                if (ParseOptionKind(Text(option["kind"])) is not { } kind) continue;
                options.Add(new AcpPermissionOption(optionId, Text(option["name"]) ?? optionId, kind));
            }
        }

        var server = Text(toolCall["_meta"]?["claudeCode"]?["mcpServer"]?["name"])
                     ?? ClaudeToolServer(Text(toolCall["name"]))
                     ?? (announcedServers.TryGetValue(id, out var announced) ? announced : null)
                     ?? GeminiServer(root["options"]);
        var isMcpApproval = root["_meta"]?["is_mcp_tool_approval"] is JsonValue flag && flag.TryGetValue<bool>(out var set) && set;

        var edits = EditsOf(toolCall);
        var paths = edits.Select(e => e.Path).ToList();
        if (toolCall["locations"] is JsonArray locations)
            paths.AddRange(locations.Select(l => Text(l?["path"])).OfType<string>());

        return new AcpPermissionRequest(
            id,
            Text(toolCall["title"]) ?? string.Empty,
            ParseToolKind(Text(toolCall["kind"])),
            server,
            isMcpApproval,
            options,
            CommandOf(toolCall["rawInput"]))
        {
            Edits = edits,
            Paths = paths.Distinct().ToList(),
        };
    }

    // ACP's diff content where the adapter sends it; Claude Code's own Edit, MultiEdit and Write
    // input where it doesn't.
    private static IReadOnlyList<AcpFileEdit> EditsOf(JsonObject toolCall)
    {
        var edits = new List<AcpFileEdit>();
        if (toolCall["content"] is JsonArray content)
        {
            foreach (var node in content)
            {
                if (Text(node?["type"]) != "diff" || Text(node?["path"]) is not { } path || Text(node?["newText"]) is not { } newText) continue;
                edits.Add(new AcpFileEdit(path, Text(node?["oldText"]), newText));
            }
        }

        if (edits.Count > 0 || toolCall["rawInput"] is not JsonObject input || Text(input["file_path"]) is not { } file) return edits;
        if (Text(input["new_string"]) is { } replacement) edits.Add(new AcpFileEdit(file, Text(input["old_string"]) ?? string.Empty, replacement));
        else if (Text(input["content"]) is { } written) edits.Add(new AcpFileEdit(file, null, written));
        else if (input["edits"] is JsonArray many)
            foreach (var edit in many)
                if (Text(edit?["new_string"]) is { } each)
                    edits.Add(new AcpFileEdit(file, Text(edit?["old_string"]) ?? string.Empty, each));
        return edits;
    }

    // Claude and Gemini pass the command as a string; Codex as an argv array, which may be a shell
    // running a script.
    private static string? CommandOf(JsonNode? input) => input?["command"] switch
    {
        JsonValue value => Text(value),
        JsonArray argv => string.Join(' ', argv.Select(Text).OfType<string>()),
        _ => null,
    };

    /// <summary>The MCP server a <c>tool_call</c> update names, where the adapter says so there
    /// (Codex puts it in <c>rawInput.server</c>).</summary>
    public static string? AnnouncedServer(JsonNode? update)
    {
        if (update?["_meta"]?["is_mcp_tool_call"] is JsonValue flag && flag.TryGetValue<bool>(out var set) && set)
            return Text(update["rawInput"]?["server"]);
        return Text(update?["_meta"]?["claudeCode"]?["mcpServer"]?["name"]);
    }

    public static AcpToolKind ParseToolKind(string? kind) => kind switch
    {
        "read" => AcpToolKind.Read,
        "edit" => AcpToolKind.Edit,
        "delete" => AcpToolKind.Delete,
        "move" => AcpToolKind.Move,
        "search" => AcpToolKind.Search,
        "execute" => AcpToolKind.Execute,
        "think" => AcpToolKind.Think,
        "fetch" => AcpToolKind.Fetch,
        "switch_mode" => AcpToolKind.SwitchMode,
        _ => AcpToolKind.Other,
    };

    private static AcpPermissionOptionKind? ParseOptionKind(string? kind) => kind switch
    {
        "allow_once" => AcpPermissionOptionKind.AllowOnce,
        "allow_always" => AcpPermissionOptionKind.AllowAlways,
        "reject_once" => AcpPermissionOptionKind.RejectOnce,
        "reject_always" => AcpPermissionOptionKind.RejectAlways,
        _ => null,
    };

    // Claude Code names MCP tools mcp__<server>__<tool>.
    private static string? ClaudeToolServer(string? name)
    {
        const string prefix = "mcp__";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = name.IndexOf("__", prefix.Length, StringComparison.Ordinal);
        return end > prefix.Length ? name[prefix.Length..end] : null;
    }

    // Gemini offers "Always Allow <server>" under the option id proceed_always_server.
    private static string? GeminiServer(JsonNode? options)
    {
        const string prefix = "Always Allow ";
        if (options is not JsonArray offered) return null;
        foreach (var node in offered)
        {
            if (Text(node?["optionId"]) != "proceed_always_server") continue;
            var name = Text(node?["name"]);
            if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
        }

        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
