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
import { redactAddresses } from "@/lib/shared/redact";

type Fields = Record<string, unknown>;

/** The most of a non-pg cause's message one log line carries. */
const MAX_REASON_LENGTH = 300;

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
 * A pg server error (`DatabaseError`) always carries `severity`. Its text
 * (`message`, `detail`, `where`) can quote row values, so none of it is logged.
 */
function isPgServerError(value: Fields): boolean {
  return value.severity !== undefined;
}

/**
 * Why a query failed when the cause is NOT a pg server error — a dropped
 * connection (`Connection terminated unexpectedly`), a connect timeout, a
 * client-side serialisation fault. Those messages are the driver's own prose,
 * not row data, and without them the log line says nothing. Still, anything
 * address-shaped is redacted (`redactAddresses`) and the text is capped;
 * redaction runs first, so the cut cannot leave half an address behind.
 */
function causeReason(cause: unknown): string | undefined {
  const c = fields(cause);

  if (isPgServerError(c) || typeof c.message !== "string") return undefined;

  return redactAddresses(c.message, "").slice(0, MAX_REASON_LENGTH);
}

/**
 * A drizzle query error (own `query` string and `params`) or a bare pg server
 * error. Duck-typed, not `instanceof`, so a second copy of drizzle-orm or pg
 * in the bundle cannot silently skip redaction.
 */
export function isDbError(err: unknown): boolean {
  const e = fields(err);

  return isQueryErrorShape(e) || isPgServerError(e);
}

function isQueryErrorShape(value: Fields): boolean {
  return typeof value.query === "string" && "params" in value;
}

/**
 * A drizzle query error, or a bare pg error, becomes its kind, SQLSTATE, SQL
 * text and the constraint/table/column it names — never `params`, `message`
 * or `stack`. A drizzle error whose cause is not a pg server error also keeps
 * that cause's (redacted) message as `reason`. Anything else is returned
 * unchanged, so non-DB log lines keep their diagnosis.
 */
export function describeDbError(err: unknown): unknown {
  if (!isDbError(err)) return err;
  const e = fields(err);
  const isQueryError = isQueryErrorShape(e);
  const pg = isQueryError ? fields(e.cause) : e;
  const reason = isQueryError ? causeReason(e.cause) : undefined;

  return {
    // DrizzleQueryError never sets `this.name`, so `e.name` reads "Error";
    // a constructor name can be minified. The branch that matched says which.
    kind: isQueryError ? "DrizzleQueryError" : "DatabaseError",
    code: dbErrorCode(err),
    query: e.query,
    constraint: pg.constraint,
    table: pg.table,
    column: pg.column,
    ...(reason === undefined ? {} : { reason }),
  };
}
