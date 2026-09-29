using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

string mcpUrl = builder.Configuration["SANITY_CONTEXT_URL"]
    ?? throw new InvalidOperationException("SANITY_CONTEXT_URL not configured. Set via user-secrets or env var.");
string mcpToken = builder.Configuration["SANITY_CONTEXT_TOKEN"]
    ?? throw new InvalidOperationException("SANITY_CONTEXT_TOKEN not configured. Set via user-secrets or env var.");
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
        c.MaximumIterationsPerRequest = 8;
    })
    .Build();

builder.Services.AddSingleton(mcpClient);
builder.Services.AddSingleton<IReadOnlyList<AITool>>(aiTools);
builder.Services.AddSingleton(chatClient);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

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

app.MapPost("/api/chat", async (
    ChatRequestDto req,
    IChatClient chat,
    IReadOnlyList<AITool> tools,
    CancellationToken ct) =>
{
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

    var response = await chat.GetResponseAsync(messages, options, ct);

    var toolCalls = new List<ToolCallDto>();
    foreach (var msg in response.Messages)
    {
        foreach (var content in msg.Contents)
        {
            if (content is FunctionCallContent fc)
            {
                string args = string.Empty;
                if (fc.Arguments is { Count: > 0 })
                {
                    try
                    {
                        args = JsonSerializer.Serialize(fc.Arguments);
                    }
                    catch
                    {
                        args = fc.Arguments.ToString() ?? string.Empty;
                    }
                }
                if (args.Length > 240) args = args[..240] + "…";
                toolCalls.Add(new ToolCallDto(fc.Name, args));
            }
        }
    }

    return Results.Ok(new ChatResponseDto(response.Text ?? string.Empty, toolCalls));
});

app.MapGet("/api/tools", (IReadOnlyList<AITool> tools) =>
    Results.Ok(tools.Select(t => new { name = t.Name, description = t.Description })));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

public record IncomingMessage(string Role, string Content);
public record ChatRequestDto(List<IncomingMessage> Messages);
public record ToolCallDto(string Name, string Args);
public record ChatResponseDto(string Answer, List<ToolCallDto> ToolCalls);
