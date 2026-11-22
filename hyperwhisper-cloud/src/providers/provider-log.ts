import type { ProviderRequestContext } from './types';

export function logProviderEvent(
  provider: string,
  event: string,
  details: Record<string, unknown>,
  context: ProviderRequestContext = {},
) {
  console.log(`provider.${event}`, {
    provider,
    requestId: context.requestId,
    attempt: context.attempt,
    ...details,
  });
}
