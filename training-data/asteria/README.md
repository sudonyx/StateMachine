# Asteria Gate 14 — Independent Capstone Inputs

All contents are fictional and synthetic. The inputs specify business expectations and external-provider HTTP contracts, **not** a state-machine implementation or expected test answers.

Files:
- `asteria-release-policy-v1.md` — business-language versioned release-governance policy supplementing AST-BR-001.
- `asteria-synthetic-cases.ndjson` — 14 release/case input records. No policy outcomes, final states or solution columns.
- `asteria-deployment-platform.openapi.yaml` — OpenAPI 3.1 deployment-request, status and identity-lookup endpoints for one mutating provider capability.
- `asteria-health-verification.openapi.yaml` — OpenAPI 3.1 read-only health-verification capability.

The AST-BR-001 business requirements from the guided planning session remain authoritative. All endpoint server URLs are examples for local synthetic implementations, not evidence that a live service is running. This package does not supply implementations, registration instructions, machine definitions, executable policy rules or hidden expected outcomes.

The deployment provider contract specifies idempotency based on a caller-supplied logical key and request identity, but does not guarantee that a timeout indicates failure. Its read-only lookup endpoint allows reconciliation by an exact *known* key when records are accessible. If no authoritative lookup result is available, uncertainty persists. `202` means accepted for processing, not deployed.

Times and release details in synthetic cases are invented. They are not assertions that the events already took place. The material digest values are fixture identifiers, not cryptographic checksums of archived artifacts. A material revision case contains separate generation objects to exercise assessment binding.
