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

const COLUMNS = ["Email", "Account Key", "Devices"] as const;

/**
 * The table's header row. The first-load skeleton draws its own copy, laid
 * out over the skeleton's columns (#1242).
 */
function DevicesTableHead({ className }: { className?: string }) {
  return (
    <thead className={className}>
      <tr className="border-b border-[var(--dusk-line)] bg-white/[.02]">
        {COLUMNS.map((column) => (
          <th key={column} className={TH}>
            {column}
          </th>
        ))}
      </tr>
    </thead>
  );
}

const TIME_RANGES = [
  { label: "7 days", value: 7 },
  { label: "30 days", value: 30 },
  { label: "90 days", value: 90 },
] as const;

/**
 * Account Keys fetched per "Show more" in the main table (#1948). The table
 * rendered every key with devices at once; at 2,000 keys that is a page no
 * one scrolls. Matches the db-layer's ACCOUNT_KEYS_PAGE_SIZE.
 */
export const ACCOUNT_KEY_PAGE_SIZE = 50;

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
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search.trim()), 250);

    return () => clearTimeout(timer);
  }, [search]);

  // One page of ACCOUNT_KEY_PAGE_SIZE keys at a time (#1948), as an expanded
  // key pages its devices (#1661). The search box finds a key paging would
  // hide. A new range or search is a new query key, so paging starts again
  // from the top.
  const {
    data,
    isLoading: loading,
    isFetching,
    isFetchingNextPage,
    isPlaceholderData,
    hasNextPage,
    fetchNextPage,
    error: queryError,
    refetch,
  } = api.admin.devices.list.useInfiniteQuery(
    {
      days,
      search: debouncedSearch || undefined,
      limit: ACCOUNT_KEY_PAGE_SIZE,
    },
    {
      getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
      // Keep the previous range's rows on screen while a new range loads,
      // instead of tearing the table down to the skeleton on every click
      // (#863), as the Customers page does. A new search does the same.
      placeholderData: keepPreviousData,
    },
  );

  // A range switch shows the previous range's rows until the new ones land,
  // so the caption, the empty-state copy and an expanded row follow the rows
  // on screen, not the button just pressed. Adjusted during render, so the
  // render that brings the new rows already carries their range. A failed
  // fetch is not placeholder data either, so the copy names the range asked
  // for, as before.
  const [shownDays, setShownDays] = useState(days);
  if (!isPlaceholderData && shownDays !== days) setShownDays(days);
  // The same for the search the rows on screen answer (#1948), so the empty
  // copy names a search only once its own (empty) result has landed.
  const [shownSearch, setShownSearch] = useState(debouncedSearch);

  if (!isPlaceholderData && shownSearch !== debouncedSearch) {
    setShownSearch(debouncedSearch);
  }

  // In flight with rows already on screen: a range switch, a search or a
  // Refresh. A "Show more" is not: it keeps the rows it appends to at full
  // opacity, as an expanded key's does (#1948).
  const isTransitioning = isFetching && !loading && !isFetchingNextPage;

  // The first load's rows paint at opacity 0, then fade in.
  const revealed = useRevealAfterLoad(loading);

  // #1242: the skeleton layer is drawn from the first load on; a mount with
  // the rows already cached shows none, as before. isLoading is true whenever
  // a fetch starts with no rows on screen: the first load, and a Refresh
  // after a failed first load (react-query v5 puts a query with no data back
  // to "pending"). When keepPreviousData still has rows to show (a range
  // switch, or a Refresh after a failed one), isLoading stays false and the
  // dimmed rows stay instead, as on main. So any load draws it, set during
  // render so it shows in the same commit, as the skeleton rows did before.
  // Once drawn it stays mounted and fades.
  const [hasSkeleton, setHasSkeleton] = useState(loading);

  if (loading && !hasSkeleton) setHasSkeleton(true);

  // #2789: revealed only turns false in an effect, a render after a load
  // starts, so a Refresh after a failed first load would draw its first frame
  // with the last load's settled layers (skeleton hidden, real header shown)
  // and then fade the skeleton in over 400 ms. While loading, every layer
  // shows its loading state, in the same commit the load starts.
  const shown = revealed && !loading;
  // The fade runs only once the load is over, so entering a load snaps (no
  // skeleton fading in, no real header fading out over the skeleton's), as
  // before #2770. The class lands with the render that ends the load, while
  // the layers still sit at their loading opacity, so the cross-fade two
  // frames later has its start value.
  const fade = loading
    ? ""
    : " transition-[opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none";
  // Paused, not removed: a bar frozen mid-pulse does not jump while it fades.
  const pulse = `${SKEL}${shown ? " [animation-play-state:paused]" : ""}`;

  // Offset paging over a live table: a key that gains a device between two
  // pages moves up, so the next page can repeat a row. Keep the first copy,
  // so React never sees two rows with one key (as DeviceRow does).
  const seenIds = new Set<string>();
  const devices = (data?.pages.flatMap((page) => page.devices) ?? []).filter(
    (row) => !seenIds.has(row.licenseKeyId) && seenIds.add(row.licenseKeyId),
  );
  const total = data?.pages[data.pages.length - 1]?.total ?? 0;
  const nextCount = Math.min(ACCOUNT_KEY_PAGE_SIZE, total - devices.length);
  // The result's identity: the table tweens its height when it changes.
  const firstPage = data?.pages[0];
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
            {/* A "Show more" spins its own button, not this one (#1948). */}
            {isFetching && !isFetchingNextPage ? (
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

      {/* Search (#1948): the table shows one page of keys, so a key on a
          later page is found by its email or Account Key. */}
      <input
        aria-label="Search Account Keys by email or key"
        className={`w-full max-w-sm px-3.5 py-2 text-sm ${INPUT}`}
        placeholder="Search by email or Account Key..."
        type="search"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />

      {/* Devices Table. A range switch or a search that changes the row
          count tweens the card's height (#2757, as #916 did on Customers)
          instead of stepping in one frame; the first load's rows land at
          once. It keys on the first page, not `data`: "Show more" appends a
          page, and structural sharing keeps the first page the same object,
          so an append grows the table at once instead of pinning it (#1948,
          as an expanded key's list does). */}
      <HeightTween
        className={`${CARD} overflow-hidden ${
          isTransitioning ? "opacity-50" : "opacity-100"
        }`}
        // Tailwind's transition-opacity, kept beside the height tween.
        transition="opacity 150ms cubic-bezier(0.4, 0, 0.2, 1)"
        trigger={firstPage}
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
          className="t-skel grid overflow-x-auto overflow-y-hidden rounded-[20px] focus-visible:outline-2 focus-visible:outline-[var(--dusk-lav)] focus-visible:-outline-offset-2"
          role="region"
          // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
          tabIndex={0}
        >
          <table className="[grid-area:1/1] self-start w-full min-w-[36rem]">
            {/* While the skeleton shows, its own header is the visible one,
                laid out over its bars; this one is laid out over the empty
                table's header text alone, so its labels sit elsewhere. It
                cross-fades in with the rows, as the skeleton's header fades
                out, so the labels move only by the rows' own column widths,
                as they did when the skeleton shared this table. */}
            <DevicesTableHead
              className={
                hasSkeleton
                  ? `${
                      shown ? "opacity-100 blur-[0px]" : "opacity-0 blur-[2px]"
                    }${fade}`
                  : undefined
              }
            />
            <tbody
              className={`t-skel-content ${TBODY} ${
                shown ? "opacity-100 blur-[0px]" : "opacity-0 blur-[2px]"
              }${fade}`}
            >
              {loading ? null : devices.length === 0 ? (
                <tr>
                  <td
                    colSpan={3}
                    className="px-6 py-14 text-center text-[var(--dusk-mute)]"
                  >
                    {shownSearch
                      ? "No Account Keys match that search."
                      : `No device activations found in the last ${shownDays} days.`}
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
          {/* While loading: the skeleton rows, in the same grid slot as the
              table, under its own copy of the header (#1242, the #1236
              cross-fade on /older-versions). A <tbody> cannot share a slot,
              so the skeleton is its own table. It stays mounted, as #1236's
              does: once the rows land it fades out on the same signal that
              fades them in (at once under reduced motion), its pulse pauses
              where it is, and it stops taking height (h-0), so the table
              alone sets the box's height and a short result never waits on
              the fade. Nor does it set the column's width (min-w-0, its
              overflow clipped sideways), so the settled table is as wide as
              before; still in the column, it fades over the table at the
              table's width. overflow-y-hidden clips what hangs below a short
              table. A range switch keeps the previous rows mounted (above). */}
          {hasSkeleton && (
            <div
              aria-hidden
              className={`t-skel-skeleton [grid-area:1/1] self-start pointer-events-none ${
                loading ? "" : "h-0 min-w-0 overflow-x-clip"
              } ${shown ? "opacity-0 blur-[2px]" : "opacity-100 blur-[0px]"}${fade}`}
            >
              <table className="w-full min-w-[36rem]">
                <DevicesTableHead />
                <tbody className={TBODY}>
                  {Array.from({ length: 5 }).map((_, i) => (
                    <tr key={i}>
                      <td className="px-6 py-4">
                        <div
                          className={`h-4 w-48 ${pulse}`}
                        />
                      </td>
                      <td className="px-6 py-4">
                        <div
                          className={`h-4 w-28 ${pulse}`}
                        />
                      </td>
                      <td className="px-6 py-4">
                        <div
                          className={`h-4 w-12 ${pulse}`}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </HeightTween>

      {/* Row Count, and the next page (#1948) */}
      {!loading && devices.length > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-[var(--dusk-mute)] text-sm tabular-nums">
            Showing {formatCount(devices.length)} of {formatCount(total)}{" "}
            Account Key{total !== 1 ? "s" : ""}
          </p>
          {hasNextPage && (
            <button
              className={BTN_SECONDARY_XS}
              disabled={isFetchingNextPage}
              type="button"
              onClick={() => fetchNextPage()}
            >
              {isFetchingNextPage ? (
                <>
                  <Spinner className="w-3.5 h-3.5" />
                  Loading...
                </>
              ) : (
                `Show ${formatCount(nextCount)} more`
              )}
            </button>
          )}
        </div>
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
