using System.Text.Json.Nodes;
using Zdev.Agents.Acp;
using Xunit;

namespace Zdev.Agents.Tests;

public sealed class AcpSessionUpdateTests
{
    [Fact]
    public void AReplayedPicture_IsAUserImage_WithItsBytes()
    {
        var update = AcpSessionUpdate.Parse(JsonNode.Parse(
            """{"sessionUpdate":"user_message_chunk","content":{"type":"image","mimeType":"image/png","data":"AQID"}}"""));

        var image = Assert.IsType<AcpSessionUpdate.UserImage>(update);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal([1, 2, 3], image.Data);
    }

    [Fact]
    public void AReplayedPicture_WhoseDataIsNoBase64_IsNoImage()
    {
        var update = AcpSessionUpdate.Parse(JsonNode.Parse(
            """{"sessionUpdate":"user_message_chunk","content":{"type":"image","mimeType":"image/png","data":"not base64!"}}"""));

        Assert.Equal(new AcpSessionUpdate.UserMessageChunk(""), update);
    }
}
