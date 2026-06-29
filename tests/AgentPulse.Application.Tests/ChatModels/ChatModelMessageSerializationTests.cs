using System.Text.Json;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.Tests.ChatModels;

public sealed class ChatModelMessageSerializationTests
{
    public static TheoryData<ChatModelMessage> Messages => new()
    {
        new ChatModelMessage(ChatModelRole.System, "system"),
        new ChatModelMessage(ChatModelRole.User, "user"),
        new ChatModelMessage(ChatModelRole.Assistant, "assistant"),
        ChatModelMessage.CreateAssistantToolCalls(null,
        [
            new ChatModelToolCall("call-1", "read", "{\"path\":\"README.md\"}", 1),
        ]),
        ChatModelMessage.CreateAssistantToolCalls("checking",
        [
            new ChatModelToolCall("call-2", "grep", "{\"pattern\":\"AgentPulse\"}", 2),
            new ChatModelToolCall("call-1", "read", "{\"path\":\"README.md\"}", 1),
        ]),
        ChatModelMessage.CreateToolResult("call-1", "read", "{\"success\":true,\"output\":\"ok\",\"error\":null,\"metadata\":{\"path\":\"README.md\"}}"),
        ChatModelMessage.CreateToolResult("call-2", "grep", "{\"success\":false,\"output\":\"\",\"error\":\"invalid\",\"metadata\":{}}"),
        new ChatModelMessage(ChatModelRole.Assistant, null, [], null, null),
        new ChatModelMessage(ChatModelRole.Assistant, string.Empty, [], null, null),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Message_round_trips_without_losing_tool_data(ChatModelMessage expected)
    {
        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<ChatModelMessage>(json);

        Assert.NotNull(actual);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.ToolCallId, actual.ToolCallId);
        Assert.Equal(expected.ToolName, actual.ToolName);
        Assert.Equal(expected.ToolCalls.Count, actual.ToolCalls.Count);
        for (var index = 0; index < expected.ToolCalls.Count; index++)
        {
            Assert.Equal(expected.ToolCalls[index].Id, actual.ToolCalls[index].Id);
            Assert.Equal(expected.ToolCalls[index].Name, actual.ToolCalls[index].Name);
            Assert.Equal(expected.ToolCalls[index].ArgumentsJson, actual.ToolCalls[index].ArgumentsJson);
            Assert.Equal(expected.ToolCalls[index].Order, actual.ToolCalls[index].Order);
        }
    }
}
