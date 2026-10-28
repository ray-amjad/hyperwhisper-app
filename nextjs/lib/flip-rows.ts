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

/** The alpha of a computed colour: `rgba(…, a)`, `oklch(… / a)`, `color(… / a)`. */
function colorAlpha(color: string): number {
  const match =
    /\/\s*([\d.]+)(%?)\s*\)$/.exec(color) ??
    /^rgba\([^,]+,[^,]+,[^,]+,\s*([\d.]+)(%?)\s*\)$/.exec(color);

  if (match === null) return color === "transparent" ? 0 : 1;
  const alpha = Number(match[1]);

  return match[2] === "%" ? alpha / 100 : alpha;
}

/**
 * The colours drawn behind `el`'s rows, nearest first, down to the first
 * opaque one: every see-through background from `el` up to an opaque
 * ancestor. Empty when nothing above `el` is opaque.
 */
function backdropColors(el: HTMLElement): string[] {
  const colors: string[] = [];

  for (let node: Element | null = el; node !== null; node = node.parentElement) {
    const color = window.getComputedStyle(node).backgroundColor;
    const alpha = colorAlpha(color);

    if (alpha > 0) colors.push(color);
    if (alpha >= 1) return colors;
  }

  return [];
}

/**
 * The keyframe fields that make a sliding row opaque (#2458). Two rows that
 * cross mid-slide sit at one y for a frame or two; with see-through cells the
 * numbers of both print over each other. The row is painted with its own
 * colour over every colour behind the table, so the one drawn on top hides
 * the other and the reader sees the same colours as when it is still. Empty
 * when no opaque colour is behind the table.
 */
function opaqueFill(row: HTMLElement, backdrop: string[]): Keyframe {
  if (backdrop.length === 0) return {};
  const own = window.getComputedStyle(row).backgroundColor;
  const layers = [...(colorAlpha(own) > 0 ? [own] : []), ...backdrop];
  const base = layers[layers.length - 1];
  const tints = layers.slice(0, -1);

  return tints.length === 0
    ? { backgroundColor: base }
    : {
        backgroundColor: base,
        backgroundImage: tints
          .map((color) => `linear-gradient(${color}, ${color})`)
          .join(", "),
      };
}

/**
 * Where a sliding row sits in the paint order while it slides (#2458): a row
 * moving up, the one the reader most likely clicked to follow, over a row
 * moving down, and both over a far row, which fades in and so is never fully
 * opaque. Still rows keep their place under all three. `position` is only
 * there so `zIndex` applies; the keyframes set both for the slide alone.
 */
export const FLIP_LAYER = { far: 1, down: 2, up: 3 } as const;

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
 * A sliding row is opaque for the slide (`opaqueFill`) and layered by
 * `FLIP_LAYER`, so where two rows cross one is drawn whole on top.
 *
 * The slides in flight are cancelled together first, then one read pass, then
 * writes that dirty nothing: the cancels cost one style flush at the first
 * read, not one per row, and a Web Animation needs no inline style and no
 * reflow to start.
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
  // A slide in flight paints the row with its slide fill, so its own colour
  // reads true only once the slide is gone. A row in flight is slid again
  // below from where it is drawn; one already within 1px of its new top, or
  // whose whole path is off screen, lands there with no visible jump.
  slides.forEach((slide) => slide.cancel());
  slides.clear();
  const base = tbody.offsetTop;
  const viewTop = tbody.getBoundingClientRect().top;
  const viewHeight = window.innerHeight;
  const moved: {
    id: string;
    row: HTMLElement;
    delta: number;
    top: number;
    height: number;
    fill: Keyframe;
  }[] = [];
  const backdrop = backdropColors(tbody);

  rows.forEach((row, id) => {
    const old = before.get(id);

    if (old === undefined) return;
    const top = row.offsetTop - base;
    const delta = old - top;

    if (Math.abs(delta) >= 1) {
      moved.push({
        id,
        row,
        delta,
        top,
        height: row.offsetHeight,
        fill: opaqueFill(row, backdrop),
      });
    }
  });
  for (const { id, row, delta, top, height, fill } of moved) {
    const far = Math.abs(delta) > FLIP_MAX_DISTANCE;
    const shown = far ? Math.sign(delta) * FLIP_MAX_DISTANCE : delta;
    // delta > 0: the row was further down, so it is moving up.
    const layer: Keyframe = {
      ...fill,
      position: "relative",
      zIndex: far ? FLIP_LAYER.far : delta > 0 ? FLIP_LAYER.up : FLIP_LAYER.down,
    };
    // A row whose whole slide is off screen is not animated: nobody sees it,
    // and each running slide costs a style pass every frame.
    const from = viewTop + top + Math.min(0, shown);
    const to = viewTop + top + Math.max(0, shown) + height;

    if (to <= 0 || from >= viewHeight) {
      continue;
    }
    slides.set(
      id,
      row.animate(
        far
          ? [
              { transform: `translateY(${shown}px)`, opacity: 0, ...layer },
              { transform: "none", opacity: 1, ...layer },
            ]
          : [
              { transform: `translateY(${shown}px)`, ...layer },
              { transform: "none", ...layer },
            ],
        { duration: FLIP_DURATION_MS, easing: FLIP_EASING },
      ),
    );
  }
}
