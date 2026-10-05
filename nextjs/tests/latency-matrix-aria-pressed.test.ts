/**
 * #1131 sibling: on `/[locale]/latency` the "Metric" and "Clip length" buttons
 * are single-choice toggles drawn the same way as the choosing-a-model ones,
 * with the chosen value shown by colour only. Each button now carries
 * `aria-pressed`. This file renders the REAL component with empty matrices and
 * reads the first-load state from server markup.
 *
 * #1303 adds: each of the 2 button rows is a `role="group"` whose
 * `aria-labelledby` points at its visible label, so a pressed button has a
 * group name.
 *
 * WHAT IT DOES NOT PROVE: the home-region picker (it only renders with data
 * and after a click), or what a screen reader announces.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import {
  BUCKET_LABELS,
  DURATION_BUCKETS,
  type DurationBucket,
  type LatencyMatrixData,
} from "@/lib/latency/types";

const EMPTY: LatencyMatrixData = {
  vendors: [],
  regions: [],
  totalSamples: 0,
  windowDays: 0,
};

function render(defaultBucket: DurationBucket): string {
  const matrices = Object.fromEntries(
    DURATION_BUCKETS.map((bucket) => [bucket, EMPTY]),
  ) as Record<DurationBucket, LatencyMatrixData>;

  return renderToStaticMarkup(
    createElement(LatencyMatrix, { defaultBucket, matrices }),
  );
}

/** The `aria-pressed` value on the `<button>` whose text is exactly `label`. */
function pressedOf(html: string, label: string): string | null {
  const match = Array.from(
    html.matchAll(/<button\b([^>]*)>([^<]*)<\/button>/g),
  ).find((m) => m[2] === label);

  assert.ok(match, `no <button> reads "${label}"`);

  return /aria-pressed="([^"]+)"/.exec(match[1])?.[1] ?? null;
}

test("on first load only the Median metric is pressed", () => {
  const html = render(DURATION_BUCKETS[0]);

  assert.equal(pressedOf(html, "Median"), "true");
  for (const label of ["p95", "p99", "Error rate"]) {
    assert.equal(pressedOf(html, label), "false", label);
  }
});

test("on first load only the default clip length is pressed", () => {
  const chosen = DURATION_BUCKETS[DURATION_BUCKETS.length - 1];
  const html = render(chosen);

  for (const bucket of DURATION_BUCKETS) {
    assert.equal(
      pressedOf(html, BUCKET_LABELS[bucket]),
      bucket === chosen ? "true" : "false",
      bucket,
    );
  }
});

/**
 * #1303: each toggle row is a `role="group"` named by its visible label, so a
 * pressed button has a group name ("Metric", "Clip length"). Reads the row
 * `<div>` whose `aria-labelledby` points at the label `<span>`, and checks the
 * row holds exactly the buttons of that control.
 */
function groupNamed(html: string, name: string): string {
  const label = Array.from(
    html.matchAll(/<span\b([^>]*)>([^<]*)<\/span>/g),
  ).find((m) => m[2] === name);

  assert.ok(label, `no <span> reads "${name}"`);
  const id = /\bid="([^"]+)"/.exec(label[1])?.[1];

  assert.ok(id, `the "${name}" label has no id`);

  const open = Array.from(html.matchAll(/<div\b([^>]*)>/g)).find((m) =>
    m[1].includes(`aria-labelledby="${id}"`),
  );

  assert.ok(open, `no element is labelled by the "${name}" label`);
  assert.match(open[1], /\brole="group"/, `the "${name}" row is not a group`);

  const start = (open.index ?? 0) + open[0].length;

  return html.slice(start, html.indexOf("</div>", start));
}

function buttonLabels(fragment: string): string[] {
  return Array.from(fragment.matchAll(/<button\b[^>]*>([^<]*)<\/button>/g)).map(
    (m) => m[1],
  );
}

test("the Metric row is a group named by its label, holding the metric buttons", () => {
  const row = groupNamed(render(DURATION_BUCKETS[0]), "Metric");

  assert.deepEqual(buttonLabels(row), ["Median", "p95", "p99", "Error rate"]);
  assert.equal((row.match(/aria-pressed="true"/g) ?? []).length, 1);
});

test("the Clip length row is a group named by its label, holding the bucket buttons", () => {
  const row = groupNamed(render(DURATION_BUCKETS[0]), "Clip length");

  assert.deepEqual(
    buttonLabels(row),
    DURATION_BUCKETS.map((bucket) => BUCKET_LABELS[bucket]),
  );
  assert.equal((row.match(/aria-pressed="true"/g) ?? []).length, 1);
});
