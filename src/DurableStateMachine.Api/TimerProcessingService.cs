using System.Security.Cryptography;
using System.Text;
using DurableStateMachine.Persistence;

namespace DurableStateMachine.Api;

public sealed class TimerProcessingService(
    MachineStore store,
    TimeProvider timeProvider,
    HarnessCapabilityBindings bindings)
{
    public Task<IReadOnlyList<TimerProcessingResult>> ProcessDueAsync(
        int limit,
        CancellationToken cancellationToken) =>
        store.ProcessDueTimersAsync(
            timeProvider.GetUtcNow(),
            limit,
            (instanceId, operationId, capability) =>
            {
                var binding = bindings.Get(capability);
                var idempotencyKey = "dsm-" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{instanceId}\0{operationId}"))).ToLowerInvariant();
                return new EffectHarnessBinding(
                    binding.ProviderId,
                    binding.ApiDescriptionVersionId,
                    binding.OperationKey,
                    idempotencyKey,
                    OperationReplaySafety: null,
                    binding.CredentialBindingId,
                    binding.ProviderIdentity);
            },
            cancellationToken);
}
