using System.Text;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.Xiaomi;

public sealed class XiaomiSseParserTests
{
    private readonly XiaomiSseParser _parser = new();

    [Fact]
    public async Task Parses_one_delta_and_completion()
    {
        var events = await ParseAsync(StreamFor(
            Data("Hi") + Finish("stop") + Done()));

        Assert.Equal("Hi", Assert.IsType<ModelStreamEvent.TextDelta>(events[0]).Text);
        Assert.Equal(
            ModelFinishReason.Stop,
            Assert.IsType<ModelStreamEvent.Completed>(events[1]).FinishReason);
    }

    [Fact]
    public async Task Keeps_Hel_and_lo_as_separate_deltas()
    {
        var events = await ParseAsync(StreamFor(
            Data("Hel") + Data("lo") + Finish("stop") + Done()));

        Assert.Equal(
            ["Hel", "lo"],
            events.OfType<ModelStreamEvent.TextDelta>().Select(value => value.Text));
    }

    [Fact]
    public async Task Handles_json_fragmented_across_reads()
    {
        var bytes = Encoding.UTF8.GetBytes(
            Data("Hello") + Finish("stop") + Done());
        var stream = new ChunkedReadStream(
            bytes,
            Enumerable.Repeat(1, bytes.Length).ToArray());

        var events = await ParseAsync(stream);

        Assert.Equal("Hello", Assert.IsType<ModelStreamEvent.TextDelta>(events[0]).Text);
    }

    [Fact]
    public async Task Handles_fragmented_multibyte_utf8()
    {
        var content = "سلام 🌍";
        var bytes = Encoding.UTF8.GetBytes(
            Data(content) + Finish("stop") + Done());
        var stream = new ChunkedReadStream(bytes, [1, 2, 1, 3, 1, 2, 1, 4, 1]);

        var events = await ParseAsync(stream);

        Assert.Equal(content, Assert.IsType<ModelStreamEvent.TextDelta>(events[0]).Text);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Supports_lf_and_crlf(string newline)
    {
        var payload = string.Concat(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}",
            newline,
            newline,
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            newline,
            newline,
            "data: [DONE]",
            newline,
            newline);

        var events = await ParseAsync(StreamFor(payload));

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Combines_multiple_data_lines_for_one_event()
    {
        var payload = "data: {\"choices\":[\n" +
                      "data: {\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
                      Finish("stop") + Done();

        var events = await ParseAsync(StreamFor(payload));

        Assert.Equal("Hi", Assert.IsType<ModelStreamEvent.TextDelta>(events[0]).Text);
    }

    [Fact]
    public async Task Ignores_comments_keep_alive_and_unknown_sse_fields()
    {
        var payload = ": keep alive\n\n" +
                      "event: message\n" +
                      Data("Hi") +
                      ": ping\n\n" +
                      Finish("stop") + Done();

        var events = await ParseAsync(StreamFor(payload));

        Assert.Equal("Hi", Assert.IsType<ModelStreamEvent.TextDelta>(events[0]).Text);
    }

    [Fact]
    public async Task Role_only_and_empty_content_chunks_do_not_create_text_events()
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
            Data("Hi") + Finish("stop") + Done();

        var events = await ParseAsync(StreamFor(payload));

        Assert.Single(events.OfType<ModelStreamEvent.TextDelta>());
    }

    [Theory]
    [InlineData("stop", ModelFinishReason.Stop)]
    [InlineData("length", ModelFinishReason.Length)]
    [InlineData("cancelled", ModelFinishReason.Cancelled)]
    [InlineData("error", ModelFinishReason.Error)]
    [InlineData("future_reason", ModelFinishReason.Unknown)]
    public async Task Maps_finish_reasons(
        string providerReason,
        ModelFinishReason expectedReason)
    {
        var events = await ParseAsync(StreamFor(
            Data("Hi") + Finish(providerReason) + Done()));

        Assert.Equal(
            expectedReason,
            Assert.IsType<ModelStreamEvent.Completed>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task Parses_usage_without_rendering_it_as_text()
    {
        var payload = Data("Hi") + Finish("stop") +
                      "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n" +
                      Done();

        var events = await ParseAsync(StreamFor(payload));
        var usage = Assert.IsType<ModelStreamEvent.Usage>(events[^2]).Value;

        Assert.Equal(2, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(5, usage.TotalTokens);
    }

    [Fact]
    public async Task Invalid_json_is_a_safe_provider_error()
    {
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor("data: {broken}\n\n")));

        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
        Assert.DoesNotContain("{broken}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_without_done_is_incomplete()
    {
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(Data("Hi") + Finish("stop"))));

        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task Done_without_finish_reason_is_invalid()
    {
        await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(Data("Hi") + Done())));
    }

    [Fact]
    public async Task Completion_without_text_is_invalid()
    {
        await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(Finish("stop") + Done())));
    }

    [Fact]
    public async Task Event_after_completion_is_invalid()
    {
        await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(
                Data("Hi") + Finish("stop") + Done() + Data("late"))));
    }

    [Fact]
    public async Task Repeated_completion_is_invalid()
    {
        await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(
                Data("Hi") + Finish("stop") + Done() + Done())));
    }

    [Fact]
    public async Task Tool_calls_are_rejected_as_unsupported()
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\"}]},\"finish_reason\":null}]}\n\n";

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ParseAsync(StreamFor(payload)));

        Assert.Equal(ModelProviderErrorCode.UnsupportedFeature, exception.Code);
    }

    [Fact]
    public async Task Reasoning_content_is_ignored()
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"secret reasoning\"},\"finish_reason\":null}]}\n\n" +
            Data("Hi") + Finish("stop") + Done();

        var events = await ParseAsync(StreamFor(payload));

        Assert.DoesNotContain(
            events.OfType<ModelStreamEvent.TextDelta>(),
            value => value.Text.Contains("reasoning", StringComparison.Ordinal));
        Assert.Equal("Hi", events.OfType<ModelStreamEvent.TextDelta>().Single().Text);
    }

    private async Task<IReadOnlyList<ModelStreamEvent>> ParseAsync(Stream stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in _parser.ParseAsync(stream))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static MemoryStream StreamFor(string value)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(value));
    }

    private static string Data(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{escaped}\"}},\"finish_reason\":null}}]}}\n\n";
    }

    private static string Finish(string reason)
    {
        return $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";
    }

    private static string Done() => "data: [DONE]\n\n";

    private sealed class ChunkedReadStream(byte[] content, IReadOnlyList<int> chunkSizes)
        : Stream
    {
        private int _position;
        private int _chunkIndex;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= content.Length)
            {
                return ValueTask.FromResult(0);
            }

            var configuredSize = _chunkIndex < chunkSizes.Count
                ? chunkSizes[_chunkIndex++]
                : buffer.Length;
            var count = Math.Min(
                Math.Min(configuredSize, buffer.Length),
                content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
