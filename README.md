# Fastener Spec Agent

A .NET 9 chat agent that answers questions about metric hex fasteners
(**DIN 933 / 931 / 934 / 125** and their ISO equivalents **ISO 4017 / 4014 / 4032 / 7089**).
Every dimension it cites comes from a Sanity Knowledge Base served over the
Sanity Context MCP endpoint — the agent never guesses from memory.

Built for the DEV.to Sanity Challenge (Path One).

## Architecture

```
Browser (vanilla JS chat) ── POST /api/chat ──► ASP.NET Core minimal API
                                                       │
                                                       ▼
                                              Anthropic Claude (Sonnet)
                                              via Microsoft.Extensions.AI
                                              (IChatClient + function invocation)
                                                       │
                                                       ▼
                                       Sanity Context MCP (Knowledge Base mode)
                                       ── Bearer token, HTTP streamable ──►
                                                       │
                                                       ▼
                                       Sanity Knowledge Base (fastener specs)
```

Question flow: user asks → Claude sees all MCP tools → Claude calls Knowledge Base
tools (up to 8 iterations of the tool loop) → Claude composes an answer that
cites the KB entries → the API returns the answer **plus the list of tool calls
made** so the UI can show "Looked up N sources" under each reply.

The companion Studio repository (schemas, sample data, KB setup) is:
**https://github.com/aftabkh4n/sanity-fastener-kb** — Sanity project ID
`tko9cbz7`.

## Requirements

- .NET 9 SDK
- A Sanity Context MCP endpoint in **Knowledge Base mode** attached to your
  fastener Knowledge Base (see the Studio repo for the schema and seed content)
- An organization API token with the **Context Viewer** role (a project token
  will not work — the endpoint rejects it with `403 contextGrantRequired`)
- An Anthropic API key

## Setup

Clone this repository, then from the project root:

```bash
dotnet restore
```

The MCP endpoint URL is baked in as a default:

```
https://api.sanity.io/v1/context/organizations/o31vwgtu4/mcp/fastener-spec-kb
```

Override it with `SANITY_CONTEXT_URL` if you point at a different endpoint.

Store the token and Anthropic key with `dotnet user-secrets` (do not commit them):

```bash
dotnet user-secrets init
dotnet user-secrets set "SANITY_API_READ_TOKEN" "sk_your_org_context_viewer_token"
dotnet user-secrets set "ANTHROPIC_API_KEY"     "sk-ant-your-key"
```

Or set them as environment variables of the same names. `SANITY_CONTEXT_TOKEN`
is also accepted as an alias for backward compatibility.

If you do not yet have a Sanity token, create a **Context Viewer** organization
token at:
<https://www.sanity.io/organizations/o31vwgtu4/api/tokens>

A project-scoped token will be rejected by the endpoint with
`403 contextGrantRequired`.

Optional — change the model in `appsettings.json`:

```json
"Anthropic": { "Model": "claude-sonnet-4-6" }
```

## Run

```bash
dotnet run
```

Open http://localhost:5080 and try the chips or ask your own question.

On startup the console logs the MCP tool names discovered from your endpoint,
for example:

```
info: Startup[0]
      MCP tools discovered (2): knowledge_base_search, knowledge_base_read
```

## Example questions

The chat page ships with these chips:

1. **What spanner do I need for an M12 hex set screw?**
2. **Is a DIN 933 M22 interchangeable with ISO 4017?**
3. **What's the head height of an M16 set screw?**
4. **Is DIN 933 still a current standard?**

Each answer shows a collapsible "Looked up N sources" panel listing the MCP
tool calls the agent made, so you can see exactly which Knowledge Base entries
grounded the reply.

## API

`POST /api/chat` — **streaming** Server-Sent Events response.

Request:

```json
{
  "messages": [
    { "role": "user", "content": "What spanner for an M12 set screw?" }
  ]
}
```

Response is `text/event-stream` with three event types:

```
event: tool
data: {"name":"groq_query","args":"{\"query\":\"*[_type == 'fastener' && ...]\"}"}

event: text
data: {"delta":"For an M12"}

event: text
data: {"delta":" hex set screw, use "}

event: done
data: {}
```

`GET /api/tools` — lists MCP tools discovered at startup.
`GET /api/health` — liveness probe.

## Project layout

```
FastenerSpecAgent.csproj   Web SDK, targets net9.0
Program.cs                 Minimal API, MCP client boot, chat endpoint
appsettings.json           Non-secret defaults (model)
wwwroot/                   Static chat page (no framework)
```

## Design notes

- **No fastener data in this repo.** Every dimension, standard status, and
  spanner size comes back through the MCP call chain. Search the code — there
  are no numbers.
- **Tool loop.** `Microsoft.Extensions.AI`'s `FunctionInvokingChatClient`
  handles the tool loop automatically with `MaximumIterationsPerRequest = 10`.
  Tool calls are surfaced to the UI as `event: tool` frames as they happen.
- **Streaming.** The endpoint uses `IChatClient.GetStreamingResponseAsync` and
  emits SSE frames as the model produces text. Tool invocations are interleaved
  with text deltas in the same stream.
- **Lifecycle.** The MCP client is held as a singleton and disposed on
  `ApplicationStopping` — one persistent HTTP-streamable connection for the app
  lifetime, closed cleanly on shutdown.
- **System prompt.** Enforces "check KB first", "flag DIN/ISO divergence",
  "note withdrawn standards", "cite sources", "admit gaps".

## Packages

| Package | Version | Why |
| ------- | ------- | --- |
| `Anthropic` | 12.51.0 | Official Claude .NET SDK |
| `Microsoft.Extensions.AI` | 10.10.0 | `IChatClient`, function-invoking pipeline |
| `ModelContextProtocol` | 2.2.0 | Official MCP C# SDK (HTTP streamable client) |

## Troubleshooting the MCP connection

If the app fails to start with an MCP error, isolate it from the app code
with a one-off request:

```bash
curl -X POST https://api.sanity.io/v1/context/organizations/o31vwgtu4/mcp/fastener-spec-kb \
  -H "Authorization: Bearer $SANITY_API_READ_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

- `401` — token missing or invalid
- `403 contextGrantRequired` — token is a project token; use an organization
  token with the Context Viewer role
- `400` / `406` — headers missing (both `Content-Type` and `Accept` are
  required by the streamable-HTTP transport)
- anything else — recheck the URL

## License

MIT.
