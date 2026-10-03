import { NextRequest, NextResponse } from "next/server";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { isRecord } from "@/src/lib/type-guards";
import { unparseableRequestFields } from "@/src/lib/unparseable-request-fields";

type InternalEmailRequestResult =
  | { email: string }
  | { response: NextResponse };

export async function parseInternalEmailRequest(
  request: NextRequest,
): Promise<InternalEmailRequestResult> {
  const secret = request.headers.get("x-internal-secret");
  if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {
    return {
      response: NextResponse.json({ error: "Unauthorized" }, { status: 401 }),
    };
  }

  try {
    const body: unknown = await request.json();
    let email = isRecord(body) && typeof body.email === "string" ? body.email : "";
    if (!email) {
      return {
        response: NextResponse.json(
          { error: "email is required" },
          { status: 400 },
        ),
      };
    }
    email = email.toLowerCase().trim();
    return { email };
  } catch (err) {
    // A 400 here means our own services disagree about the wire format (#851).
    // The pathname tells grant-license from licenses-for-email; the body
    // carries an email, so only the helper's safe fields are logged.
    console.warn("internal email request: body did not parse", {
      path: request.nextUrl.pathname,
      ...unparseableRequestFields(request, err),
    });
    return {
      response: NextResponse.json(
        { error: "Invalid JSON body" },
        { status: 400 },
      ),
    };
  }
}
