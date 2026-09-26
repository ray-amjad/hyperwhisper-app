/**
 * Log-safe views of a database error (#1039).
 *
 * drizzle-orm wraps every failed query in a `DrizzleQueryError` whose message
 * (and so its stack, and `util.inspect` of it) is `Failed query: <sql>\nparams:
 * <bound values>` — the customer's email and, on the licence path, the full
 * licence key. The pg error sits on `.cause`, and its own `message`, `detail`,
 * `where` and `internalQuery` can carry row values too (`Key (email)=(…)`,
 * `invalid input syntax for type uuid: "…"`). So only identifiers are kept.
 */
type Fields = Record<string, unknown>;

function fields(value: unknown): Fields {
  return typeof value === "object" && value !== null ? (value as Fields) : {};
}

/** The pg SQLSTATE: the error's own `.code`, else drizzle's `.cause.code`. */
export function dbErrorCode(err: unknown): string | undefined {
  const code = fields(err).code ?? fields(fields(err).cause).code;

  return typeof code === "string" ? code : undefined;
}

/**
 * The unique index or constraint a pg error names: the error's own
 * `.constraint`, else drizzle's `.cause.constraint`. An identifier, never a value.
 */
export function dbErrorConstraint(err: unknown): string | undefined {
  const constraint =
    fields(err).constraint ?? fields(fields(err).cause).constraint;

  return typeof constraint === "string" ? constraint : undefined;
}

/**
 * A drizzle query error, or a bare pg error, becomes its name, SQLSTATE, SQL
 * text and the constraint/table/column it names — never `params`, `message`
 * or `stack`. Anything else is returned unchanged, so non-DB log lines keep
 * their diagnosis.
 */
export function describeDbError(err: unknown): unknown {
  const e = fields(err);
  const isQueryError = typeof e.query === "string" && "params" in e;

  if (!isQueryError && e.severity === undefined) return err;
  const pg = isQueryError ? fields(e.cause) : e;

  return {
    name: e.name,
    code: dbErrorCode(err),
    query: e.query,
    constraint: pg.constraint,
    table: pg.table,
    column: pg.column,
  };
}
