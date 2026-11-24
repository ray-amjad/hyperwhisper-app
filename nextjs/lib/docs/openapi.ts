// The Local API spec (public/docs/api-reference/local-api/openapi.yaml, also
// served at that URL) as the endpoint pages need it. Mintlify generated one
// page per operation from the same file; lib/docs/nav.ts lists them, and
// tests/docs-content.test.ts holds the nav to the spec.
//
// Only what the spec uses: objects, arrays, enums, defaults and local $refs.
// Pure: the node tests import it with a spec parsed from disk.

import { endpointSlug } from "./nav";

export type Schema = {
  $ref?: string;
  type?: string;
  format?: string;
  description?: string;
  enum?: unknown[];
  default?: unknown;
  nullable?: boolean;
  minimum?: number;
  maximum?: number;
  required?: string[];
  properties?: Record<string, Schema>;
  items?: Schema;
};

export type Parameter = {
  in: "query" | "path" | "header" | "cookie";
  name: string;
  required?: boolean;
  description?: string;
  schema?: Schema;
};

type MediaContent = Record<string, { schema?: Schema }>;

export type Operation = {
  summary: string;
  description?: string;
  security?: unknown[];
  parameters?: Parameter[];
  requestBody?: { required?: boolean; content?: MediaContent };
  responses?: Record<string, { description?: string; content?: MediaContent }>;
};

export type OpenApiSpec = {
  info: { title: string; description?: string; version: string };
  servers?: { url: string; description?: string }[];
  security?: unknown[];
  paths: Record<string, Record<string, Operation>>;
  components?: {
    schemas?: Record<string, Schema>;
    securitySchemes?: Record<string, { description?: string }>;
  };
};

export type LocatedOperation = Operation & {
  method: string;
  path: string;
  slug: string;
};

const METHODS = ["get", "post", "put", "patch", "delete"];

/** Every operation, in file order, with the slug of its docs page. */
export function listOperations(spec: OpenApiSpec): LocatedOperation[] {
  const out: LocatedOperation[] = [];

  for (const [path, item] of Object.entries(spec.paths)) {
    for (const [method, operation] of Object.entries(item)) {
      if (!METHODS.includes(method)) continue;
      out.push({
        ...operation,
        method: method.toUpperCase(),
        path,
        slug: `api-reference/${endpointSlug(operation.summary)}`,
      });
    }
  }

  return out;
}

/** The schema a `#/components/schemas/X` ref names, with its name. */
export function resolveSchema(
  spec: OpenApiSpec,
  schema: Schema,
): { schema: Schema; name?: string } {
  let current = schema;
  let name: string | undefined;

  for (let depth = 0; current.$ref && depth < 10; depth += 1) {
    const ref = current.$ref;
    const match = ref.match(/^#\/components\/schemas\/(.+)$/);
    const target = match ? spec.components?.schemas?.[match[1]] : undefined;

    if (!match || !target) throw new Error(`openapi.yaml: unresolved ${ref}`);
    name = match[1];
    current = target;
  }

  return { schema: current, name };
}

/** A one-line type label: `string`, `integer`, `Mode[]`, `string (uuid)`. */
export function typeLabel(spec: OpenApiSpec, schema: Schema): string {
  const { schema: resolved, name } = resolveSchema(spec, schema);

  if (resolved.type === "array" && resolved.items) {
    return `${typeLabel(spec, resolved.items)}[]`;
  }
  const base = resolved.type ?? name ?? "object";
  const label = resolved.type === "object" && name ? name : base;

  return resolved.format ? `${label} (${resolved.format})` : label;
}

/** The operation's one JSON body schema, if it has one. */
export function jsonSchema(content?: MediaContent): Schema | undefined {
  return content?.["application/json"]?.schema;
}

/** Whether the operation needs the bearer token (`security: []` opts out). */
export function needsAuth(spec: OpenApiSpec, operation: Operation): boolean {
  const security = operation.security ?? spec.security ?? [];

  return security.length > 0;
}

/**
 * A Markdown description as one line of plain text: the meta description
 * Mintlify gave an endpoint page (the whole description, marks removed).
 */
export function plainText(markdown: string | undefined): string | undefined {
  if (!markdown) return undefined;
  const text = markdown
    .replace(/`([^`]*)`/g, "$1")
    .replace(/\*\*([^*]*)\*\*/g, "$1")
    .replace(/\s+/g, " ")
    .trim();

  return text || undefined;
}
