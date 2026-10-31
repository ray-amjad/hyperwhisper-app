"use client";

import { api } from "@/lib/trpc/client";
import { Spinner } from "@/components/ui/Spinner";
import { BTN_SECONDARY, CARD, LABEL } from "@/components/user/panel-styles";

export default function BillingCard() {
  const { data: billing } = api.customer.billingProviders.useQuery();
  const stripePortalMutation = api.customer.stripePortalUrl.useMutation({
    onSuccess: (data) => window.open(data.url, "_blank"),
  });

  return (
    <div className={`${CARD} p-5 sm:p-6`}>
      <p className={`${LABEL} mb-3.5`}>Billing & Invoices</p>
      <div className="flex flex-wrap gap-3">
        {billing?.hasStripe && (
          <button
            onClick={() => stripePortalMutation.mutate()}
            disabled={stripePortalMutation.isPending}
            className={BTN_SECONDARY}
          >
            {stripePortalMutation.isPending && (
              <Spinner className="w-4 h-4" label="Opening" />
            )}
            Stripe Portal
          </button>
        )}
        {billing?.hasPolar && (
          <a
            href="https://polar.sh/hyperwhisper/portal"
            target="_blank"
            rel="noopener noreferrer"
            className={BTN_SECONDARY}
          >
            Polar Portal
          </a>
        )}
      </div>
      {stripePortalMutation.error && (
        <p className="text-sm text-rose-300 mt-2.5" role="alert">
          {stripePortalMutation.error.message}
        </p>
      )}
    </div>
  );
}
