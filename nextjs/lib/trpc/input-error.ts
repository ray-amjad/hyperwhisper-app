/**
 * #1146, #1155: when the server's zod input check rejects a field, tRPC v11
 * answers BAD_REQUEST and its message is the zod v4 issue array serialized as
 * JSON, regex included. A router's own BAD_REQUEST refusals carry a sentence,
 * so only a message that parses as a JSON array is an input-validation failure.
 */
export function isInputValidationError(err: {
  message: string;
  data?: { code?: string } | null;
}): boolean {
  if (err.data?.code !== "BAD_REQUEST") return false;
  try {
    return Array.isArray(JSON.parse(err.message));
  } catch {
    return false;
  }
}

/** The line to show for a mutation error: `inputSentence` for a zod input failure, else the server's own message. */
export function errorSentence(
  err: { message: string; data?: { code?: string } | null },
  inputSentence: string,
): string {
  return isInputValidationError(err) ? inputSentence : err.message;
}
