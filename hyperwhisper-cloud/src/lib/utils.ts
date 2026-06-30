// GENERAL UTILITY HELPERS
// Shared helpers for type guards, rounding, and retry operations

/**
 * Type guard for plain object values
 */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/**
 * Safely read the body text from a Response without throwing
 */
export async function safeReadText(response: Response): Promise<string | undefined> {
  try {
    return await response.text();
  } catch {
    return undefined;
  }
}

/**
 * Round a numeric value to the nearest tenth, guarding against NaN/Infinity.
 */
export function roundToTenth(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  const rounded = Math.round((value + Number.EPSILON) * 10) / 10;
  return Math.abs(rounded) < Number.EPSILON ? 0 : rounded;
}

/**
 * Round a numeric value up to the next tenth, ensuring non-negative output.
 */
export function roundUpToTenth(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  const rounded = Math.ceil((value - Number.EPSILON) * 10) / 10;
  return rounded <= 0 ? 0 : rounded;
}

/**
 * Retry configuration options
 */
export interface RetryOptions {
  /** Maximum number of retry attempts (default: 3) */
  maxRetries?: number;
  /** Initial delay in milliseconds (default: 1000ms) */
  initialDelayMs?: number;
  /** Backoff multiplier for exponential delay (default: 2) */
  backoffMultiplier?: number;
  /** Optional callback invoked before each retry attempt */
  onRetry?: (attempt: number, error: Error, delayMs: number) => void;
}

/**
 * Retry an async operation with exponential backoff
 */
export async function retryWithBackoff<T>(
  fn: () => Promise<T>,
  options: RetryOptions = {}
): Promise<T> {
  const {
    maxRetries = 3,
    initialDelayMs = 1000,
    backoffMultiplier = 2,
    onRetry,
  } = options;

  let lastError: Error;

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    try {
      return await fn();
    } catch (error) {
      lastError = error instanceof Error ? error : new Error(String(error));

      if (attempt === maxRetries) {
        throw lastError;
      }

      const delayMs = initialDelayMs * Math.pow(backoffMultiplier, attempt);

      if (onRetry) {
        onRetry(attempt + 1, lastError, delayMs);
      }

      await new Promise(resolve => setTimeout(resolve, delayMs));
    }
  }

  throw lastError!;
}
