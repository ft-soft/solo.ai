# Solo → Solo AI model generation

`POST /api/v1/solo-agent/generations` receives the current message, permitted history
and complete fresh catalog from the authenticated Solo backend. It is mapped into
the common runtime host with `MapSoloAgentModelEndpoint(existingAuthenticationPolicy)`;
`AddSoloAgentModelEndpoint(configuration)` registers its processing services.

The authentication policy must authorize the trusted Solo/delegated sender.
`ISoloAgentRunAuthorizer.AuthorizeAndAcceptAsync` must additionally bind sender to
chat/run/message ownership and permitted history, and atomically accept only one
attempt. `PrepareGeneration` supplies server-owned history and `Complete` closes the
accepted attempt in the endpoint's finally block, including failures and cancellation.
The normal host registers `SoloAgentRunAuthorizer`; it accepts empty incoming history,
binds chats to the delegated user and retains history/replay state in process memory. Message replay
is checked by (delegated user, messageId) across all chats, including a retry of the first send with
a newly assigned chatId.
See [startup and the runtime limits](startup.md). The endpoint module alone has no default gate.
Missing authentication produces 401, denied ownership/replay produces 403, and a
missing runtime authorizer produces 503 without model dispatch. Browser identity,
correlation IDs or a submitted catalog alone are not authorization.

The JSON request contains required `contractVersion: 1` and `input`, whose fields
are requestId, chatId, runId, messageId (nonempty UUIDs), message, history and catalog.
History contains role/content records. Catalog preserves formId, nullable presetId,
name, nullable category/description and profiles with id/nullable displayName.
See [the complete synthetic request](../tests/solo-agent/generation-v1.json).
Names are case-sensitive; unknown/duplicate fields and absent constructor fields
are rejected. Unsupported versions are explicit failures. No tools are executed.

The reply has required `contractVersion`, `result` and `error`, with exactly one
of result/error non-null. A successful HTTP exchange returns 200 and a result with
all four matching correlation IDs, outcome, text, selection and clarificationOptions.
**200 means a processed exchange; outcome determines whether generation succeeded.**
For example, provider_error/timeout/cancelled technical results have no text or
selection. Only recommendation, clarification, no_match and empty_catalog contain
user text. It must be rendered literally. The [reply fixture](../tests/solo-agent/reply-v1.json)
defines a representative compatible result. AI validates all candidate combinations
against the input snapshot; Solo's client additionally checks correlation and joint IDs.

Boundary failures have null result and a technical error: validation_failed (400,
or 415 for content type), unsupported_contract (400), limit_exceeded (413),
configuration_failure (503), timeout (504) or provider_error (502). Authentication
401/403 uses the host's existing challenge/forbid behavior; no domain reply shape
is promised there. Cancellation after disconnect does not promise a JSON reply.

`SoloAgentModelApi` requires explicit Enabled, MaxRequestBytes, MaxResponseBytes and
finite RequestTimeout. It defaults to disabled. Full request and successful reply
byte bounds reject overflow without truncating catalog or output. The deadline
covers body reading, runtime authorization and model work; late replies are discarded.
Diagnostics log request ID, version, outcome and duration, excluding payload,
credentials and raw exceptions. Infrastructure logging still needs deployment checks.

The producer uses the `Solo.Ai.Client` NuGet package owned by this repository, configured through
`SoloAgent:Client` with Enabled, BaseUri, RequestTimeout, TotalDeadline and full byte
caps. It uses the named `solo-ai` HttpClient. DOC-5748 supplies its agreed outbound
authentication handler/credential binding to that client. No new auth scheme is
invented by the model client. HTTP attempts are not retried or redirected; old
endpoint/version, oversized or mismatched replies are technical failures.

The client package owns the DTOs used by the endpoint and Solo's consumer. Both
repositories keep synthetic fixtures. Builds consume published package versions
without adjacent checkouts or source-linked assemblies. Release a new immutable
package version and update the consumer when changing the contract; the wire
version is checked separately. The normal host supplies identity/history/run lifecycle;
durable storage, chat CRUD and UI integration remain DOC-5748 work.
