using System.Net;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Tests;

public sealed class LlmClientTests
{
    [Fact]
    public async Task SendChatAsyncThrowsReadableHttpErrorWithResponseBody()
    {
        var client = CreateClient(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED","message":"API key is not allowed"}}""");
        var request = CreateRequest();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendChatAsync(request));

        Assert.Contains("HTTP 403 Forbidden", ex.Message);
        Assert.Contains("PERMISSION_DENIED", ex.Message);
        Assert.Contains("API key is not allowed", ex.Message);
    }

    [Fact]
    public async Task StreamChatAsyncThrowsReadableHttpErrorWithResponseBody()
    {
        var client = CreateClient(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED","message":"API key is not allowed"}}""");
        var request = CreateRequest();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(request))
            {
            }
        });

        Assert.Contains("HTTP 403 Forbidden", ex.Message);
        Assert.Contains("PERMISSION_DENIED", ex.Message);
        Assert.Contains("API key is not allowed", ex.Message);
    }

    [Fact]
    public async Task StreamChatAsyncOmitsUsageStreamOptionsForGeminiOpenAiEndpoint()
    {
        var capturedBody = string.Empty;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            capturedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"choices":[{"delta":{"content":"hi"}}]}

                    data: [DONE]

                    """)
            };
        });
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));
        var request = CreateRequest(apiEndpoint: "https://generativelanguage.googleapis.com/v1beta/openai");

        await foreach (var _ in client.StreamChatAsync(request))
        {
        }

        Assert.Contains(@"""stream"":true", capturedBody);
        Assert.DoesNotContain("stream_options", capturedBody);
    }

    private static OpenAiCompatibleLlmClient CreateClient(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body)
        });

        return new OpenAiCompatibleLlmClient(new HttpClient(handler));
    }

    private static LlmRequest CreateRequest(string apiEndpoint = "https://example.test/v1")
    {
        return new LlmRequest
        {
            AgentName = "GM",
            Options = new LlmOptions(
                apiEndpoint,
                "test-key",
                "test-model",
                Temperature: 0.5,
                MaxTokens: 32),
            Messages = [LlmMessage.User("hello")]
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            this.respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return respond(request, cancellationToken);
        }
    }
}
