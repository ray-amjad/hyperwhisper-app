"use client";

import { useEffect, useId, useState } from "react";
import { keepPreviousData } from "@tanstack/react-query";
import { api } from "@/lib/trpc/client";
import { formatDate, formatDateTime } from "@/lib/format-date";
import { useRevealAfterLoad } from "@/lib/use-reveal-after-load";

const TIME_RANGES = [
  { label: "7 days", value: 7 },
  { label: "30 days", value: 30 },
  { label: "90 days", value: 90 },
] as const;

/** Devices fetched per "Show more" in an expanded key (#1661). */
export const DEVICE_PAGE_SIZE = 50;

// The page's copy and dates are en-US (formatDate(..., "en-US") below), so
// counts use the same locale: "1,284", never "1284" (#1661). One formatter
// for server and client render, so hydration never disagrees.
const countFormatter = new Intl.NumberFormat("en-US");

export function formatCount(n: number): string {
  return countFormatter.format(n);
}

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
    isFetching,
    isPlaceholderData,
    error: queryError,
    refetch,
  } = api.admin.devices.list.useQuery(
    { days },
    // Keep the previous range's rows on screen while a new range loads,
    // instead of tearing the table down to the skeleton on every click
    // (#863), as the Customers page does.
    { placeholderData: keepPreviousData }
  );

  // A range switch shows the previous range's rows until the new ones land,
  // so the caption, the empty-state copy and an expanded row follow the rows
  // on screen, not the button just pressed. Adjusted during render, so the
  // render that brings the new rows already carries their range. A failed
  // fetch is not placeholder data either, so the copy names the range asked
  // for, as before.
  const [shownDays, setShownDays] = useState(days);
  if (!isPlaceholderData && shownDays !== days) setShownDays(days);

  // In flight with rows already on screen: a range switch or a Refresh.
  const isTransitioning = isFetching && !loading;

  // The first load's rows paint at opacity 0, then fade in.
  const revealed = useRevealAfterLoad(loading);

  const devices = data?.devices ?? [];
  const error = queryError?.message ?? null;

  function truncateKey(key: string) {
    if (key.length <= 12) return key;
    return key.slice(0, 6) + "…" + key.slice(-4);
  }

  return (
    <div className="space-y-6">
      {/* Page Header. Wraps on a phone, so the range buttons and Refresh
          never push the page wider than the viewport (#1764, as #1330). */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-white">
            Device Activations
          </h1>
          <p className="text-gray-400 text-sm mt-1">
            Unique devices per Account Key (active in last {shownDays} days)
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {/* Time Range Selector */}
          <div
            role="group"
            aria-label="Time range"
            className="flex bg-white/5 rounded-lg border border-white/10 overflow-hidden"
          >
            {TIME_RANGES.map((range) => (
              <button
                key={range.value}
                type="button"
                aria-pressed={days === range.value}
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
        <div className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg" role="alert">
          <p className="text-red-300">{error}</p>
        </div>
      )}

      {/* Devices Table */}
      <div
        aria-busy={loading || isTransitioning}
        className={`bg-white/5 backdrop-blur-lg rounded-xl border border-white/10 overflow-hidden transition-opacity ${
          isTransitioning ? "opacity-50" : "opacity-100"
        }`}
      >
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="border-b border-white/10">
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Email
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Account Key
                </th>
                <th className="px-6 py-4 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                  Devices
                </th>
              </tr>
            </thead>
            {/* First load only: skeleton rows, then the real rows fade in.
                A range switch keeps the previous rows mounted (above). */}
            {loading && (
              <tbody aria-hidden className="divide-y divide-white/5">
                {Array.from({ length: 5 }).map((_, i) => (
                  <tr key={i}>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-48 animate-pulse motion-reduce:animate-none" />
                    </td>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-28 animate-pulse motion-reduce:animate-none" />
                    </td>
                    <td className="px-6 py-4">
                      <div className="h-4 bg-white/10 rounded w-12 animate-pulse motion-reduce:animate-none" />
                    </td>
                  </tr>
                ))}
              </tbody>
            )}
            <tbody
              className={`t-skel-content divide-y divide-white/5 transition-[opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
                revealed ? "opacity-100 blur-[0px]" : "opacity-0 blur-[2px]"
              }`}
            >
              {loading ? null : devices.length === 0 ? (
                <tr>
                  <td
                    colSpan={3}
                    className="px-6 py-12 text-center text-gray-400"
                  >
                    No device activations found in the last {shownDays} days.
                  </td>
                </tr>
              ) : (
                devices.map((row) => (
                  <DeviceRow
                    key={row.licenseKeyId}
                    row={row}
                    days={shownDays}
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
          Showing {formatCount(devices.length)} Account Key
          {devices.length !== 1 ? "s" : ""} with active devices
        </p>
      )}
    </div>
  );
}

export function DeviceRow({
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
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search.trim()), 250);

    return () => clearTimeout(timer);
  }, [search]);

  // One page of DEVICE_PAGE_SIZE devices at a time (#1661): a key can hold
  // over a thousand, and rendering them all made the page 72,000 px tall. A
  // new search is a new query key, so paging starts again from the top.
  const {
    data,
    isLoading,
    isFetching,
    isFetchingNextPage,
    hasNextPage,
    fetchNextPage,
  } = api.admin.devices.forLicense.useInfiniteQuery(
    {
      licenseKeyId: row.licenseKeyId,
      days,
      search: debouncedSearch || undefined,
      limit: DEVICE_PAGE_SIZE,
    },
    {
      enabled: expanded,
      getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
      // Keep the current rows on screen while a search narrows them.
      placeholderData: keepPreviousData,
    },
  );
  // Offset paging over a live table: a device validated between two pages
  // moves to the top, so the next page can repeat a row. Keep the first copy,
  // so React never sees two rows with one key.
  const seenIds = new Set<string>();
  const loadedDevices = (
    data?.pages.flatMap((page) => page.devices) ?? []
  ).filter(
    (device) => !seenIds.has(device.deviceId) && seenIds.add(device.deviceId),
  );
  const total = data?.pages[data.pages.length - 1]?.total ?? 0;
  const nextCount = Math.min(DEVICE_PAGE_SIZE, total - loadedDevices.length);
  // A search refetch dims the rows it will replace; "Show more" does not.
  const dimmed = isFetching && !isFetchingNextPage;
  // The row click stays for the mouse; this id lets the disclosure button
  // point aria-controls at the devices row it opens (#1307).
  const panelId = useId();

  return (
    <>
      <tr
        onClick={onToggle}
        className="hover:bg-white/5 transition-colors cursor-pointer"
      >
        <td className="px-6 py-4">
          {/* A real button, so the disclosure is in the Tab order, opens on
              Enter / Space and announces its state (#1307). It stops the
              click so the row's own onClick does not toggle a second time. */}
          <button
            aria-controls={expanded ? panelId : undefined}
            aria-expanded={expanded}
            className="flex items-center gap-3 text-left rounded focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2"
            type="button"
            onClick={(event) => {
              event.stopPropagation();
              onToggle();
            }}
          >
            <span className="w-8 h-8 rounded-full bg-gradient-to-br from-emerald-500 to-teal-500 flex items-center justify-center text-white text-sm font-medium">
              {row.email[0].toUpperCase()}
            </span>
            <span className="text-gray-300">{row.email}</span>
          </button>
        </td>
        <td className="px-6 py-4">
          <code className="text-gray-400 text-sm bg-white/5 px-2 py-1 rounded">
            {truncateKey(row.licenseKey)}
          </code>
        </td>
        <td className="px-6 py-4">
          <div className="flex items-center gap-2">
            <span className="inline-flex items-center px-2.5 py-1 bg-blue-500/20 text-blue-300 rounded-full text-sm font-medium">
              {formatCount(row.deviceCount)}
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
        <tr id={panelId}>
          <td colSpan={3} className="px-6 py-4 bg-white/[0.02]">
            <div className="mb-3">
              <input
                aria-label="Search this key's devices by name or id"
                className="w-full max-w-sm px-3 py-1.5 text-sm bg-white/10 border border-white/20 rounded-lg text-white placeholder-gray-400 focus:outline-none focus:border-emerald-500"
                placeholder="Search devices by name or id..."
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>
            {isLoading ? (
              <div className="space-y-2">
                {Array.from({ length: 2 }).map((_, i) => (
                  <div
                    key={i}
                    className="h-4 bg-white/10 rounded w-64 animate-pulse"
                  />
                ))}
              </div>
            ) : loadedDevices.length === 0 ? (
              <p className="text-gray-400 text-sm">
                {debouncedSearch
                  ? "No devices match that search."
                  : "No devices found."}
              </p>
            ) : (
              <div
                aria-busy={isFetching}
                className={`space-y-2 transition-opacity ${
                  dimmed ? "opacity-50" : "opacity-100"
                }`}
              >
                {loadedDevices.map((device) => (
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
                      {/* <bdi> isolates a user-typed name, so an Arabic name or a
                          bracket never reorders the id beside it on an RTL page (#1659).
                          The gap sits on the parent: a margin on the <bdi> follows the
                          id's own LTR direction and lands on the wrong side in RTL. */}
                      <div className="flex flex-wrap items-baseline gap-x-2">
                        <bdi className="text-white text-sm">
                          {device.deviceName || device.deviceId}
                        </bdi>
                        {device.deviceName && (
                          <bdi className="text-gray-400 text-xs">
                            {device.deviceId}
                          </bdi>
                        )}
                      </div>
                    </div>
                    <div className="text-right text-xs text-gray-400">
                      <div>First seen: {formatDate(device.createdAt, "en-US")}</div>
                      <div>
                        Last active: {formatDateTime(device.lastValidatedAt, "en-US")}
                      </div>
                    </div>
                  </div>
                ))}
                <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
                  <p className="text-gray-400 text-xs">
                    Showing {formatCount(loadedDevices.length)} of{" "}
                    {formatCount(total)} device{total !== 1 ? "s" : ""}
                  </p>
                  {hasNextPage && (
                    <button
                      className="px-3 py-1.5 text-sm text-emerald-300 bg-emerald-500/10 hover:bg-emerald-500/20 rounded-lg transition-colors disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2"
                      disabled={isFetchingNextPage}
                      type="button"
                      onClick={() => fetchNextPage()}
                    >
                      {isFetchingNextPage
                        ? "Loading..."
                        : `Show ${formatCount(nextCount)} more`}
                    </button>
                  )}
                </div>
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}
