"use client";

import { useState } from "react";
import { api } from "@/lib/trpc/client";

const TIME_RANGES = [
  { label: "7 days", value: 7 },
  { label: "30 days", value: 30 },
  { label: "90 days", value: 90 },
] as const;

/**
 * Devices Client Component
 *
 * Displays device activation counts per license with expandable rows
 * showing individual device details.
 * Admin-only page — server component handles authorization.
 */
export default function DevicesClient() {
  const [days, setDays] = useState(30);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const {
    data,
    isLoading: loading,
    error: queryError,
    refetch,
  } = api.admin.devices.list.useQuery({ days });

  const devices = data?.devices ?? [];
  const error = queryError?.message ?? null;

  function truncateKey(key: string) {
    if (key.length <= 12) return key;
    return key.slice(0, 6) + "…" + key.slice(-4);
  }

  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-white">
            Device Activations
          </h1>
          <p className="text-gray-400 text-sm mt-1">
            Unique devices per license key (active in last {days} days)
          </p>
        </div>
        <div className="flex items-center gap-2">
          {/* Time Range Selector */}
          <div className="flex bg-white/5 rounded-lg border border-white/10 overflow-hidden">
            {TIME_RANGES.map((range) => (
              <button
                key={range.value}
                onClick={() => setDays(range.value)}
                className={`px-3 py-2 text-sm transition-colors ${
                  days === range.value
                    ? "bg-emerald-500/20 text-emerald-300"
                    : "text-gray-400 hover:text-white hover:bg-white/5"
                }`}
              >
                {range.label}
              </button>
            ))}
          </div>
          <button
            onClick={() => refetch()}
            disabled={loading}
            className="px-4 py-2 bg-emerald-500 hover:bg-emerald-600 text-white rounded-lg transition-colors flex items-center gap-2 disabled:opacity-50"
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

      {/* Error Message */}
      {error && (
        <div className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg">
          <p className="text-red-300">{error}</p>
        </div>
      )}

      {/* Devices Table */}
      <div className="bg-white/5 backdrop-blur-lg rounded-xl border border-white/10 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="border-b border-white/10">
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Email
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  License Key
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Devices
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-white/5">
              {loading ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <tr key={i}>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-48 animate-pulse" />
                    </td>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-28 animate-pulse" />
                    </td>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-12 animate-pulse" />
                    </td>
                  </tr>
                ))
              ) : devices.length === 0 ? (
                <tr>
                  <td
                    colSpan={3}
                    className="px-6 py-12 text-center text-gray-400"
                  >
                    No device activations found in the last {days} days.
                  </td>
                </tr>
              ) : (
                devices.map((row) => (
                  <DeviceRow
                    key={row.licenseKeyId}
                    row={row}
                    days={days}
                    truncateKey={truncateKey}
                    expanded={expandedId === row.licenseKeyId}
                    onToggle={() =>
                      setExpandedId(
                        expandedId === row.licenseKeyId
                          ? null
                          : row.licenseKeyId
                      )
                    }
                  />
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Row Count */}
      {!loading && devices.length > 0 && (
        <p className="text-gray-400 text-sm">
          Showing {devices.length} license
          {devices.length !== 1 ? "s" : ""} with active devices
        </p>
      )}
    </div>
  );
}

function DeviceRow({
  row,
  days,
  truncateKey,
  expanded,
  onToggle,
}: {
  row: {
    licenseKeyId: string;
    email: string;
    licenseKey: string;
    deviceCount: number;
  };
  days: number;
  truncateKey: (key: string) => string;
  expanded: boolean;
  onToggle: () => void;
}) {
  const { data, isLoading } = api.admin.devices.forLicense.useQuery(
    { licenseKeyId: row.licenseKeyId, days },
    { enabled: expanded }
  );

  function formatDate(date: string | Date) {
    return new Date(date).toLocaleDateString("en-US", {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  }

  function formatDateTime(date: string | Date) {
    return new Date(date).toLocaleString("en-US", {
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  return (
    <>
      <tr
        onClick={onToggle}
        className="hover:bg-white/5 transition-colors cursor-pointer"
      >
        <td className="px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="w-8 h-8 rounded-full bg-gradient-to-br from-emerald-500 to-teal-500 flex items-center justify-center text-white text-sm font-medium">
              {row.email[0].toUpperCase()}
            </div>
            <span className="text-gray-300">{row.email}</span>
          </div>
        </td>
        <td className="px-6 py-4">
          <code className="text-gray-400 text-sm bg-white/5 px-2 py-1 rounded">
            {truncateKey(row.licenseKey)}
          </code>
        </td>
        <td className="px-6 py-4">
          <div className="flex items-center gap-2">
            <span className="inline-flex items-center px-2.5 py-1 bg-blue-500/20 text-blue-300 rounded-full text-sm font-medium">
              {row.deviceCount}
            </span>
            <svg
              className={`w-4 h-4 text-gray-400 transition-transform ${
                expanded ? "rotate-180" : ""
              }`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M19 9l-7 7-7-7"
              />
            </svg>
          </div>
        </td>
      </tr>
      {expanded && (
        <tr>
          <td colSpan={3} className="px-6 py-4 bg-white/[0.02]">
            {isLoading ? (
              <div className="space-y-2">
                {Array.from({ length: 2 }).map((_, i) => (
                  <div
                    key={i}
                    className="h-4 bg-white/10 rounded w-64 animate-pulse"
                  />
                ))}
              </div>
            ) : !data?.devices.length ? (
              <p className="text-gray-500 text-sm">No devices found.</p>
            ) : (
              <div className="space-y-2">
                {data.devices.map((device) => (
                  <div
                    key={device.deviceId}
                    className="flex items-center justify-between py-2 px-3 bg-white/5 rounded-lg"
                  >
                    <div className="flex items-center gap-3">
                      <svg
                        className="w-4 h-4 text-gray-400"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          strokeLinecap="round"
                          strokeLinejoin="round"
                          strokeWidth={2}
                          d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                        />
                      </svg>
                      <div>
                        <span className="text-white text-sm">
                          {device.deviceName || device.deviceId}
                        </span>
                        {device.deviceName && (
                          <span className="text-gray-500 text-xs ml-2">
                            {device.deviceId}
                          </span>
                        )}
                      </div>
                    </div>
                    <div className="text-right text-xs text-gray-400">
                      <div>First seen: {formatDate(device.createdAt)}</div>
                      <div>
                        Last active: {formatDateTime(device.lastValidatedAt)}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}
