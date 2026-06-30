"use client";

import { api } from "@/lib/trpc/client";

export default function BillingCard() {
  const { data: billing } = api.customer.billingProviders.useQuery();
  const stripePortalMutation = api.customer.stripePortalUrl.useMutation({
    onSuccess: (data) => window.open(data.url, "_blank"),
  });

  return (
    <div className="bg-white/5 rounded-xl border border-white/10 p-5">
      <p className="text-sm text-gray-400 mb-3">Billing & Invoices</p>
      <div className="flex flex-wrap gap-3">
        {billing?.hasStripe && (
          <button
            onClick={() => stripePortalMutation.mutate()}
            disabled={stripePortalMutation.isPending}
            className="px-4 py-2 text-sm text-gray-300 hover:text-white bg-white/5 hover:bg-white/10 rounded-lg transition-colors disabled:opacity-50"
          >
            {stripePortalMutation.isPending ? "Opening..." : "Stripe Portal"}
          </button>
        )}
        {billing?.hasPolar && (
          <a
            href="https://polar.sh/hyperwhisper/portal"
            target="_blank"
            rel="noopener noreferrer"
            className="px-4 py-2 text-sm text-gray-300 hover:text-white bg-white/5 hover:bg-white/10 rounded-lg transition-colors"
          >
            Polar Portal
          </a>
        )}
      </div>
      {stripePortalMutation.error && (
        <p className="text-sm text-red-400 mt-2">
          {stripePortalMutation.error.message}
        </p>
      )}
    </div>
  );
}
