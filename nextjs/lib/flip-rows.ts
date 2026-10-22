/**
 * A FLIP for a re-ranked table (#766, #812). No transition covers a list whose
 * rows change place, so each row is drawn back at the place it held before the
 * re-order, then let go so it slides to its new one.
 *
 * Two halves: `snapshotRowTops` runs in the event handler, before the state
 * that re-orders the rows changes; `slideRows` runs in a layout effect on the
 * new order, after the DOM commit and before paint, so no frame shows the jump.
 */

/**
 * How far a re-ranked row may visibly travel. A row that moves further starts
 * this far from its new place and fades in, instead of flying across the
 * whole table.
 */
export const FLIP_MAX_DISTANCE = 400;

/**
 * The slide's timing: --motion-duration-fast and --motion-ease-smooth-out in
 * globals.css (a test pins the two together). The rows run on the Web
 * Animations API rather than an inline transition, so a re-rank writes no
 * inline style and needs no forced reflow to set a start state.
 */
export const FLIP_DURATION_MS = 250;
export const FLIP_EASING = "cubic-bezier(0.22, 1, 0.36, 1)";

/**
 * Read at event time, never during render, so the server markup and the first
 * client render stay identical (no hydration mismatch).
 */
export function prefersReducedMotion(): boolean {
  return (
    typeof window !== "undefined" &&
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches
  );
}

/**
 * Each row's top relative to `tbody`, keyed by row id. The visual top, a slide
 * in flight included, so a re-rank that lands before the last slide ends
 * carries on from where the row is drawn.
 */
export function snapshotRowTops(
  tbody: HTMLElement,
  rows: Map<string, HTMLElement>,
): Map<string, number> {
  const base = tbody.getBoundingClientRect().top;
  const rowTops = new Map<string, number>();

  rows.forEach((row, id) => {
    rowTops.set(id, row.getBoundingClientRect().top - base);
  });

  return rowTops;
}

/**
 * Slide every row in `rows` that `before` also holds from its old top to its
 * new one. A row `before` does not hold just appears. `slides` keeps each
 * row's running animation, so a new re-rank cancels the old slide first.
 *
 * One read pass, then writes that dirty nothing: offsetTop ignores the
 * transform a slide in flight draws, so no slide has to be cancelled before
 * the read, and a Web Animation needs no inline style and no reflow to start.
 * Writing inline styles row by row and reading the next row forced a style +
 * layout flush per row, about 50 per slider input, which made a drag stutter.
 */
export function slideRows(
  tbody: HTMLElement,
  rows: Map<string, HTMLElement>,
  before: Map<string, number>,
  slides: Map<string, Animation>,
): void {
  if (typeof tbody.animate !== "function") return;
  const base = tbody.offsetTop;
  const viewTop = tbody.getBoundingClientRect().top;
  const viewHeight = window.innerHeight;
  const moved: {
    id: string;
    row: HTMLElement;
    delta: number;
    top: number;
    height: number;
  }[] = [];

  rows.forEach((row, id) => {
    const old = before.get(id);

    if (old === undefined) return;
    const top = row.offsetTop - base;
    const delta = old - top;

    if (Math.abs(delta) >= 1) {
      moved.push({ id, row, delta, top, height: row.offsetHeight });
    }
  });
  for (const { id, row, delta, top, height } of moved) {
    const far = Math.abs(delta) > FLIP_MAX_DISTANCE;
    const shown = far ? Math.sign(delta) * FLIP_MAX_DISTANCE : delta;
    // A row whose whole slide is off screen is not animated: nobody sees it,
    // and each running slide costs a style pass every frame.
    const from = viewTop + top + Math.min(0, shown);
    const to = viewTop + top + Math.max(0, shown) + height;

    slides.get(id)?.cancel();
    if (to <= 0 || from >= viewHeight) {
      slides.delete(id);
      continue;
    }
    slides.set(
      id,
      row.animate(
        far
          ? [
              { transform: `translateY(${shown}px)`, opacity: 0 },
              { transform: "none", opacity: 1 },
            ]
          : [{ transform: `translateY(${shown}px)` }, { transform: "none" }],
        { duration: FLIP_DURATION_MS, easing: FLIP_EASING },
      ),
    );
  }
}
