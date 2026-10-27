using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Zdev.Agents.Acp;
using Zdev.Common;
using Xunit;

namespace Zdev.Agents.Tests;

/// <summary>The ACP client against a scripted agent on in-process pipes: the handshake puts the
/// session in the asking mode, a turn streams its updates, and the agent's permission requests are
/// answered by the write guard without reaching the user unless the guard has no opinion.</summary>
public sealed class AcpAgentConnectionTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly AcpMcpServer Server = new("diffdino", new Uri("http://127.0.0.1:5577/token/mcp"));

    private readonly ScriptedAgent _agent = new();
    private readonly RecordingPrompt _prompt = new();
    private readonly AcpAgentConnection _connection;

    public AcpAgentConnectionTests()
    {
        _connection = AcpAgentConnection.Over(_agent.ClientReads, _agent.ClientWrites, _prompt, Server.Name);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _agent.Dispose();
    }

    [Fact]
    public async Task Open_InitializesOpensTheSessionWithTheServer_AndSetsTheAskingMode()
    {
        var failure = await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline);

        Assert.Null(failure);
        Assert.Equal(["initialize", "session/new", "session/set_mode"], _agent.Methods);
        var session = _agent.Params("session/new")!;
        Assert.Equal("C:/repo", session["cwd"]!.GetValue<string>());
        var server = session["mcpServers"]![0]!;
        Assert.Equal("http", server["type"]!.GetValue<string>());
        Assert.Equal(Server.Url.ToString(), server["url"]!.GetValue<string>());
        Assert.Equal("default", _agent.Params("session/set_mode")!["modeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connect_OverStreams_OpensTheSessionInTheHarnessAskingMode_AndTakesTurns()
    {
        var harness = AcpHarness.ClaudeCode with { ChatMode = "auto" };
        _agent.OnPrompt = async agent =>
        {
            await agent.Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hi."}}""");
            return "end_turn";
        };

        var start = await AcpAgentConnection.ConnectAsync(_agent.ClientReads, _agent.ClientWrites, harness, "C:/repo", Server, _prompt,
            CancellationToken.None).WaitAsync(Deadline);

        await using var connection = Assert.IsType<AcpStart.Started>(start).Connection;
        var updates = new List<AcpSessionUpdate>();
        connection.Updated += u => { lock (updates) updates.Add(u); };
        var stop = await connection.PromptAsync("hello", CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(AcpStopReason.EndTurn, stop);
        Assert.Equal(["initialize", "session/new", "session/set_mode", "session/prompt"], _agent.Methods);
        Assert.Equal("default", _agent.Params("session/set_mode")!["modeId"]!.GetValue<string>());
        Assert.Contains(updates, u => u is AcpSessionUpdate.MessageChunk { Text: "Hi." });
    }

    [Fact]
    public async Task Connect_WithoutAnMcpServer_OpensTheSessionWithNone()
    {
        var start = await AcpAgentConnection.ConnectAsync(_agent.ClientReads, _agent.ClientWrites, AcpHarness.ClaudeCode, "C:/repo", null, _prompt,
            CancellationToken.None).WaitAsync(Deadline);

        await using var connection = Assert.IsType<AcpStart.Started>(start).Connection;
        Assert.Empty(_agent.Params("session/new")!["mcpServers"]!.AsArray());
    }

    [Fact]
    public async Task Connect_ToAnAgentWithoutTheChatMode_FailsAndSaysWhich()
    {
        var harness = AcpHarness.ClaudeCode with { ChatMode = "yolo" };

        var start = await AcpAgentConnection.ConnectAsync(_agent.ClientReads, _agent.ClientWrites, harness, "C:/repo", Server, _prompt,
            CancellationToken.None).WaitAsync(Deadline);

        Assert.Contains("yolo", Assert.IsType<AcpStart.Failed>(start).Reason);
    }

    [Fact]
    public async Task AnAgentThatEmbedsContext_GetsTheSelectionAsAnAttachedResource()
    {
        _agent.Capabilities = """{"promptCapabilities":{"embeddedContext":true}}""";
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        Assert.True(_connection.EmbedsContext);

        await _connection.PromptAsync(
            [new AcpContent.Prose("Why?"), new AcpContent.Resource(new Uri("file:///C:/repo/a.cs#L3:5"), "line 3")],
            CancellationToken.None).WaitAsync(Deadline);

        var prompt = _agent.Params("session/prompt")!["prompt"]!.AsArray();
        Assert.Equal("text", prompt[0]!["type"]!.GetValue<string>());
        Assert.Equal("resource", prompt[1]!["type"]!.GetValue<string>());
        Assert.Equal("file:///C:/repo/a.cs#L3:5", prompt[1]!["resource"]!["uri"]!.GetValue<string>());
        Assert.Equal("line 3", prompt[1]!["resource"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAgentThatSaysNothingAboutContext_IsNotSentResources()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));

        Assert.False(_connection.EmbedsContext);
    }

    [Fact]
    public async Task AnAgentThatAcceptsImages_GetsAPictureAsBase64()
    {
        _agent.Capabilities = """{"promptCapabilities":{"image":true}}""";
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        Assert.True(_connection.AcceptsImages);

        await _connection.PromptAsync(
            [new AcpContent.Prose("Look"), new AcpContent.Image("image/png", [1, 2, 3])],
            CancellationToken.None).WaitAsync(Deadline);

        var image = _agent.Params("session/prompt")!["prompt"]!.AsArray()[1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        Assert.Equal("AQID", image["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAgentThatSaysNothingAboutImages_DoesNotAcceptThem()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));

        Assert.False(_connection.AcceptsImages);
    }

    [Fact]
    public async Task Open_WithoutTheAskingMode_Fails()
    {
        var failure = await _connection.OpenAsync("C:/repo", Server, "read-only", null, CancellationToken.None).WaitAsync(Deadline);

        Assert.NotNull(failure);
        Assert.Contains("read-only", failure);
        Assert.DoesNotContain("session/set_mode", _agent.Methods);
    }

    [Fact]
    public async Task SetMode_MovesTheSessionToAModeItOffered_AndRefusesOneItDidNot()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));

        await _connection.SetModeAsync("auto", CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal("auto", _agent.Params("session/set_mode")!["modeId"]!.GetValue<string>());
        Assert.True(_connection.Offers("default"));
        Assert.False(_connection.Offers("yolo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _connection.SetModeAsync("yolo", CancellationToken.None));
    }

    [Fact]
    public async Task Open_SendsTheSessionMeta()
    {
        const string meta = """{"claudeCode":{"options":{"model":"opus"}}}""";
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", meta, CancellationToken.None).WaitAsync(Deadline));

        Assert.Equal("opus", _agent.Params("session/new")!["_meta"]!["claudeCode"]!["options"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_StreamsUpdates_AndPutsAWriteToTheUser()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        var updates = new List<AcpSessionUpdate>();
        var decided = new List<(string, AcpPermissionVerdict)>();
        _connection.Updated += u => { lock (updates) updates.Add(u); };
        _connection.PermissionDecided += (r, v) => { lock (decided) decided.Add((r.Title, v)); };
        _prompt.Answer = "no";
        _agent.OnPrompt = async agent =>
        {
            await agent.Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Looking."}}""");
            var answer = await agent.RequestPermission("""
                {"toolCall":{"toolCallId":"t1","title":"Write a.txt","kind":"edit"},
                 "options":[{"optionId":"yes","name":"Yes","kind":"allow_once"},{"optionId":"no","name":"No","kind":"reject_once"}]}
                """);
            Assert.Equal("no", answer?["outcome"]?["optionId"]?.GetValue<string>());
            return "end_turn";
        };

        var stop = await _connection.PromptAsync("go", CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(AcpStopReason.EndTurn, stop);
        Assert.Contains(updates, u => u is AcpSessionUpdate.MessageChunk { Text: "Looking." });
        Assert.Equal([("Write a.txt", AcpPermissionVerdict.Rejected)], decided);
        Assert.Equal(["Write a.txt"], _prompt.Asked);
    }

    [Fact]
    public async Task Prompt_AnUnclassifiedTool_IsAskedOfTheUser()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        _prompt.Answer = "yes";
        JsonNode? answer = null;
        _agent.OnPrompt = async agent =>
        {
            answer = await agent.RequestPermission("""
                {"toolCall":{"toolCallId":"t1","title":"Switch mode","kind":"switch_mode"},
                 "options":[{"optionId":"yes","name":"Yes","kind":"allow_once"},{"optionId":"no","name":"No","kind":"reject_once"}]}
                """);
            return "end_turn";
        };

        await _connection.PromptAsync("go", CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(["Switch mode"], _prompt.Asked);
        Assert.Equal("yes", answer?["outcome"]?["optionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_CancelledTurn_ReportsCancelled()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        _agent.OnPrompt = _ => Task.FromResult("cancelled");

        Assert.Equal(AcpStopReason.Cancelled, await _connection.PromptAsync("go", CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task AgentExit_ClosesTheConnection_AndFailsTheTurn()
    {
        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));
        _agent.OnPrompt = agent =>
        {
            agent.Exit();
            return new TaskCompletionSource<string>().Task;
        };

        await Assert.ThrowsAsync<AcpClosedException>(() => _connection.PromptAsync("go", CancellationToken.None).WaitAsync(Deadline));
        await _connection.Closed.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Open_ReadsWhetherTheAgentLoadsSessions_AndNamesTheNewSession()
    {
        _agent.Capabilities = """{"loadSession":true}""";

        Assert.Null(await _connection.OpenAsync("C:/repo", Server, "default", null, CancellationToken.None).WaitAsync(Deadline));

        Assert.True(_connection.CanLoadSession);
        Assert.Equal("s1", _connection.SessionId);
    }

    [Fact]
    public async Task ConnectLoading_ReplaysTheSavedHistoryThroughUpdated_BeforeTheLiveTurns()
    {
        _agent.Capabilities = """{"loadSession":true}""";
        _agent.OnLoad = async agent =>
        {
            await agent.Update("""{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"Fix it."}}""");
            await agent.Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Fixed."}}""");
        };
        _agent.OnPrompt = async agent =>
        {
            await agent.Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Done."}}""");
            return "end_turn";
        };
        var updates = new List<AcpSessionUpdate>();

        var load = await AcpAgentConnection.ConnectLoadingAsync(_agent.ClientReads, _agent.ClientWrites, AcpHarness.ClaudeCode, "C:/repo",
            "saved-1", Server, _prompt, u => { lock (updates) updates.Add(u); }, CancellationToken.None).WaitAsync(Deadline);
        List<AcpSessionUpdate> history;
        lock (updates) history = updates.ToList();
        await using var connection = Assert.IsType<AcpLoad.Loaded>(load).Connection;
        await connection.PromptAsync("And now?", CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal("saved-1", connection.SessionId);
        Assert.Equal(["initialize", "session/load", "session/set_mode", "session/prompt"], _agent.Methods);
        var loaded = _agent.Params("session/load")!;
        Assert.Equal("saved-1", loaded["sessionId"]!.GetValue<string>());
        Assert.Equal("C:/repo", loaded["cwd"]!.GetValue<string>());
        Assert.Equal(Server.Url.ToString(), loaded["mcpServers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("default", _agent.Params("session/set_mode")!["modeId"]!.GetValue<string>());
        Assert.Equal<AcpSessionUpdate>([new AcpSessionUpdate.UserMessageChunk("Fix it."), new AcpSessionUpdate.MessageChunk("Fixed.")], history);
        Assert.Equal(new AcpSessionUpdate.MessageChunk("Done."), updates[^1]);
    }

    [Fact]
    public async Task ConnectLoading_AnAgentThatDoesNotLoadSessions_GetsANewSessionAndSaysWhy()
    {
        var load = await AcpAgentConnection.ConnectLoadingAsync(_agent.ClientReads, _agent.ClientWrites, AcpHarness.ClaudeCode, "C:/repo",
            "saved-1", null, _prompt, _ => { }, CancellationToken.None).WaitAsync(Deadline);

        var opened = Assert.IsType<AcpLoad.OpenedNew>(load);
        await using var connection = opened.Connection;
        Assert.Contains("does not load", opened.Reason);
        Assert.False(connection.CanLoadSession);
        Assert.Equal("s1", connection.SessionId);
        Assert.Equal(["initialize", "session/new", "session/set_mode"], _agent.Methods);
    }

    [Fact]
    public async Task ConnectLoading_ALoadTheAgentRefuses_GetsANewSessionAndSaysWhy()
    {
        _agent.Capabilities = """{"loadSession":true}""";
        _agent.LoadError = "Session saved-1 not found.";

        var load = await AcpAgentConnection.ConnectLoadingAsync(_agent.ClientReads, _agent.ClientWrites, AcpHarness.ClaudeCode, "C:/repo",
            "saved-1", null, _prompt, _ => { }, CancellationToken.None).WaitAsync(Deadline);

        var opened = Assert.IsType<AcpLoad.OpenedNew>(load);
        await using var connection = opened.Connection;
        Assert.Contains("Session saved-1 not found.", opened.Reason);
        Assert.Equal("s1", connection.SessionId);
        Assert.Equal(["initialize", "session/load", "session/new", "session/set_mode"], _agent.Methods);
    }

    [Fact]
    public async Task ConnectLoading_AnAgentThatExitsWhileLoading_Fails()
    {
        _agent.Capabilities = """{"loadSession":true}""";
        _agent.OnLoad = agent =>
        {
            agent.Exit();
            return Task.CompletedTask;
        };

        var load = await AcpAgentConnection.ConnectLoadingAsync(_agent.ClientReads, _agent.ClientWrites, AcpHarness.ClaudeCode, "C:/repo",
            "saved-1", null, _prompt, _ => { }, CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal("The agent exited while starting.", Assert.IsType<AcpLoad.Failed>(load).Reason);
    }

    [Fact]
    public async Task StartLoading_ACommandNotOnPath_Fails()
    {
        var nowhere = new MapServerEnvironment(new Dictionary<string, string>());

        var load = await AcpAgentConnection.StartLoadingAsync(AcpHarness.ClaudeCode, "C:/repo", "saved-1", null, _prompt, _ => { }, nowhere,
            CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal("'npx' was not found on PATH.", Assert.IsType<AcpLoad.Failed>(load).Reason);
    }

    private sealed class RecordingPrompt : IAcpPermissionPrompt
    {
        public List<string> Asked { get; } = new();
        public string? Answer { get; set; }

        public Task<string?> AskAsync(AcpPermissionRequest request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request.Title);
            return Task.FromResult(Answer);
        }
    }

    /// <summary>An agent on the far end of two anonymous pipes, answering the handshake itself and
    /// a prompt through <see cref="OnPrompt"/>.</summary>
    private sealed class ScriptedAgent : IDisposable
    {
        private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private readonly List<(string Method, JsonNode? Params)> _received = new();
        private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _replies = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private long _nextId = 100;

        public ScriptedAgent()
        {
            ClientReads = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle), Encoding.UTF8);
            ClientWrites = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle), new UTF8Encoding(false));
            _writer = new StreamWriter(_toClient, new UTF8Encoding(false));
            _reader = new StreamReader(_fromClient, Encoding.UTF8);
            _ = Task.Run(Loop);
        }

        public TextReader ClientReads { get; }
        public TextWriter ClientWrites { get; }

        public Func<ScriptedAgent, Task<string>>? OnPrompt { get; set; }

        public List<string> Methods
        {
            get
            {
                lock (_received) return _received.Select(r => r.Method).ToList();
            }
        }

        public JsonNode? Params(string method)
        {
            lock (_received) return _received.Last(r => r.Method == method).Params;
        }

        public Task Update(string update) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = "s1", ["update"] = JsonNode.Parse(update) } });

        public async Task<JsonNode?> RequestPermission(string parameters)
        {
            var id = Interlocked.Increment(ref _nextId);
            var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_replies) _replies[id] = reply;
            var body = JsonNode.Parse(parameters)!.AsObject();
            body["sessionId"] = "s1";
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "session/request_permission", ["params"] = body });
            return await reply.Task;
        }

        /// <summary>The <c>agentCapabilities</c> initialize answers with.</summary>
        public string Capabilities { get; set; } = "{}";

        /// <summary>Replays a saved session's history while <c>session/load</c> waits for its answer.</summary>
        public Func<ScriptedAgent, Task>? OnLoad { get; set; }

        /// <summary>When set, <c>session/load</c> is answered with this error.</summary>
        public string? LoadError { get; set; }

        public void Exit() => _toClient.Dispose();

        private async Task Loop()
        {
            try
            {
                while (await _reader.ReadLineAsync() is { } line)
                {
                    var message = JsonNode.Parse(line)!.AsObject();
                    var method = message["method"]?.GetValue<string>();
                    if (method is null)
                    {
                        TaskCompletionSource<JsonNode?>? reply;
                        lock (_replies) _replies.Remove(message["id"]!.GetValue<long>(), out reply);
                        reply?.TrySetResult(message["result"]?.DeepClone());
                        continue;
                    }

                    lock (_received) _received.Add((method, message["params"]?.DeepClone()));
                    if (message["id"] is not { } id) continue;
                    _ = Answer(id.DeepClone(), method);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private const string Modes = """{"currentModeId":"auto","availableModes":[{"id":"default","name":"Manual"},{"id":"auto","name":"Auto"}]}""";

        private async Task Answer(JsonNode id, string method)
        {
            if (method == "session/load" && LoadError is { } error)
            {
                await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32002, ["message"] = error } });
                return;
            }

            JsonNode? result = method switch
            {
                "initialize" => JsonNode.Parse($$"""{"protocolVersion":1,"agentCapabilities":{{Capabilities}},"authMethods":[]}"""),
                "session/new" => JsonNode.Parse($$"""{"sessionId":"s1","modes":{{Modes}}}"""),
                "session/load" => await Load(),
                "session/set_mode" => new JsonObject(),
                "session/prompt" => new JsonObject { ["stopReason"] = OnPrompt is { } run ? await run(this) : "end_turn" },
                _ => null,
            };
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }

        private async Task<JsonNode?> Load()
        {
            if (OnLoad is { } replay) await replay(this);
            return JsonNode.Parse($$"""{"modes":{{Modes}}}""");
        }

        private async Task Send(JsonObject message)
        {
            await _write.WaitAsync();
            try
            {
                await _writer.WriteAsync(message.ToJsonString() + "\n");
                await _writer.FlushAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose()
        {
            _toClient.Dispose();
            _fromClient.Dispose();
        }
    }
}
