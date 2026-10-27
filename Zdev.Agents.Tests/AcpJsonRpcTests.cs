using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Zdev.Agents.Acp;
using Xunit;

namespace Zdev.Agents.Tests;

/// <summary>Every request from the agent gets an answer: one the client fails to handle is answered
/// with an error, or the agent waits for it forever and its turn never ends.</summary>
public sealed class AcpJsonRpcTests : IAsyncDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
    private readonly StreamWriter _agentWrites;
    private readonly StreamReader _agentReads;
    private readonly ThrowingHandler _handler = new();
    private readonly AcpJsonRpc _rpc;

    public AcpJsonRpcTests()
    {
        var clientReads = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle), Encoding.UTF8);
        var clientWrites = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle), new UTF8Encoding(false));
        _agentWrites = new StreamWriter(_toClient, new UTF8Encoding(false)) { AutoFlush = true };
        _agentReads = new StreamReader(_fromClient, Encoding.UTF8);
        _rpc = new AcpJsonRpc(clientReads, clientWrites, _handler);
        _rpc.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _rpc.DisposeAsync();
        _toClient.Dispose();
        _fromClient.Dispose();
    }

    [Fact]
    public async Task A_request_the_client_fails_on_is_answered_with_an_internal_error()
    {
        _handler.Failure = new IOException("The file is locked.");

        var error = await AskAndReadError();

        Assert.Equal(AcpRpcException.InternalError, error["code"]!.GetValue<int>());
        Assert.Contains("The file is locked.", error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_cancelled_other_than_by_closing_the_connection_is_answered_with_an_error()
    {
        _handler.Failure = new OperationCanceledException("The client gave up waiting.");

        var error = await AskAndReadError();

        Assert.Equal(AcpRpcException.InternalError, error["code"]!.GetValue<int>());
    }

    /// <summary>Sends a request as the agent and returns the error its answer carries.</summary>
    private async Task<JsonNode> AskAndReadError()
    {
        await _agentWrites.WriteLineAsync("""{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"path":"a.txt"}}""");
        var line = await _agentReads.ReadLineAsync().WaitAsync(ReplyTimeout);
        Assert.NotNull(line);
        var reply = Assert.IsType<JsonObject>(JsonNode.Parse(line));
        Assert.Equal(7, reply["id"]!.GetValue<int>());
        var error = reply["error"];
        Assert.NotNull(error);
        return error;
    }

    private sealed class ThrowingHandler : IAcpClientMessages
    {
        public Exception Failure { get; set; } = new InvalidOperationException();

        public void OnNotification(string method, JsonNode? parameters)
        {
        }

        public Task<JsonNode?> OnRequest(string method, JsonNode? parameters, CancellationToken ct) =>
            Task.FromException<JsonNode?>(Failure);
    }
}
