using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

const string DefaultMcpUrl =
    "https://api.sanity.io/v1/context/organizations/o31vwgtu4/mcp/fastener-spec-kb";

string mcpUrl = builder.Configuration["SANITY_CONTEXT_URL"] ?? DefaultMcpUrl;

string mcpToken = builder.Configuration["SANITY_API_READ_TOKEN"]
    ?? builder.Configuration["SANITY_CONTEXT_TOKEN"]
    ?? throw new InvalidOperationException(
        "Sanity API read token missing. Set SANITY_API_READ_TOKEN via user-secrets or env var. " +
        "Create a Context Viewer token at " +
        "https://www.sanity.io/organizations/o31vwgtu4/api/tokens");

string anthropicKey = builder.Configuration["ANTHROPIC_API_KEY"]
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not configured. Set via user-secrets or env var.");

string model = builder.Configuration["Anthropic:Model"] ?? "claude-sonnet-4-6";

var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = loggerFactory.CreateLogger("Startup");

startupLogger.LogInformation("Connecting to Sanity Context MCP at {Url}", mcpUrl);

var mcpTransport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(mcpUrl),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {mcpToken}"
    }
});

var mcpClient = await McpClient.CreateAsync(mcpTransport);
var mcpTools = await mcpClient.ListToolsAsync();

startupLogger.LogInformation("MCP tools discovered ({Count}): {Names}",
    mcpTools.Count,
    string.Join(", ", mcpTools.Select(t => t.Name)));

var aiTools = mcpTools.Cast<AITool>().ToList();

var anthropic = new AnthropicClient { ApiKey = anthropicKey };

IChatClient chatClient = anthropic.AsIChatClient(model)
    .AsBuilder()
    .UseFunctionInvocation(configure: c =>
    {
        c.MaximumIterationsPerRequest = 10;
    })
    .Build();

builder.Services.AddSingleton(mcpClient);
builder.Services.AddSingleton<IReadOnlyList<AITool>>(aiTools);
builder.Services.AddSingleton(chatClient);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        mcpClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        startupLogger.LogInformation("MCP client closed.");
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Error closing MCP client");
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

const string SystemPrompt = """
You are a specialist assistant for metric hex fasteners. You answer questions about
DIN 933 (hex set screw / fully threaded hex bolt), DIN 931 (hex bolt, partially
threaded), DIN 934 (hex nut), DIN 125 (flat washer), and their ISO equivalents
ISO 4017, ISO 4014, ISO 4032, and ISO 7089.

Rules you must follow:
1. Always consult the Knowledge Base tools before answering any question about
   dimensions, tolerances, spanner (wrench) sizes, thread pitches, or standards.
   Never answer dimensional questions from memory.
2. If the DIN and ISO values differ for the requested size, state so explicitly,
   give both values, and ask which standard the user is working to if the choice
   matters for their task.
3. When a DIN standard has been withdrawn or superseded (for example DIN 933 was
   withdrawn in favour of ISO 4017), say so, but note that the DIN designation is
   still commonly used when ordering.
4. Cite the Knowledge Base entries you used (title and identifier if available).
5. If the Knowledge Base does not cover the question, say so plainly rather than
   guessing.
6. Keep answers concise and practical. Use millimetres. Show head-across-flats as
   "s = X mm", head height as "k = X mm", thread pitch as "P = X mm" where those
   are the standard symbols.
""";

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

app.MapPost("/api/chat", async (
    HttpContext http,
    ChatRequestDto req,
    IChatClient chat,
    IReadOnlyList<AITool> tools,
    CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";
    http.Response.StatusCode = 200;

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, SystemPrompt)
    };

    foreach (var m in req.Messages)
    {
        var role = m.Role?.ToLowerInvariant() switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            _ => ChatRole.User
        };
        messages.Add(new ChatMessage(role, m.Content ?? string.Empty));
    }

    var options = new ChatOptions
    {
        Tools = tools.ToList(),
        MaxOutputTokens = 2048
    };

    var seenCalls = new HashSet<string>();

    async Task WriteEvent(string ev, object payload)
    {
        var json = JsonSerializer.Serialize(payload, jsonOpts);
        await http.Response.WriteAsync($"event: {ev}\ndata: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await foreach (var update in chat.GetStreamingResponseAsync(messages, options, ct))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        await WriteEvent("text", new { delta = tc.Text });
                        break;

                    case FunctionCallContent fc:
                        var key = fc.CallId ?? $"{fc.Name}:{Guid.NewGuid()}";
                        if (!seenCalls.Add(key)) break;
                        string args = string.Empty;
                        if (fc.Arguments is { Count: > 0 })
                        {
                            try { args = JsonSerializer.Serialize(fc.Arguments); }
                            catch { args = fc.Arguments.ToString() ?? string.Empty; }
                        }
                        if (args.Length > 240) args = args[..240] + "…";
                        await WriteEvent("tool", new { name = fc.Name, args });
                        break;
                }
            }
        }

        await WriteEvent("done", new { });
    }
    catch (OperationCanceledException)
    {
        // Client disconnected; end quietly.
    }
    catch (Exception ex)
    {
        await WriteEvent("error", new { message = ex.Message });
    }
});

app.MapGet("/api/tools", (IReadOnlyList<AITool> tools) =>
    Results.Ok(tools.Select(t => new { name = t.Name, description = t.Description })));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

public record IncomingMessage(string Role, string Content);
public record ChatRequestDto(List<IncomingMessage> Messages);
