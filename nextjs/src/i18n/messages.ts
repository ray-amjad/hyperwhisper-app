import type { locales } from "./locales";

type Locale = (typeof locales)[number];

type MessageModule = {
  default: Record<string, unknown>;
};

function isMessageModule(value: unknown): value is MessageModule {
  return (
    typeof value === "object" &&
    value !== null &&
    "default" in value &&
    typeof value.default === "object" &&
    value.default !== null
  );
}

export async function loadMessages(
  locale: Locale,
): Promise<Record<string, unknown>> {
  const messageModule: unknown = await import(`../../messages/${locale}.json`);

  if (!isMessageModule(messageModule)) {
    throw new TypeError(`Invalid message module for locale: ${locale}`);
  }

  return messageModule.default;
}
