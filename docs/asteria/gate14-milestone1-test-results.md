# Gate 14 — Asteria Milestone 1 test results

## Executed validation

Validation was run with .NET SDK `10.0.100`.

| Command | Result |
|---|---|
| `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --filter "FullyQualifiedName~AsteriaMilestone1Tests|FullyQualifiedName~DefinitionLoaderTests"` | Passed: 18, failed: 0, skipped: 0 |
| `dotnet build DurableStateMachine.slnx` | Passed: 0 warnings, 0 errors |
| `dotnet test DurableStateMachine.slnx` | Passed: 107, failed: 0, skipped: 0 |

The test runner is VSTest with xUnit. The focused command exercises the
Asteria behavioral test and the definition-loader suite; the full solution
test run includes all existing regression tests.

## Asteria-specific coverage

`AsteriaMilestone1Tests.Registered_definition_supports_local_progress_concern_history_and_safe_cancellation`
verifies:

- API registration of language 1.3 definition `asteria-release-governance@1.0`
  and rejection of an unsupported language version.
- Instance creation with all six concerns at their declared initial states.
- Incomplete submission refusal as unresolved; complete, well-formed local
  fixture facts allow the guarded `Draft -> UnderAssessment` transition.
- Deployment-request preparation is refused before the parent reaches
  `ReadyForDeployment`; the deployment concern remains unchanged.
- Functional and security verification requests progress independently.
- A Release Reviewer request does not implicitly request or approve Emergency
  approval.
- Repeated command identity is deduplicated without adding another concern
  transition.
- Parent and concern history inspection and deterministic, read-only replay.
- Cancellation guard refusal before a cancellation-intent transition and
  acceptance after that committed concern update.
- Parent cancellation leaves the separate deployment concern unchanged, and
  terminal-parent refusal prevents later concern input.

`DefinitionLoaderTests.Loads_both_workflow_definitions` also loads and checks
the Asteria definition, all ten candidate parent states, all six concerns,
absence of the special cancellation declaration, and absence of any parent
transition into deployment or completion.

No test creates an accepted approval, verification result, deployment
execution result, or health-check outcome. The test facts are local fixtures,
not claims of authoritative Asteria evidence.

## Result interpretation and limitations

The tests establish definition-language feasibility and the supported local
state/concern semantics only. They do not establish business-policy
correctness, employee/reviewer authorization, generation-aware evidence
validity, provider behavior, or safe deployment. In particular, the
`ReadyForDeployment` guard remains unreachable and there is no route to
`ReadyForDeployment`, `Completed`, or `Prohibited`.

The full requirement-by-requirement classification, explicit feasibility
answers, and work still required for a safely executable Gate 14 workflow are
in [gate14-milestone1-feasibility.md](./gate14-milestone1-feasibility.md).
Design semantics and deliberate omissions are in
[gate14-milestone1-design.md](./gate14-milestone1-design.md).
