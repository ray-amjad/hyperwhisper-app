"use client";

import { useState, useEffect, useRef } from "react";
import { api } from "@/lib/trpc/client";

const CREDITS_PER_MINUTE = 6.3;

function formatCredits(credits: number) {
  return credits.toLocaleString("en-US", {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  });
}

export default function CustomersClient() {
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 300);
    return () => clearTimeout(timer);
  }, [search]);

  // Timers that fire after a delay and call setState/refetch — tracked so they
  // can be cleared on unmount to avoid set-after-unmount warnings and wasted
  // authenticated refetches.
  const refundTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (refundTimerRef.current) clearTimeout(refundTimerRef.current);
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  const {
    data,
    isLoading: loading,
    error: queryError,
    refetch,
  } = api.admin.customers.list.useQuery(
    debouncedSearch ? { search: debouncedSearch } : undefined
  );

  const [showGrant, setShowGrant] = useState(false);
  const [grantEmail, setGrantEmail] = useState("");
  const [grantResult, setGrantResult] = useState<{ email: string; licenseKey: string } | null>(null);

  const grantMutation = api.admin.customers.grant.useMutation({
    onSuccess: (data) => {
      setGrantResult(data);
      setGrantEmail("");
      refetch();
    },
  });

  // Refund modal state
  const [refundTarget, setRefundTarget] = useState<{
    id: string;
    email: string;
    key: string;
  } | null>(null);

  const refundMutation = api.admin.customers.refund.useMutation({
    onSuccess: () => {
      refundTimerRef.current = setTimeout(() => {
        setRefundTarget(null);
        refundMutation.reset();
        refetch();
      }, 1500);
    },
  });

  // Add-credits modal state. Targets a single license key directly, so no
  // license selector is needed.
  const [creditTarget, setCreditTarget] = useState<{
    id: string;
    email: string;
    key: string;
    credits: number;
  } | null>(null);
  const [creditAmount, setCreditAmount] = useState("");

  const addCreditsMutation = api.admin.customers.addCredits.useMutation({
    onSuccess: () => {
      setCreditTarget(null);
      setCreditAmount("");
      refetch();
    },
  });

  function openAddCredits(target: {
    id: string;
    email: string;
    key: string;
    credits: number;
  }) {
    setCreditTarget(target);
    setCreditAmount("");
    addCreditsMutation.reset();
  }

  function handleAddCredits(e: React.FormEvent) {
    e.preventDefault();
    if (!creditTarget) return;
    const amount = parseFloat(creditAmount);
    if (isNaN(amount) || amount <= 0) return;
    addCreditsMutation.mutate({ licenseKeyId: creditTarget.id, amount });
  }

  // Inline email-edit state
  const [editingUserId, setEditingUserId] = useState<string | null>(null);
  const [editEmail, setEditEmail] = useState("");
  // Session-only marker for customers whose email was edited in this view.
  const [editedUserIds, setEditedUserIds] = useState<Set<string>>(new Set());

  const updateEmailMutation = api.admin.customers.updateEmail.useMutation({
    onSuccess: (_data, variables) => {
      setEditingUserId(null);
      setEditedUserIds((prev) => {
        const next = new Set(prev);
        next.add(variables.userId);
        return next;
      });
      refetch();
    },
  });

  function startEdit(userId: string, email: string) {
    setEditingUserId(userId);
    setEditEmail(email);
    updateEmailMutation.reset();
  }

  function cancelEdit() {
    setEditingUserId(null);
    updateEmailMutation.reset();
  }

  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  async function copyKey(key: string) {
    try {
      await navigator.clipboard.writeText(key);
      setCopiedKey(key);
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
      copyTimerRef.current = setTimeout(() => setCopiedKey(null), 2000);
    } catch {
      // Clipboard API unavailable (e.g. insecure context) — ignore.
    }
  }

  const customers = data?.customers ?? [];
  const error = queryError?.message ?? null;

  function formatDate(timestamp: number) {
    return new Date(timestamp * 1000).toLocaleDateString("en-US", {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  }

  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-white">Customers</h1>
          <p className="text-gray-400 text-sm mt-1">
            All customers with their license keys and credit balances
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => { setShowGrant(!showGrant); setGrantResult(null); grantMutation.reset(); }}
            className="px-4 py-2 bg-emerald-500 hover:bg-emerald-600 text-white rounded-lg transition-colors flex items-center gap-2"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            Grant Account Key
          </button>
          <button
            onClick={() => refetch()}
            disabled={loading}
            className="px-4 py-2 bg-white/10 hover:bg-white/15 text-gray-300 rounded-lg transition-colors flex items-center gap-2 disabled:opacity-50"
          >
            <svg
              className={`w-4 h-4 ${loading ? "animate-spin" : ""}`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
              />
            </svg>
            Refresh
          </button>
        </div>
      </div>

      {/* Search */}
      <div>
        <input
          type="text"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search by email..."
          className="w-full max-w-md px-4 py-2 bg-white/10 border border-white/20 rounded-lg text-white placeholder-gray-500 focus:outline-none focus:border-emerald-500"
        />
      </div>

      {/* Grant License Form */}
      {showGrant && (
        <div className="p-4 bg-white/5 backdrop-blur-lg rounded-xl border border-white/10 space-y-3">
          <h3 className="text-white font-medium">Grant Account Key by Email</h3>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              if (grantEmail.trim()) {
                setGrantResult(null);
                grantMutation.mutate({ email: grantEmail.trim() });
              }
            }}
            className="flex items-center gap-3"
          >
            <input
              type="email"
              value={grantEmail}
              onChange={(e) => setGrantEmail(e.target.value)}
              placeholder="user@example.com"
              required
              className="flex-1 px-3 py-2 bg-white/10 border border-white/20 rounded-lg text-white placeholder-gray-500 focus:outline-none focus:border-emerald-500"
            />
            <button
              type="submit"
              disabled={grantMutation.isPending}
              className="px-4 py-2 bg-emerald-500 hover:bg-emerald-600 text-white rounded-lg transition-colors disabled:opacity-50"
            >
              {grantMutation.isPending ? "Granting..." : "Grant"}
            </button>
          </form>
          {grantMutation.error && (
            <p className="text-red-300 text-sm">{grantMutation.error.message}</p>
          )}
          {grantResult && (
            <div className="p-3 bg-emerald-500/20 border border-emerald-500/30 rounded-lg space-y-1">
              <p className="text-emerald-300 text-sm">Account Key granted to <span className="font-medium">{grantResult.email}</span></p>
              <p className="text-white font-mono text-sm select-all">{grantResult.licenseKey}</p>
            </div>
          )}
        </div>
      )}

      {/* Error Message */}
      {error && (
        <div className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg">
          <p className="text-red-300">{error}</p>
        </div>
      )}

      {/* Customers Table */}
      <div className="bg-white/5 backdrop-blur-lg rounded-xl border border-white/10 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="border-b border-white/10">
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Email
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Account Keys
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Total
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Created
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-white/5">
              {loading ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <tr key={i}>
                    {Array.from({ length: 4 }).map((_, j) => (
                      <td key={j} className="px-6 py-4">
                        <div className="h-4 bg-white/10 rounded w-24 animate-pulse" />
                      </td>
                    ))}
                  </tr>
                ))
              ) : customers.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-6 py-12 text-center text-gray-400">
                    No customers found.
                  </td>
                </tr>
              ) : (
                customers.map((customer) => {
                  const isEditing = editingUserId === customer.userId;
                  return (
                    <tr
                      key={customer.userId}
                      className="hover:bg-white/5 transition-colors align-top"
                    >
                      {/* Email + inline edit */}
                      <td className="px-6 py-4">
                        {isEditing ? (
                          <form
                            onSubmit={(e) => {
                              e.preventDefault();
                              const value = editEmail.trim();
                              if (value) {
                                updateEmailMutation.mutate({
                                  userId: customer.userId,
                                  newEmail: value,
                                });
                              }
                            }}
                            className="flex flex-col gap-2"
                          >
                            <div className="flex items-center gap-2">
                              <input
                                type="email"
                                value={editEmail}
                                onChange={(e) => setEditEmail(e.target.value)}
                                autoFocus
                                required
                                className="px-2 py-1 bg-white/10 border border-emerald-500/60 rounded text-white text-sm focus:outline-none focus:border-emerald-400 min-w-[14rem]"
                              />
                              <button
                                type="submit"
                                disabled={updateEmailMutation.isPending}
                                className="px-2.5 py-1 bg-emerald-500 hover:bg-emerald-600 text-white rounded text-xs font-medium transition-colors disabled:opacity-50"
                              >
                                {updateEmailMutation.isPending ? "Saving..." : "Save"}
                              </button>
                              <button
                                type="button"
                                onClick={cancelEdit}
                                disabled={updateEmailMutation.isPending}
                                className="px-2.5 py-1 bg-white/10 hover:bg-white/15 text-gray-300 rounded text-xs font-medium transition-colors border border-white/10 disabled:opacity-50"
                              >
                                Cancel
                              </button>
                            </div>
                            <p className="text-amber-300/80 text-xs">
                              Updates this customer&apos;s account email and moves
                              {" "}
                              {customer.licenseCount === 1
                                ? "their Account Key"
                                : `all ${customer.licenseCount} Account Keys`}
                              {" "}
                              to the new address.
                            </p>
                            {updateEmailMutation.error && (
                              <p className="text-red-300 text-xs">
                                {updateEmailMutation.error.message}
                              </p>
                            )}
                          </form>
                        ) : (
                          <div className="flex items-center gap-2 flex-wrap">
                            <span className="text-gray-300">{customer.email}</span>
                            {customer.licenseCount > 1 && (
                              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-blue-500/20 text-blue-300">
                                {customer.licenseCount} licenses
                              </span>
                            )}
                            {editedUserIds.has(customer.userId) && (
                              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-white/10 text-gray-400">
                                Edited
                              </span>
                            )}
                            <button
                              type="button"
                              onClick={() => startEdit(customer.userId, customer.email)}
                              title="Edit email"
                              aria-label="Edit email"
                              className="text-gray-500 hover:text-emerald-300 transition-colors"
                            >
                              <svg
                                className="h-3.5 w-3.5"
                                fill="none"
                                viewBox="0 0 24 24"
                                strokeWidth={1.8}
                                stroke="currentColor"
                              >
                                <path
                                  strokeLinecap="round"
                                  strokeLinejoin="round"
                                  d="M16.862 4.487l1.687-1.688a1.875 1.875 0 112.652 2.652L10.582 16.07a4.5 4.5 0 01-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 011.13-1.897l8.932-8.931zm0 0L19.5 7.125"
                                />
                              </svg>
                            </button>
                          </div>
                        )}
                      </td>

                      {/* License keys (one per license, each copyable + refundable) */}
                      <td className="px-6 py-4">
                        <div className="flex flex-col gap-2">
                          {customer.licenses.map((license) => (
                            <div key={license.id} className="flex items-center gap-2 flex-wrap">
                              <button
                                type="button"
                                onClick={() => copyKey(license.key)}
                                title="Click to copy Account Key"
                                aria-label={
                                  copiedKey === license.key
                                    ? "Account Key copied"
                                    : "Copy Account Key"
                                }
                                className="group inline-flex items-center gap-1.5 rounded-md border border-white/10 bg-white/5 px-2 py-1 font-mono text-xs text-gray-300 transition-colors hover:border-blue-400/40 hover:bg-blue-500/10 hover:text-blue-200"
                              >
                                <code>{license.key.slice(0, 8)}...</code>
                                {copiedKey === license.key ? (
                                  <svg
                                    className="h-3.5 w-3.5 text-emerald-400"
                                    fill="none"
                                    viewBox="0 0 24 24"
                                    strokeWidth={2.5}
                                    stroke="currentColor"
                                  >
                                    <path
                                      strokeLinecap="round"
                                      strokeLinejoin="round"
                                      d="M4.5 12.75l6 6 9-13.5"
                                    />
                                  </svg>
                                ) : (
                                  <svg
                                    className="h-3.5 w-3.5 text-gray-500 transition-colors group-hover:text-blue-300"
                                    fill="none"
                                    viewBox="0 0 24 24"
                                    strokeWidth={1.8}
                                    stroke="currentColor"
                                  >
                                    <path
                                      strokeLinecap="round"
                                      strokeLinejoin="round"
                                      d="M15.75 17.25v3.375c0 .621-.504 1.125-1.125 1.125h-9.75a1.125 1.125 0 01-1.125-1.125V7.875c0-.621.504-1.125 1.125-1.125H6.75a9.06 9.06 0 011.5.124m7.5 10.376h3.375c.621 0 1.125-.504 1.125-1.125V11.25c0-4.46-3.243-8.161-7.5-8.876a9.06 9.06 0 00-1.5-.124H9.375c-.621 0-1.125.504-1.125 1.125v3.5m7.5 10.375H9.375a1.125 1.125 0 01-1.125-1.125v-9.25m11.25 5.5h-1.875a1.125 1.125 0 01-1.125-1.125v-1.875M18 14.25v-3.75"
                                    />
                                  </svg>
                                )}
                              </button>
                              <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium ${
                                license.status === "revoked"
                                  ? "bg-red-500/20 text-red-300"
                                  : "bg-emerald-500/20 text-emerald-300"
                              }`}>
                                {license.status}
                              </span>
                              <span className="inline-flex items-center gap-1 text-[11px] font-medium text-blue-300">
                                {formatCredits(license.credits)}
                                <span className="text-gray-500">
                                  ({Math.floor(license.credits / CREDITS_PER_MINUTE)} min)
                                </span>
                              </span>
                              <button
                                onClick={() =>
                                  openAddCredits({
                                    id: license.id,
                                    email: customer.email,
                                    key: license.key,
                                    credits: license.credits,
                                  })
                                }
                                className="px-2 py-0.5 bg-emerald-500/20 hover:bg-emerald-500/30 text-emerald-300 rounded transition-colors text-[11px] font-medium"
                              >
                                Add credits
                              </button>
                              {license.stripeSessionId && license.status !== "revoked" && (
                                <button
                                  onClick={() =>
                                    setRefundTarget({
                                      id: license.id,
                                      email: customer.email,
                                      key: license.key,
                                    })
                                  }
                                  className="px-2 py-0.5 bg-red-500/20 hover:bg-red-500/30 text-red-300 rounded transition-colors text-[11px] font-medium"
                                >
                                  Refund
                                </button>
                              )}
                            </div>
                          ))}
                        </div>
                      </td>

                      {/* Total credits */}
                      <td className="px-6 py-4">
                        <span className="text-blue-300 text-sm font-medium">
                          {customer.totalCredits.toLocaleString()}
                        </span>
                      </td>

                      {/* Created */}
                      <td className="px-6 py-4 text-gray-400 text-sm">
                        {formatDate(customer.created)}
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Customer Count */}
      {!loading && customers.length > 0 && (
        <p className="text-gray-400 text-sm">
          Showing {customers.length} customer{customers.length !== 1 ? "s" : ""}
        </p>
      )}

      {/* Refund Modal */}
      {refundTarget && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div
            className="absolute inset-0 bg-black/60 backdrop-blur-sm"
            onClick={() => { if (!refundMutation.isPending) { setRefundTarget(null); refundMutation.reset(); } }}
          />
          <div className="relative bg-slate-800 border border-white/10 rounded-xl shadow-2xl w-full max-w-md mx-4 p-6 space-y-5">
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold text-white">Refund Account Key</h2>
              <button
                onClick={() => { setRefundTarget(null); refundMutation.reset(); }}
                className="text-gray-400 hover:text-white transition-colors"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            <div className="space-y-2 text-sm">
              <p className="text-gray-400">Email: <span className="text-white">{refundTarget.email}</span></p>
              <p className="text-gray-400">Key: <code className="text-white font-mono text-xs">{refundTarget.key}</code></p>
            </div>

            {refundMutation.isSuccess ? (
              <div className="p-3 bg-emerald-500/20 border border-emerald-500/30 rounded-lg">
                <p className="text-emerald-300 text-sm">
                  Refund processed successfully{refundMutation.data?.revoked ? " and Account Key revoked" : ""}.
                </p>
              </div>
            ) : (
              <>
                {refundMutation.error && (
                  <div className="p-3 bg-red-500/20 border border-red-500/30 rounded-lg">
                    <p className="text-red-300 text-sm">{refundMutation.error.message}</p>
                  </div>
                )}
                <div className="flex gap-3 pt-2">
                  <button
                    onClick={() =>
                      refundMutation.mutate({
                        licenseKeyId: refundTarget.id,
                        revokeLicense: true,
                      })
                    }
                    disabled={refundMutation.isPending}
                    className="flex-1 px-4 py-2 bg-red-500 hover:bg-red-600 text-white rounded-lg transition-colors disabled:opacity-50 text-sm font-medium"
                  >
                    {refundMutation.isPending ? "Processing..." : "Refund & Revoke"}
                  </button>
                  <button
                    onClick={() =>
                      refundMutation.mutate({
                        licenseKeyId: refundTarget.id,
                        revokeLicense: false,
                      })
                    }
                    disabled={refundMutation.isPending}
                    className="flex-1 px-4 py-2 bg-white/10 hover:bg-white/15 text-gray-300 rounded-lg transition-colors disabled:opacity-50 text-sm font-medium border border-white/10"
                  >
                    {refundMutation.isPending ? "Processing..." : "Refund Only"}
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}

      {/* Add Credits Modal (targets a single license key directly) */}
      {creditTarget && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          {/* Backdrop */}
          <div
            className="absolute inset-0 bg-black/60 backdrop-blur-sm"
            onClick={() => { if (!addCreditsMutation.isPending) { setCreditTarget(null); addCreditsMutation.reset(); } }}
          />

          {/* Modal */}
          <div className="relative bg-slate-800 border border-white/10 rounded-xl shadow-2xl w-full max-w-md mx-4 p-6 space-y-5">
            {/* Header */}
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold text-white">Add Credits</h2>
              <button
                onClick={() => { setCreditTarget(null); addCreditsMutation.reset(); }}
                className="text-gray-400 hover:text-white transition-colors"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            {/* Key Info */}
            <div className="p-3 bg-white/5 rounded-lg space-y-1">
              <p className="text-white font-medium">{creditTarget.email}</p>
              <p className="text-gray-300 font-mono text-sm">
                {creditTarget.key.slice(0, 16)}...
              </p>
              <p className="text-gray-400 text-sm">
                Current balance: {formatCredits(creditTarget.credits)} credits
                ({Math.floor(creditTarget.credits / CREDITS_PER_MINUTE)} minutes)
              </p>
            </div>

            {/* Form */}
            <form onSubmit={handleAddCredits} className="space-y-4">
              {/* Credit Amount */}
              <div>
                <label className="block text-sm font-medium text-gray-300 mb-1.5">
                  Credits to Add
                </label>
                <input
                  type="number"
                  value={creditAmount}
                  onChange={(e) => setCreditAmount(e.target.value)}
                  placeholder="e.g. 5000"
                  min="1"
                  step="1"
                  required
                  className="w-full px-3 py-2 bg-white/10 border border-white/20 rounded-lg text-white placeholder-gray-500 focus:outline-none focus:border-emerald-500"
                />
                {creditAmount && parseFloat(creditAmount) > 0 && (
                  <p className="text-gray-400 text-xs mt-1">
                    = {Math.floor(parseFloat(creditAmount) / CREDITS_PER_MINUTE)} minutes of transcription
                  </p>
                )}
              </div>

              {/* Quick amounts */}
              <div className="flex gap-2">
                {[1000, 5000, 10000, 20000].map((amount) => (
                  <button
                    key={amount}
                    type="button"
                    onClick={() => setCreditAmount(amount.toString())}
                    className="flex-1 px-2 py-1.5 bg-white/5 hover:bg-white/10 border border-white/10 rounded-lg text-gray-300 text-sm transition-colors"
                  >
                    {amount.toLocaleString()}
                  </button>
                ))}
              </div>

              {/* Error */}
              {addCreditsMutation.error && (
                <p className="text-red-300 text-sm">{addCreditsMutation.error.message}</p>
              )}

              {/* Actions */}
              <div className="flex gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => { setCreditTarget(null); addCreditsMutation.reset(); }}
                  className="flex-1 px-4 py-2 bg-white/10 hover:bg-white/15 text-gray-300 rounded-lg transition-colors"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={addCreditsMutation.isPending || !creditAmount || parseFloat(creditAmount) <= 0}
                  className="flex-1 px-4 py-2 bg-emerald-500 hover:bg-emerald-600 text-white rounded-lg transition-colors disabled:opacity-50"
                >
                  {addCreditsMutation.isPending ? "Adding..." : "Add Credits"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
