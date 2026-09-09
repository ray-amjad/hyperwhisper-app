import { TRPCError } from "@trpc/server";

export interface CustomerRefundSource {
  retrievePaymentIntent: (
    stripeSessionId: string,
  ) => Promise<string | { id: string } | null>;
  createRefund: (
    paymentIntentId: string,
    idempotencyKey: string,
  ) => Promise<void>;
}

export interface CustomerPaymentRefunder {
  refundLicensePayment: (
    licenseKeyId: string,
    stripeSessionId: string,
  ) => Promise<void>;
}

/** Builds the license-payment refund flow around its Stripe operations. */
export function createCustomerPaymentRefunder(
  source: CustomerRefundSource,
): CustomerPaymentRefunder {
  async function refundLicensePayment(
    licenseKeyId: string,
    stripeSessionId: string,
  ): Promise<void> {
    const paymentIntent = await source.retrievePaymentIntent(stripeSessionId);

    if (!paymentIntent) {
      throw new TRPCError({
        code: "BAD_REQUEST",
        message: "No payment intent found for this session",
      });
    }

    const paymentIntentId =
      typeof paymentIntent === "string" ? paymentIntent : paymentIntent.id;

    await source.createRefund(paymentIntentId, `admin-refund-${licenseKeyId}`);
  }

  return { refundLicensePayment };
}
