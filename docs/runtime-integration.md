# Runtime integration

Solo authenticates the sender and reads its permission-scoped catalog. Solo AI
currently accepts unauthenticated requests, partitions chats by the supplied user ID,
reads that chat's server-owned history and accepts one generation attempt before the model. Request/chat/run/message IDs are correlation, not authority.
Duplicate submissions and reconnects must not automatically start another attempt.

The Solo frontend enters through `POST /api/v2/soloAgent/SendMessage` with only
chatId, messageId and message in its existing Solo session. Its
`ISoloAgentMessageRuntime` receives the server-resolved user and calls the generation
service in that user context; ownership/history/run lifecycle is delegated to Solo AI.
Solo registers `SoloAgentMessageRuntime`, which binds the server user and generates
request/run IDs. Ownership, accepted attempts and permitted history are enforced by
the normal Solo AI host. See [the Solo client endpoint](../../solo/doc/dev/solo-agent-endpoint.md).

Solo's scoped `SoloAgentGenerationService` invokes its local `SoloAgentCatalogReader`
inside the authenticated Solo request context, then sends the complete snapshot
through the `Solo.Ai.Client` NuGet package to `POST /api/v1/solo-agent/generations`. Never replace
that snapshot with a browser catalog or arbitrary user/profile ID. Solo does not
reference this repository's source projects; it consumes the published client package.
The receiving endpoint calls `ISoloAgentRunRuntime.TryAcceptAsync` before the model
pipeline to check chat/user consistency, history, replay and acceptance of one attempt;
without a runtime implementation the endpoint fails closed. Incoming authentication
is temporarily removed. `X-Solo-User-Id` is unverified context, not proof of identity.
Solo derives that header from its server user; the browser does not supply it.
See [startup](startup.md).

`SoloAgentModelPipeline` accepts the prepared snapshot only after those checks.
The snapshot contains
each distinct `(formId, presetId)` and its allowed profiles, original name, category
and description. Missing descriptions and profile names remain null. No snapshot
is cached or written into chat history by these modules.

The model context is ordered: trusted system instruction, permitted user/assistant
history, one user-level JSON catalog snapshot, current user text. Metadata and
history are untrusted data. The runtime must not include earlier internal catalog
snapshots or system messages in history.

The generic model request uses wire version 1 and the capabilities `ordered_messages`,
`non_streaming`, `json_schema`. Its contract is owned by Visograph's
`docs/reference/model-exchange.md`. The direct HTTP adapter in `Solo.Ai/Visograph` sends the service key
only in the Bearer header, rejects a missing old endpoint, checks reply correlation,
and makes one HTTP attempt without redirects, fallback or repair.

The document schema is [recommendation-v1.schema.json](../src/Solo.Ai/recommendation-v1.schema.json).
All four properties `kind`, `candidate`, `reason`, `candidates` are required.
`recommendation` has one candidate, null reason and an empty candidates array.
`clarification` has null candidate and either distinct allowed variants/profiles
or an `intent` question without candidates. `no_match` has no candidates or reason.
Each candidate specifies all three IDs; preset and profile may be null. A profile
is never chosen arbitrarily. The validator checks the joint combination in this
request's snapshot, including clarification options. Invalid JSON, extra text,
unknown IDs and incompatible combinations produce technical failures.

`SoloAgentResponse` retains all four correlation IDs. Only `recommendation`,
`clarification`, `no_match` and `empty_catalog` contain user text. The UI must render
it as literal plain text, including catalog names; it must not interpret Markdown,
HTML, URLs or actions. Technical outcomes have no model text or selection.
The endpoint completes only its accepted run and returns only the complete, current
result. Errors/cancellation close that attempt; a late result cannot complete a later run.

`AddSoloAgentModelEndpoint` registers configuration through Solo.Toolbox
`AddConfiguration`. Each options class implements `ICustomConfiguration` and
declares its section with `ConfigurationAttribute`: `SoloAgentModelApi`,
`SoloAgentModel` and `VisographModelClient`. Direct injection and `IOptions<T>`
resolve the same configuration instance.

Configure `SoloAgentModelOptions` with `Enabled`, `MaxRequestBytes`, `TotalDeadline`
and `ModelTimeout`; configure `ModelClientOptions` with `Enabled`, `BaseUri`, `ApiKey`,
`RequestTimeout`, `MaxRequestBytes` and `MaxResponseBytes`. BaseUri is the deployment
root ending in `/`; it must have no user information, query or fragment. Required
values must be positive, deadlines finite and at most one day, and model timeout
must not exceed the total deadline. That one-day cap is a configuration guard,
not a model SLA. Store credentials in the host's secret configuration.

Byte limits apply to full serialized requests and full responses. Overflow fails
without truncating the catalog, history, schema or model output. Solo's generation
service starts its total deadline before catalog reading; both HTTP deadlines cover
response body reading. Cancellation and expired deadlines prohibit accepting late
responses or starting a model call after an expired catalog read.

Solo's existing catalog providers are synchronous and do not accept cancellation.
An ongoing provider read cannot be forcibly interrupted by this adapter; its result
is discarded once the read returns after a cancellation/deadline. A hard bound for
that stage requires a cancellable source or the runtime's isolated request boundary
and must be checked in deployment. Cancellation does not prove provider compute
has stopped. No component here implements persistence or exactly-once delivery.

The normal executable `Solo.Ai.Host` registers `AddSoloAgentModelEndpoint(configuration)` and
`MapSoloAgentModelEndpoint()` without an authentication policy. Its
`SoloAgentRunRuntime` supplies server-owned history and completes each accepted
attempt in the endpoint's finally block. Configure `SoloAgentModelApi` with Enabled, full request/response byte caps
and RequestTimeout; configure `SoloAgentModel` and `VisographModelClient` for the
pipeline and downstream HTTP call. Register the real `ISoloAgentRunRuntime`.
The endpoint module provides no standalone chat host or default allow-all gate.

The path remains disabled until the runtime wiring and the actual provider's role,
schema, context/output limits, overflow behavior and effective infrastructure
timeouts have been verified. Component tests are not end-user acceptance.
