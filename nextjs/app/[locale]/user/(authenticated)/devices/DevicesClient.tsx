"use client";

import { useEffect, useId, useState } from "react";
import { keepPreviousData } from "@tanstack/react-query";
import { api } from "@/lib/trpc/client";
import { formatDate, formatDateTime } from "@/lib/format-date";
import { useRevealAfterLoad } from "@/lib/use-reveal-after-load";
import { ErrorReveal } from "@/components/ErrorReveal";
import { HeightTween } from "@/components/ui/HeightTween";
import { Spinner } from "@/components/ui/Spinner";
import {
  ALERT_ERROR,
  BTN_SECONDARY,
  BTN_SECONDARY_XS,
  CARD,
  CHIP,
  CHIP_LAV,
  CODE_BOX,
  FOCUS_RING,
  INPUT,
  PAGE_SUB,
  PAGE_TITLE,
  SEG,
  SEG_ITEM,
  SEG_OFF,
  SEG_ON,
  SKEL,
  TBODY,
  TH,
  TR_HOVER,
} from "@/components/user/panel-styles";

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
          <h1 className={PAGE_TITLE}>
            Device Activations
          </h1>
          <p className={PAGE_SUB}>
            Unique devices per Account Key (active in last {shownDays} days)
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {/* Time Range Selector */}
          <div
            role="group"
            aria-label="Time range"
            className={SEG}
          >
            {TIME_RANGES.map((range) => (
              <button
                key={range.value}
                type="button"
                aria-pressed={days === range.value}
                onClick={() => setDays(range.value)}
                className={`${SEG_ITEM} ${
                  days === range.value ? SEG_ON : SEG_OFF
                }`}
              >
                {range.label}
              </button>
            ))}
          </div>
          <button
            onClick={() => refetch()}
            disabled={isFetching}
            className={BTN_SECONDARY}
          >
            {isFetching ? (
              <Spinner className="w-4 h-4" />
            ) : (
              <svg
                className="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
                />
              </svg>
            )}
            Refresh
          </button>
        </div>
      </div>

      {/* Error Message. It reveals (#2286) instead of arriving at full height
          in one frame; the space-y-6 gap rides inside the clip as pb-6. */}
      <ErrorReveal gap="pb-6">
        {error && (
          <div className={`p-4 ${ALERT_ERROR}`} role="alert">
            <p className="text-rose-300">{error}</p>
          </div>
        )}
      </ErrorReveal>

      {/* Devices Table. A range switch that changes the row count tweens
          the card's height (#2757, as #916 did on Customers) instead of
          stepping in one frame; the first load's rows land at once. */}
      <HeightTween
        className={`${CARD} overflow-hidden ${
          isTransitioning ? "opacity-50" : "opacity-100"
        }`}
        // Tailwind's transition-opacity, kept beside the height tween.
        transition="opacity 150ms cubic-bezier(0.4, 0, 0.2, 1)"
        trigger={data}
      >
        {/* aria-busy sits here: HeightTween's box takes no aria props, and
            this box holds the whole table. The box scrolls sideways on a
            phone, so it is a named region with a tab stop (#1148, as #1145
            did on /download): a keyboard user can scroll it.
            The ring is inset (-outline-offset-2), and rounded like the card:
            the card clips its overflow, so an outset ring would be hidden. */}
        <div
          aria-busy={loading || isTransitioning}
          aria-label="Device activations table"
          className="overflow-x-auto rounded-[20px] focus-visible:outline-2 focus-visible:outline-[var(--dusk-lav)] focus-visible:-outline-offset-2"
          role="region"
          // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
          tabIndex={0}
        >
          <table className="w-full min-w-[36rem]">
            <thead>
              <tr className="border-b border-[var(--dusk-line)] bg-white/[.02]">
                <th className={TH}>
                  Email
                </th>
                <th className={TH}>
                  Account Key
                </th>
                <th className={TH}>
                  Devices
                </th>
              </tr>
            </thead>
            {/* First load only: skeleton rows, then the real rows fade in.
                A range switch keeps the previous rows mounted (above). */}
            {loading && (
              <tbody aria-hidden className={TBODY}>
                {Array.from({ length: 5 }).map((_, i) => (
                  <tr key={i}>
                    <td className="px-6 py-4">
                      <div className={`h-4 w-48 ${SKEL}`} />
                    </td>
                    <td className="px-6 py-4">
                      <div className={`h-4 w-28 ${SKEL}`} />
                    </td>
                    <td className="px-6 py-4">
                      <div className={`h-4 w-12 ${SKEL}`} />
                    </td>
                  </tr>
                ))}
              </tbody>
            )}
            <tbody
              className={`t-skel-content ${TBODY} transition-[opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
                revealed ? "opacity-100 blur-[0px]" : "opacity-0 blur-[2px]"
              }`}
            >
              {loading ? null : devices.length === 0 ? (
                <tr>
                  <td
                    colSpan={3}
                    className="px-6 py-14 text-center text-[var(--dusk-mute)]"
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
      </HeightTween>

      {/* Row Count */}
      {!loading && devices.length > 0 && (
        <p className="text-[var(--dusk-mute)] text-sm tabular-nums">
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
  // The search result's identity: the device list tweens when it changes.
  const firstPage = data?.pages[0];
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
        className={`${TR_HOVER} cursor-pointer`}
      >
        <td className="px-6 py-4">
          {/* A real button, so the disclosure is in the Tab order, opens on
              Enter / Space and announces its state (#1307). It stops the
              click so the row's own onClick does not toggle a second time. */}
          <button
            aria-controls={expanded ? panelId : undefined}
            aria-expanded={expanded}
            className={`flex items-center gap-3 text-left rounded-lg ${FOCUS_RING}`}
            type="button"
            onClick={(event) => {
              event.stopPropagation();
              onToggle();
            }}
          >
            <span className="w-8 h-8 shrink-0 rounded-full bg-[linear-gradient(135deg,#c4b5fd,#f0abfc_50%,#fdba8c)] flex items-center justify-center text-[#0b0b14] text-sm font-semibold">
              {row.email[0].toUpperCase()}
            </span>
            <span className="text-[var(--dusk-ink)] [overflow-wrap:anywhere]">{row.email}</span>
          </button>
        </td>
        <td className="px-6 py-4">
          <code className={`font-mono text-[var(--dusk-mute)] text-xs ${CODE_BOX} px-2 py-1 rounded-lg whitespace-nowrap`}>
            {truncateKey(row.licenseKey)}
          </code>
        </td>
        <td className="px-6 py-4">
          <div className="flex items-center gap-2">
            <span className={`${CHIP} ${CHIP_LAV} tabular-nums`}>
              {formatCount(row.deviceCount)}
            </span>
            <svg
              className={`w-4 h-4 text-[var(--dusk-dim)] transition-transform duration-200 ease-[cubic-bezier(.2,0,0,1)] motion-reduce:transition-none ${
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
          {/* The panel still mounts on open (its query only runs while it
              is open), and .t-cell-reveal grows its one grid row from 0fr
              (#702), as the #914 inline editor does: a table row cannot tween
              its own height. The padding moved inside the clip, or it would
              arrive in one frame. */}
          <td colSpan={3} className="p-0 bg-[rgba(5,6,13,.35)]">
            <div className="t-cell-reveal">
              <div>
                <div className="px-6 py-4">
                  <div className="mb-3">
                    <input
                      aria-label="Search this key's devices by name or id"
                      className={`w-full max-w-sm px-3.5 py-2 text-sm ${INPUT}`}
                      placeholder="Search devices by name or id..."
                      type="search"
                      value={search}
                      onChange={(e) => setSearch(e.target.value)}
                    />
                  </div>
                  {/* A search swaps this list for one of another length, so
                      it tweens its height too (#2757). It keys on the first
                      page, not `data`: "Show more" appends a page, and
                      structural sharing keeps the first page the same object,
                      so an append grows the list at once instead of pinning
                      it. A new search is a new query, so a new first page. It
                      sits inside the .t-cell-reveal clip, which only animates
                      the open; the first page after the open lands at once. */}
                  <HeightTween trigger={firstPage}>
                    {isLoading ? (
                      <div className="space-y-2">
                        {Array.from({ length: 2 }).map((_, i) => (
                          <div
                            key={i}
                            className={`h-4 w-64 ${SKEL}`}
                          />
                        ))}
                      </div>
                    ) : loadedDevices.length === 0 ? (
                      <p className="text-[var(--dusk-mute)] text-sm">
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
                            className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2.5 px-3.5 rounded-xl border border-[var(--dusk-line)] bg-white/[.03]"
                          >
                            <div className="flex items-center gap-3">
                              <svg
                                className="w-4 h-4 shrink-0 text-[var(--dusk-dim)]"
                                fill="none"
                                stroke="currentColor"
                                viewBox="0 0 24 24"
                              >
                                <path
                                  strokeLinecap="round"
                                  strokeLinejoin="round"
                                  strokeWidth={1.5}
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
                                  <bdi className="text-[var(--dusk-dim)] text-xs font-mono">
                                    {device.deviceId}
                                  </bdi>
                                )}
                              </div>
                            </div>
                            <div className="text-right text-xs text-[var(--dusk-mute)] tabular-nums">
                              <div>First seen: {formatDate(device.createdAt, "en-US")}</div>
                              <div>
                                Last active: {formatDateTime(device.lastValidatedAt, "en-US")}
                              </div>
                            </div>
                          </div>
                        ))}
                        <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
                          <p className="text-[var(--dusk-mute)] text-xs tabular-nums">
                            Showing {formatCount(loadedDevices.length)} of{" "}
                            {formatCount(total)} device{total !== 1 ? "s" : ""}
                          </p>
                          {hasNextPage && (
                            <button
                              className={BTN_SECONDARY_XS}
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
                  </HeightTween>
                </div>
              </div>
            </div>
          </td>
        </tr>
      )}
    </>
  );
}
