using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.Tests.ChatModels;

public sealed class ChatModelContractsTests
{
    [Fact]
    public void Chat_model_message_is_immutable_and_rejects_empty_content()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChatModelMessage(ChatModelRole.User, "   "));

        var message = new ChatModelMessage(ChatModelRole.Assistant, "answer");

        Assert.Equal(ChatModelRole.Assistant, message.Role);
        Assert.Equal("answer", message.Content);
        Assert.Null(typeof(ChatModelMessage).GetProperty(nameof(ChatModelMessage.Role))!.SetMethod);
        Assert.Null(typeof(ChatModelMessage).GetProperty(nameof(ChatModelMessage.Content))!.SetMethod);
    }

    [Fact]
    public void Chat_model_request_copies_messages_and_exposes_read_only_order()
    {
        var source = new List<ChatModelMessage>
        {
            new(ChatModelRole.System, "system"),
            new(ChatModelRole.User, "prompt"),
        };
        var request = new ChatModelRequest(source);

        source.Add(new ChatModelMessage(ChatModelRole.Assistant, "late mutation"));

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ChatModelRole.System, request.Messages[0].Role);
        Assert.Equal(ChatModelRole.User, request.Messages[1].Role);

        var mutableView = Assert.IsAssignableFrom<IList<ChatModelMessage>>(request.Messages);
        Assert.Throws<NotSupportedException>(() =>
            mutableView.Add(new ChatModelMessage(ChatModelRole.Assistant, "mutation")));
    }

    [Fact]
    public void Chat_model_request_requires_a_system_message()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ChatModelRequest([new ChatModelMessage(ChatModelRole.User, "prompt")]));

        Assert.Contains("system", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(1, 2, -1)]
    [InlineData(1, 2, 4)]
    public void Model_usage_rejects_invalid_numeric_invariants(
        long inputTokens,
        long outputTokens,
        long totalTokens)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ModelUsage(inputTokens, outputTokens, totalTokens));
    }

    [Fact]
    public void Model_usage_preserves_valid_counts()
    {
        var usage = new ModelUsage(12, 8, 20);

        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(8, usage.OutputTokens);
        Assert.Equal(20, usage.TotalTokens);
    }

    [Fact]
    public void Stream_events_are_type_safe_and_provider_independent()
    {
        ModelStreamEvent[] events =
        [
            new ModelStreamEvent.TextDelta(" "),
            new ModelStreamEvent.Usage(new ModelUsage(3, 2, 5)),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
            new ModelStreamEvent.Failed("provider-independent failure"),
        ];

        Assert.IsType<ModelStreamEvent.TextDelta>(events[0]);
        Assert.IsType<ModelStreamEvent.Usage>(events[1]);
        Assert.IsType<ModelStreamEvent.Completed>(events[2]);
        Assert.IsType<ModelStreamEvent.Failed>(events[3]);
    }

    [Fact]
    public void Application_model_contracts_do_not_reference_provider_or_ef_packages()
    {
        var referencedAssemblies = typeof(ChatModelRequest)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            referencedAssemblies,
            static name => name.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            referencedAssemblies,
            static name => name.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
    }
}
