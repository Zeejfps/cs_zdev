using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Zdev.Common;

namespace Zdev.Agents.Acp;

/// <summary>Asks the user about a permission request the policy leaves open. Answers the chosen
/// option id, or null to cancel the tool call.</summary>
public interface IAcpPermissionPrompt
{
    Task<string?> AskAsync(AcpPermissionRequest request, CancellationToken ct);

    /// <summary>The answers the user gave ahead of time, read again for each request before it is
    /// put to them, so a change applies from the next one. Called on the connection's reader thread.</summary>
    AcpApprovalRules Rules => AcpApprovalRules.AskingForEdits;
}

/// <summary>The app's MCP server as a session is handed it. A session opened without one gets no MCP
/// server from the app, and no MCP tool passes the write guard as the app's own.</summary>
public sealed record AcpMcpServer(string Name, Uri Url);

/// <summary>How starting an agent went.</summary>
public abstract record AcpStart
{
    public sealed record Started(AcpAgentConnection Connection) : AcpStart;

    public sealed record Failed(string Reason) : AcpStart;
}

/// <summary>How starting an agent on a saved session went: the session is open again, the agent
/// could not load it and a new session is open instead, or the agent did not start.</summary>
public abstract record AcpLoad
{
    /// <summary>The saved session is open again. The history the agent replayed while loading it
    /// reached the update handler before this was returned.</summary>
    public sealed record Loaded(AcpAgentConnection Connection) : AcpLoad;

    /// <summary>The agent does not load sessions, or refused to load this one, for
    /// <paramref name="Reason"/>; a new session is open on <paramref name="Connection"/> instead.</summary>
    public sealed record OpenedNew(AcpAgentConnection Connection, string Reason) : AcpLoad;

    public sealed record Failed(string Reason) : AcpLoad;
}

/// <summary>
/// One agent run over the Agent Client Protocol: the adapter process, its one session, and the
/// write guard. Every permission request goes through <see cref="AcpPermissionPolicy"/> with the
/// prompt's <see cref="IAcpPermissionPrompt.Rules"/>, edits judged against the session's folder, and
/// the session is put in its harness's asking mode first, so that no write is decided inside the CLI.
/// Events are raised on the reader's thread.
/// </summary>
public sealed class AcpAgentConnection : IAsyncDisposable, IAcpClientMessages
{
    /// <summary>Whether the agent takes a file's text attached to a turn as context, rather than
    /// only prose.</summary>
    public bool EmbedsContext { get; private set; }

    /// <summary>Whether the agent takes pictures in a turn, as <see cref="AcpContent.Image"/>.</summary>
    public bool AcceptsImages { get; private set; }

    /// <summary>Whether the agent said, as it started, that it can load a saved session, so a later
    /// <see cref="StartLoadingAsync"/> can open this one again.</summary>
    public bool CanLoadSession { get; private set; }

    /// <summary>The id of the open session, to load it again later.</summary>
    public string SessionId => _sessionId ?? throw new InvalidOperationException("The session is not open.");

    private const int StderrLines = 40;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(90);

    private readonly AcpJsonRpc _rpc;
    private readonly Process? _process;

    /// <summary>The processes the agent runs as, all ended with it; none for a connection over the
    /// caller's streams.</summary>
    private readonly AgentProcessTree? _tree;
    private readonly IAcpPermissionPrompt _prompt;
    private readonly string? _ownServer;
    private string? _workspace;
    private readonly Dictionary<string, string> _announcedServers = new();
    private readonly Queue<string> _stderr = new();
    private string? _sessionId;
    private IReadOnlySet<string> _modes = new HashSet<string>();
    private int _disposed;

    private AcpAgentConnection(TextReader incoming, TextWriter outgoing, Process? process, IAcpPermissionPrompt prompt, string? ownServer)
    {
        _process = process;
        _tree = process is null ? null : AgentProcessTree.Adopt(process);
        _prompt = prompt;
        _ownServer = ownServer;
        _rpc = new AcpJsonRpc(incoming, outgoing, this);
    }

    /// <summary>A fragment of the agent's output, a tool call, or its plan.</summary>
    public event Action<AcpSessionUpdate>? Updated;

    /// <summary>A permission request and what was answered, including the ones the user decided.</summary>
    public event Action<AcpPermissionRequest, AcpPermissionVerdict>? PermissionDecided;

    /// <summary>Every protocol line in either direction, for a trace. Raised on the thread that moved it.</summary>
    public event Action<AcpTraffic>? Traffic
    {
        add => _rpc.Traffic += value;
        remove => _rpc.Traffic -= value;
    }

    /// <summary>Completes when the agent's output ends: it exited, crashed, or was disposed.</summary>
    public Task Closed => _rpc.Closed;

    /// <summary>The tail of what the adapter wrote to stderr, for a failure message.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderr) return string.Join('\n', _stderr);
        }
    }

    public static async Task<AcpStart> StartAsync(
        AcpHarness harness,
        string workingDirectory,
        AcpMcpServer? server,
        IAcpPermissionPrompt prompt,
        IServerEnvironment environment,
        CancellationToken ct)
    {
        if (Launch(harness, workingDirectory, server, prompt, environment, out var notLaunched) is not { } connection)
            return new AcpStart.Failed(notLaunched);

        var failure = await connection.OpenForHarnessAsync(harness, workingDirectory, server, ct).ConfigureAwait(false);
        if (failure is null) return new AcpStart.Started(connection);

        await connection.DisposeAsync().ConfigureAwait(false);
        return new AcpStart.Failed(connection.WithStderrTail(failure));
    }

    /// <summary>
    /// Starts <paramref name="harness"/>'s adapter as <see cref="StartAsync"/> does, but opens the
    /// saved session <paramref name="sessionId"/> with <c>session/load</c> instead of a new one. The
    /// agent replays the session's history as <c>session/update</c> notifications while it loads, so
    /// <paramref name="updated"/> is subscribed to <see cref="Updated"/> before the session opens: it
    /// hears the history, then the live updates, in the order the agent sent them. An agent that does
    /// not advertise <c>loadSession</c>, or refuses the load, gets a new session instead, which the
    /// result tells apart.
    /// </summary>
    public static async Task<AcpLoad> StartLoadingAsync(
        AcpHarness harness,
        string workingDirectory,
        string sessionId,
        AcpMcpServer? server,
        IAcpPermissionPrompt prompt,
        Action<AcpSessionUpdate> updated,
        IServerEnvironment environment,
        CancellationToken ct)
    {
        if (Launch(harness, workingDirectory, server, prompt, environment, out var notLaunched) is not { } connection)
            return new AcpLoad.Failed(notLaunched);

        connection.Updated += updated;
        var load = await connection.LoadForHarnessAsync(harness, workingDirectory, sessionId, server, ct).ConfigureAwait(false);
        if (load is not AcpLoad.Failed failed) return load;

        await connection.DisposeAsync().ConfigureAwait(false);
        return new AcpLoad.Failed(connection.WithStderrTail(failed.Reason));
    }

    /// <summary>Starts the adapter's process and reads its stderr; null, with why, when it could not.</summary>
    private static AcpAgentConnection? Launch(
        AcpHarness harness,
        string workingDirectory,
        AcpMcpServer? server,
        IAcpPermissionPrompt prompt,
        IServerEnvironment environment,
        out string failure)
    {
        failure = string.Empty;
        if (environment.ResolveCommand(harness.Command) is not { } executable)
        {
            failure = $"'{harness.Command}' was not found on PATH.";
            return null;
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in harness.Args) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment.Variables) start.Environment[key] = value;
        foreach (var (key, value) in harness.Environment) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            failure = $"'{harness.Command}' could not be started: {e.Message}";
            return null;
        }

        var connection = new AcpAgentConnection(process.StandardOutput, process.StandardInput, process, prompt, server?.Name);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) connection.NoteStderr(line);
        };
        process.BeginErrorReadLine();
        return connection;
    }

    private string WithStderrTail(string failure)
    {
        var tail = StderrTail;
        return tail.Length == 0 ? failure : $"{failure}\n{tail}";
    }

    /// <summary>
    /// Opens a session with an agent the caller runs at the far end of <paramref name="incoming"/>
    /// and <paramref name="outgoing"/>, as <see cref="StartAsync"/> does with the process it starts:
    /// the same handshake, modes and write guard. For an agent on another transport, or one a test
    /// plays in process. The connection owns no process, and disposing it stops reading but leaves
    /// the streams to the caller.
    /// </summary>
    public static async Task<AcpStart> ConnectAsync(
        TextReader incoming,
        TextWriter outgoing,
        AcpHarness harness,
        string workingDirectory,
        AcpMcpServer? server,
        IAcpPermissionPrompt prompt,
        CancellationToken ct)
    {
        var connection = Over(incoming, outgoing, prompt, server?.Name);
        var failure = await connection.OpenForHarnessAsync(harness, workingDirectory, server, ct).ConfigureAwait(false);
        if (failure is null) return new AcpStart.Started(connection);

        await connection.DisposeAsync().ConfigureAwait(false);
        return new AcpStart.Failed(failure);
    }

    /// <summary>
    /// Opens the saved session <paramref name="sessionId"/> with an agent the caller runs at the far
    /// end of the streams, as <see cref="StartLoadingAsync"/> does with the process it starts, and as
    /// <see cref="ConnectAsync"/> opens a new one: <paramref name="updated"/> hears the replayed
    /// history and then the live updates, and an agent that can't load the session gets a new one.
    /// </summary>
    public static async Task<AcpLoad> ConnectLoadingAsync(
        TextReader incoming,
        TextWriter outgoing,
        AcpHarness harness,
        string workingDirectory,
        string sessionId,
        AcpMcpServer? server,
        IAcpPermissionPrompt prompt,
        Action<AcpSessionUpdate> updated,
        CancellationToken ct)
    {
        var connection = Over(incoming, outgoing, prompt, server?.Name);
        connection.Updated += updated;
        var load = await connection.LoadForHarnessAsync(harness, workingDirectory, sessionId, server, ct).ConfigureAwait(false);
        if (load is AcpLoad.Failed) await connection.DisposeAsync().ConfigureAwait(false);
        return load;
    }

    /// <summary>Opens the session in <paramref name="harness"/>'s asking mode and checks it offers the
    /// chat mode too. Answers why it could not, or null; disposes the connection when it throws.</summary>
    private async Task<string?> OpenForHarnessAsync(AcpHarness harness, string workingDirectory, AcpMcpServer? server, CancellationToken ct)
    {
        try
        {
            var failure = await OpenAsync(workingDirectory, server, harness.AskingMode, harness.SessionMetaJson, ct).ConfigureAwait(false);
            if (failure is null && harness.ChatMode is { } chatMode && !Offers(chatMode))
                failure = $"The agent offers no '{chatMode}' mode.";
            return failure;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Loads <paramref name="sessionId"/>, or opens a new session when the agent can't, in
    /// <paramref name="harness"/>'s asking mode, and checks it offers the chat mode too. Disposes the
    /// connection when it throws.</summary>
    private async Task<AcpLoad> LoadForHarnessAsync(AcpHarness harness, string workingDirectory, string sessionId, AcpMcpServer? server,
        CancellationToken ct)
    {
        try
        {
            var load = await OpenLoadingAsync(workingDirectory, sessionId, server, harness.AskingMode, harness.SessionMetaJson, ct)
                .ConfigureAwait(false);
            if (load is not AcpLoad.Failed && harness.ChatMode is { } chatMode && !Offers(chatMode))
                return new AcpLoad.Failed($"The agent offers no '{chatMode}' mode.");
            return load;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A connection over given streams, for tests: no process to own.</summary>
    internal static AcpAgentConnection Over(TextReader incoming, TextWriter outgoing, IAcpPermissionPrompt prompt, string? ownServer) =>
        new(incoming, outgoing, null, prompt, ownServer);

    /// <summary>Handshakes, opens the session, and puts it in <paramref name="askingMode"/>.
    /// Answers why it could not, or null.</summary>
    internal async Task<string?> OpenAsync(
        string workingDirectory, AcpMcpServer? server, string askingMode, string? sessionMetaJson, CancellationToken ct)
    {
        _workspace = workingDirectory;
        _rpc.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            await InitializeAsync(timeout.Token).ConfigureAwait(false);
            return await NewSessionAsync(workingDirectory, server, sessionMetaJson, timeout.Token).ConfigureAwait(false)
                   ?? await EnterAskingModeAsync(askingMode, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (DescribeStartFailure(e, ct) is { } failure)
        {
            return failure;
        }
    }

    /// <summary>Handshakes, loads <paramref name="sessionId"/> or, when the agent can't, opens a new
    /// session, and puts it in <paramref name="askingMode"/>.</summary>
    internal async Task<AcpLoad> OpenLoadingAsync(
        string workingDirectory, string sessionId, AcpMcpServer? server, string askingMode, string? sessionMetaJson, CancellationToken ct)
    {
        _workspace = workingDirectory;
        _rpc.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            await InitializeAsync(timeout.Token).ConfigureAwait(false);
            var notLoaded = await LoadSessionAsync(workingDirectory, sessionId, server, sessionMetaJson, timeout.Token).ConfigureAwait(false);
            if (notLoaded is not null
                && await NewSessionAsync(workingDirectory, server, sessionMetaJson, timeout.Token).ConfigureAwait(false) is { } noSession)
                return new AcpLoad.Failed(noSession);
            if (await EnterAskingModeAsync(askingMode, timeout.Token).ConfigureAwait(false) is { } unguarded)
                return new AcpLoad.Failed(unguarded);
            return notLoaded is null ? new AcpLoad.Loaded(this) : new AcpLoad.OpenedNew(this, notLoaded);
        }
        catch (Exception e) when (DescribeStartFailure(e, ct) is { } failure)
        {
            return new AcpLoad.Failed(failure);
        }
    }

    /// <summary>Why a start that threw <paramref name="e"/> failed, or null for an exception a start
    /// does not expect, which is left to propagate.</summary>
    private static string? DescribeStartFailure(Exception e, CancellationToken ct) => e switch
    {
        AcpRpcException rpc => $"The agent refused to start: {rpc.Message}",
        AcpClosedException => "The agent exited while starting.",
        OperationCanceledException when !ct.IsCancellationRequested => "The agent did not answer in time.",
        _ => null,
    };

    private async Task InitializeAsync(CancellationToken ct)
    {
        var initialized = await _rpc.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject { ["name"] = "DiffDino", ["version"] = "1" },
        }, ct).ConfigureAwait(false);
        var capabilities = initialized?["agentCapabilities"];
        EmbedsContext = capabilities?["promptCapabilities"]?["embeddedContext"] is JsonValue embeds
                        && embeds.TryGetValue<bool>(out var embedded) && embedded;
        AcceptsImages = capabilities?["promptCapabilities"]?["image"] is JsonValue images
                        && images.TryGetValue<bool>(out var accepted) && accepted;
        CanLoadSession = capabilities?["loadSession"] is JsonValue loads && loads.TryGetValue<bool>(out var loadable) && loadable;
    }

    /// <summary>Opens a new session. Answers why it could not, or null.</summary>
    private async Task<string?> NewSessionAsync(string workingDirectory, AcpMcpServer? server, string? sessionMetaJson, CancellationToken ct)
    {
        var session = await _rpc.RequestAsync("session/new", new JsonObject
        {
            ["_meta"] = sessionMetaJson is null ? null : JsonNode.Parse(sessionMetaJson),
            ["cwd"] = workingDirectory,
            ["mcpServers"] = McpServersJson(server),
        }, ct).ConfigureAwait(false);

        if (session?["sessionId"] is not JsonValue id || !id.TryGetValue<string>(out var sessionId))
            return "The agent opened no session.";
        _sessionId = sessionId;
        _modes = OfferedModes(session);
        return null;
    }

    /// <summary>Loads the saved session <paramref name="sessionId"/>, which the agent replays as
    /// <c>session/update</c> notifications before it answers. Answers why it was not loaded, or null:
    /// the agent advertised no <c>loadSession</c>, or refused this one.</summary>
    private async Task<string?> LoadSessionAsync(
        string workingDirectory, string sessionId, AcpMcpServer? server, string? sessionMetaJson, CancellationToken ct)
    {
        if (!CanLoadSession) return "The agent does not load saved sessions.";
        JsonNode? loaded;
        try
        {
            loaded = await _rpc.RequestAsync("session/load", new JsonObject
            {
                ["_meta"] = sessionMetaJson is null ? null : JsonNode.Parse(sessionMetaJson),
                ["sessionId"] = sessionId,
                ["cwd"] = workingDirectory,
                ["mcpServers"] = McpServersJson(server),
            }, ct).ConfigureAwait(false);
        }
        catch (AcpRpcException e)
        {
            return $"The agent could not load the session: {e.Message}";
        }

        _sessionId = sessionId;
        _modes = OfferedModes(loaded);
        return null;
    }

    /// <summary>Puts the open session in <paramref name="askingMode"/>, which it must offer. Answers
    /// why it could not, or null.</summary>
    private async Task<string?> EnterAskingModeAsync(string askingMode, CancellationToken ct)
    {
        if (!Offers(askingMode))
            return $"The agent offers no '{askingMode}' mode, so its writes can't be refused.";
        await _rpc.RequestAsync("session/set_mode", new JsonObject
        {
            ["sessionId"] = SessionId,
            ["modeId"] = askingMode,
        }, ct).ConfigureAwait(false);
        return null;
    }

    private static JsonArray McpServersJson(AcpMcpServer? server)
    {
        if (server is null) return new JsonArray();
        return new JsonArray(new JsonObject
        {
            ["type"] = "http",
            ["name"] = server.Name,
            ["url"] = server.Url.ToString(),
            ["headers"] = new JsonArray(),
        });
    }

    private static HashSet<string> OfferedModes(JsonNode? session)
    {
        var offered = new HashSet<string>(StringComparer.Ordinal);
        if (session?["modes"]?["availableModes"] is not JsonArray modes) return offered;
        foreach (var mode in modes)
            if (mode?["id"] is JsonValue id && id.TryGetValue<string>(out var value))
                offered.Add(value);
        return offered;
    }

    /// <summary>Whether the session offered <paramref name="mode"/> when it opened.</summary>
    public bool Offers(string mode) => _modes.Contains(mode);

    /// <summary>Puts the session in <paramref name="mode"/>, which it must offer.</summary>
    public async Task SetModeAsync(string mode, CancellationToken ct)
    {
        var sessionId = _sessionId ?? throw new InvalidOperationException("The session is not open.");
        if (!Offers(mode)) throw new InvalidOperationException($"The agent offers no '{mode}' mode.");
        await _rpc.RequestAsync("session/set_mode", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["modeId"] = mode,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Sends one user turn and completes when the agent ends it.</summary>
    public Task<AcpStopReason> PromptAsync(string text, CancellationToken ct) => PromptAsync([new AcpContent.Prose(text)], ct);

    /// <summary>Sends one user turn made of <paramref name="content"/> and completes when the agent
    /// ends it. Attach a <see cref="AcpContent.Resource"/> only where <see cref="EmbedsContext"/>, and an
    /// <see cref="AcpContent.Image"/> only where <see cref="AcceptsImages"/>.</summary>
    public async Task<AcpStopReason> PromptAsync(IReadOnlyList<AcpContent> content, CancellationToken ct)
    {
        var sessionId = _sessionId ?? throw new InvalidOperationException("The session is not open.");
        using var cancel = ct.Register(() => _ = CancelTurnAsync());
        var result = await _rpc.RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray([.. content.Select(ToJson)]),
        }, CancellationToken.None).ConfigureAwait(false);

        return (result?["stopReason"] as JsonValue)?.TryGetValue<string>(out var reason) == true
            ? reason switch
            {
                "max_tokens" => AcpStopReason.MaxTokens,
                "max_turn_requests" => AcpStopReason.MaxTurnRequests,
                "refusal" => AcpStopReason.Refusal,
                "cancelled" => AcpStopReason.Cancelled,
                _ => AcpStopReason.EndTurn,
            }
            : AcpStopReason.EndTurn;
    }

    private static JsonNode ToJson(AcpContent content) => content switch
    {
        AcpContent.Prose text => new JsonObject { ["type"] = "text", ["text"] = text.Value },
        AcpContent.Resource resource => new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = resource.Uri.ToString(),
                ["mimeType"] = "text/plain",
                ["text"] = resource.Text,
            },
        },
        AcpContent.Image image => new JsonObject
        {
            ["type"] = "image",
            ["mimeType"] = image.MimeType,
            ["data"] = Convert.ToBase64String(image.Data),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown content."),
    };

    /// <summary>Asks the agent to stop the turn in progress; the turn then ends <c>cancelled</c>.</summary>
    public async Task CancelTurnAsync()
    {
        if (_sessionId is not { } sessionId) return;
        try
        {
            await _rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId }).ConfigureAwait(false);
        }
        catch (AcpClosedException)
        {
        }
    }

    void IAcpClientMessages.OnNotification(string method, JsonNode? parameters)
    {
        if (method != "session/update") return;
        var update = parameters?["update"];
        if (update?["sessionUpdate"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "tool_call"
            && update["toolCallId"] is JsonValue idValue && idValue.TryGetValue<string>(out var toolCallId)
            && AcpPermissionPolicy.AnnouncedServer(update) is { } announced)
        {
            lock (_announcedServers) _announcedServers[toolCallId] = announced;
        }

        if (AcpSessionUpdate.Parse(update) is { } parsed) Updated?.Invoke(parsed);
    }

    async Task<JsonNode?> IAcpClientMessages.OnRequest(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (method != "session/request_permission")
            throw new AcpRpcException(AcpRpcException.MethodNotFound, $"DiffDino does not offer {method}.");

        AcpPermissionRequest? request;
        lock (_announcedServers) request = AcpPermissionPolicy.Parse(parameters, _announcedServers);
        if (request is null) throw new AcpRpcException(AcpRpcException.InvalidParams, "Unreadable permission request.");

        string? chosen;
        switch (AcpPermissionPolicy.Decide(request, _ownServer, _prompt.Rules, _workspace))
        {
            case AcpPermissionDecision.Select select:
                PermissionDecided?.Invoke(request, select.Verdict);
                chosen = select.OptionId;
                break;
            case AcpPermissionDecision.AskUser:
                chosen = await _prompt.AskAsync(request, ct).ConfigureAwait(false);
                PermissionDecided?.Invoke(request, IsAllow(request, chosen) ? AcpPermissionVerdict.Allowed : AcpPermissionVerdict.Rejected);
                break;
            default:
                throw new InvalidOperationException("Unhandled permission decision.");
        }

        return new JsonObject
        {
            ["outcome"] = chosen is null
                ? new JsonObject { ["outcome"] = "cancelled" }
                : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen },
        };
    }

    private static bool IsAllow(AcpPermissionRequest request, string? optionId)
    {
        foreach (var option in request.Options)
            if (option.OptionId == optionId)
                return option.Kind is AcpPermissionOptionKind.AllowOnce or AcpPermissionOptionKind.AllowAlways;
        return false;
    }

    private void NoteStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > StderrLines) _stderr.Dequeue();
        }
    }

    // The process goes first: a read of its output doesn't answer cancellation on every platform,
    // and it is the process ending that lets the read loop finish.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Ended whether or not the first process has exited: a wrapper such as npx exits and leaves
        // the adapter it started running.
        _tree?.End();

        await _rpc.DisposeAsync().ConfigureAwait(false);
        _tree?.Dispose();
        _process?.Dispose();
    }
}
