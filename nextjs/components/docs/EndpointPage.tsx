import type { ReactNode } from "react";

import Link from "next/link";

import {
  type LocatedOperation,
  type OpenApiSpec,
  type Parameter,
  type Schema,
  jsonSchema,
  needsAuth,
  resolveSchema,
  typeLabel,
} from "@/lib/docs/openapi";

// One Local API operation, rendered from openapi.yaml at build time: the
// page Mintlify generated for it, at the same URL. No "try it" playground:
// the server only listens on the reader's own 127.0.0.1.

/** `code`, **bold** and paragraphs: all the Markdown the spec's text uses. */
function Inline({ text }: { text: string }) {
  const parts = text.split(/(`[^`]+`|\*\*[^*]+\*\*)/g).filter(Boolean);

  return (
    <>
      {parts.map((part, i) => {
        if (part.startsWith("`") && part.endsWith("`")) {
          return <code key={i}>{part.slice(1, -1)}</code>;
        }
        if (part.startsWith("**") && part.endsWith("**")) {
          return <strong key={i}>{part.slice(2, -2)}</strong>;
        }

        return part;
      })}
    </>
  );
}

export function MarkdownText({ text }: { text?: string }) {
  if (!text) return null;

  return (
    <>
      {text
        .trim()
        .split(/\n\s*\n/)
        .map((paragraph, i) => (
          <p key={i}>
            <Inline text={paragraph.replace(/\s*\n\s*/g, " ")} />
          </p>
        ))}
    </>
  );
}

function constraints(schema: Schema): string[] {
  const out: string[] = [];

  if (schema.enum)
    out.push(`one of ${schema.enum.map((v) => JSON.stringify(v)).join(", ")}`);
  if (schema.default !== undefined)
    out.push(`default ${JSON.stringify(schema.default)}`);
  if (schema.minimum !== undefined) out.push(`min ${schema.minimum}`);
  if (schema.maximum !== undefined) out.push(`max ${schema.maximum}`);
  if (schema.nullable) out.push("nullable");

  return out;
}

function Field({
  spec,
  name,
  schema,
  required,
  depth,
}: {
  spec: OpenApiSpec;
  name: string;
  schema: Schema;
  required?: boolean;
  depth: number;
}) {
  const { schema: resolved } = resolveSchema(spec, schema);
  const inner =
    resolved.type === "array" && resolved.items
      ? resolveSchema(spec, resolved.items).schema
      : resolved;
  const notes = constraints(resolved);
  const description = schema.description ?? resolved.description;

  return (
    <li className="docs-field">
      <p className="docs-field-head">
        <code className="docs-field-name">{name}</code>
        <span className="docs-field-type">{typeLabel(spec, schema)}</span>
        {required ? (
          <span className="docs-field-required">required</span>
        ) : null}
      </p>
      {description ? (
        <div className="docs-field-desc">
          <MarkdownText text={description} />
        </div>
      ) : null}
      {notes.length ? (
        <p className="docs-field-notes">{notes.join(" · ")}</p>
      ) : null}
      {inner.properties && depth < 4 ? (
        <Fields depth={depth + 1} schema={inner} spec={spec} />
      ) : null}
    </li>
  );
}

function Fields({
  spec,
  schema,
  depth = 0,
}: {
  spec: OpenApiSpec;
  schema: Schema;
  depth?: number;
}) {
  const { schema: resolved } = resolveSchema(spec, schema);
  const properties = resolved.properties ?? {};

  if (!Object.keys(properties).length) return null;

  return (
    <ul className="docs-fields">
      {Object.entries(properties).map(([name, child]) => (
        <Field
          key={name}
          depth={depth}
          name={name}
          required={resolved.required?.includes(name)}
          schema={child}
          spec={spec}
        />
      ))}
    </ul>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = title.toLowerCase().replace(/[^a-z0-9]+/g, "-");

  return (
    <section aria-labelledby={id}>
      <h2 id={id}>{title}</h2>
      {children}
    </section>
  );
}

function ParameterList({
  spec,
  params,
}: {
  spec: OpenApiSpec;
  params: Parameter[];
}) {
  return (
    <ul className="docs-fields">
      {params.map((param) => (
        <Field
          key={`${param.in}:${param.name}`}
          depth={4}
          name={param.name}
          required={param.required}
          schema={{ ...(param.schema ?? {}), description: param.description }}
          spec={spec}
        />
      ))}
    </ul>
  );
}

export function EndpointPage({
  spec,
  operation,
}: {
  spec: OpenApiSpec;
  operation: LocatedOperation;
}) {
  const server = spec.servers?.[0]?.url ?? "http://127.0.0.1:{port}";
  const pathParams = operation.parameters?.filter((p) => p.in === "path") ?? [];
  const queryParams =
    operation.parameters?.filter((p) => p.in === "query") ?? [];
  const body = jsonSchema(operation.requestBody?.content);
  const auth = needsAuth(spec, operation);

  return (
    <div className="docs-endpoint">
      <p className="docs-endpoint-line">
        <span className="docs-method" data-method={operation.method}>
          {operation.method}
        </span>
        <code>
          <span className="docs-endpoint-server">{server}</span>
          {operation.path}
        </code>
      </p>
      <p>
        {auth ? (
          <>
            Send <code>Authorization: Bearer &lt;token&gt;</code>, with the
            token from the discovery file.
          </>
        ) : (
          <>No authentication: this route answers without a bearer token.</>
        )}{" "}
        See the{" "}
        <Link href="/docs/api-reference/local-api/overview">
          Local API overview
        </Link>{" "}
        for the port, the token and the response envelope.
      </p>
      <MarkdownText text={operation.description} />

      {pathParams.length ? (
        <Section title="Path parameters">
          <ParameterList params={pathParams} spec={spec} />
        </Section>
      ) : null}
      {queryParams.length ? (
        <Section title="Query parameters">
          <ParameterList params={queryParams} spec={spec} />
        </Section>
      ) : null}
      {body ? (
        <Section title="Request body">
          <p className="docs-field-notes">
            <code>application/json</code>, {typeLabel(spec, body)}
            {operation.requestBody?.required ? ", required" : ""}
          </p>
          <Fields schema={body} spec={spec} />
        </Section>
      ) : null}
      <Section title="Responses">
        {Object.entries(operation.responses ?? {}).map(([status, response]) => {
          const schema = jsonSchema(response.content);

          return (
            <div key={status} className="docs-response">
              <h3 id={`response-${status}`}>
                <span className="docs-status">{status}</span>{" "}
                {response.description}
              </h3>
              {schema ? (
                <>
                  <p className="docs-field-notes">
                    <code>application/json</code>, {typeLabel(spec, schema)}
                  </p>
                  <Fields schema={schema} spec={spec} />
                </>
              ) : null}
            </div>
          );
        })}
      </Section>
      <p>
        The whole spec is{" "}
        <a href="/docs/api-reference/local-api/openapi.yaml">openapi.yaml</a>.
      </p>
    </div>
  );
}
