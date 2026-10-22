/**
 * Credits per minute of audio, for turning a credit balance into minutes.
 *
 * ONE rate for every page that converts credits and minutes: the customer
 * dashboard (`server/api/routers/customer.ts`) and the admin Customers page
 * (`app/[locale]/user/(authenticated)/customers/CustomersClient.tsx`), so an
 * admin and the customer see the same minutes for the same balance (#1657).
 *
 * It is the rate of the default HyperWhisper Cloud STT route: 1 credit =
 * $0.001, and xAI Grok STT batch is $0.10/hour = 1.6667 credits/min. The
 * cloud's `CREDITS_PER_MINUTE` (`hyperwhisper-cloud/src/lib/constants.ts`)
 * holds the same value; `tests/credits-per-minute.test.ts` keeps them equal.
 *
 * No imports: a client component imports this file.
 */
export const CREDITS_PER_MINUTE = 1.67;
