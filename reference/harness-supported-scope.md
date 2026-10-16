# Supported Scope

Generic Harness imports OpenAPI-described provider operations and invokes them
through a provider-agnostic HTTP execution pipeline. It does not include
provider-specific adapters. The [HTTP API specification](../harness.openapi.yaml)
describes the harness's own endpoints; this page describes the current
capability boundary.

## Provider descriptions

- Register an OpenAPI description as JSON for a provider. A description must
  contain at least one path.
- Absolute HTTP and HTTPS server URLs are supported. Server URL variables and
  non-HTTP schemes are not.
- Path, query, and header parameters are supported. Cookie parameters and
  other parameter locations are represented as unsupported operations.
- Parameter and request/response schemas support string, integer, number,
  boolean, object, and array types, including local schema references.
- External schema references and schema composition (`allOf`, `anyOf`,
  `oneOf`, and `not`) are unsupported. An operation using an unsupported
  construct remains discoverable with an unsupported status; it cannot be
  executed.
- Request bodies support `application/json` and
  `application/x-www-form-urlencoded`. Other request media types are
  unsupported.
- A successful `application/json` response schema can be imported and
  validated. Other response formats and richer OpenAPI response behavior are
  not modeled.

## Invocation and HTTP behavior

- Inputs are generic JSON values and must match parameters declared by the
  selected operation. Undeclared or missing required parameters, invalid
  schema values, and request bodies not declared by the operation are rejected
  before dispatch.
- Parameter values are serialized as scalar values. Array and object values
  are supported for JSON request bodies, not for path, query, or header
  parameters.
- The harness constructs requests from the imported server, path, method, and
  declared operation inputs. Redirect following is disabled.
- Only successful provider responses are read. Non-success response bodies are
  not exposed to callers or recorded.
- Response bodies are limited by `Execution:ResponseBody:MaxBytes`, which
  defaults to 1 MiB. Exceeding the limit fails the execution; the body is not
  silently truncated and presented as valid.
- A validated provider response is returned only in the response to the
  invocation that produced it. Idempotency lookups and rejected or failed
  executions do not include a provider response.

## Durable execution evidence and retries

- Executions, attempts, and sanitized evidence are persisted. Evidence records
  execution state, checkpoints, response status, and response-body metadata
  such as its hash and length; it does not persist the full provider response
  body or credential values.
- Executions can be inspected after restart. If the harness restarts while an
  execution is running, its remote outcome is recorded as unknown rather than
  assumed to have failed or succeeded.
- Replay evaluates historical evidence and does not call the provider again.
- The default retry budget is one attempt. Retries require a retryable failure
  and a replay-safety decision that permits retry; applicable `Retry-After`
  delays are honored. An ambiguous non-idempotent operation is not retried
  automatically.

## Credentials and operational boundary

- Credential bindings can be created, updated, rotated, enabled, and disabled.
  Supported binding scheme identifiers are API key, bearer token, and OAuth 2.0
  client credentials.
- API-key bindings apply the referenced value to the configured header. The
  current bearer-token and OAuth 2.0 execution paths do not yet inject a
  resolved token value and should not be used for provider calls.
- Secret values are not included in credential-binding responses or durable
  execution evidence. The default secret provider is in-memory, with optional
  configuration-based lookup; it is intended for development, not durable
  production secret management.
- The harness provides the APIs for importing descriptions, invoking
  operations, and inspecting execution history. The CLI client is a small
  convenience interface, not a complete client for every harness endpoint.
