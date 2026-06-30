import { Resend } from "resend";

import { env } from "@/src/env/server.mjs";

export const resend = new Resend(env.RESEND_API_KEY);

// Default from email address
export const DEFAULT_FROM_EMAIL = "HyperWhisper <support@hyperwhisper.com>";
