"use client";

import {
  useEffect,
  useLayoutEffect,
  useRef,
  type CSSProperties,
  type ReactNode,
} from "react";

// The height tween: 300 ms on --motion-ease-smooth-out, the `01-card-resize`
// timing #916 asked for.
export const HEIGHT_TWEEN_MS = 300;
export const HEIGHT_TRANSITION = `height ${HEIGHT_TWEEN_MS}ms var(--motion-ease-smooth-out)`;

/** The tween in flight: `stop` ends it, `retarget` moves its end height. */
export type HeightTweenHandle = { stop: () => void; retarget: () => void };

/** What a HeightTween box remembers between commits. */
export type HeightTweenState = {
  /** The box's height in the last painted frame, while no tween runs. */
  settledHeight: number | null;
  tween: HeightTweenHandle | null;
};

/**
 * The layout effect that runs when `trigger` changes, split out of the
 * component (#2757) so a node test can drive it with stand-in elements.
 * `previous` is the trigger value before this commit.
 */
export function tweenOnTriggerChange(
  state: HeightTweenState,
  element: HTMLElement,
  inner: HTMLElement,
  previous: unknown,
): void {
  // The first value after `undefined` (the first load's rows replacing the
  // skeleton) lands at once.
  if (previous === undefined) return;

  let from = state.settledHeight;

  if (state.tween) {
    // The computed height mid-transition is where the box is drawn now
    // (offsetHeight would read the pinned target).
    from = parseFloat(getComputedStyle(element).height);
    state.tween.stop();
  }

  // The two-frame start below would hold the old height for those frames,
  // and the global guard would then end the transition at once: a late step
  // instead of an on-time one.
  if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

  const to = element.offsetHeight;

  if (from === null || Math.abs(from - to) < 1) return;

  // Pin the box where it was drawn; the new content must not spill out of
  // it while it is still shorter.
  element.style.height = `${from}px`;
  element.style.overflow = "clip";

  const target = () =>
    inner.offsetHeight + element.offsetHeight - element.clientHeight;
  let started = false;
  let timer = 0;
  let frame = 0;

  const unpin = () => {
    window.cancelAnimationFrame(frame);
    window.clearTimeout(timer);
    element.removeEventListener("transitionend", onEnd);
    element.style.height = "";
    element.style.overflow = "";
    state.tween = null;
    state.settledHeight = element.offsetHeight;
  };
  // Children's transitions bubble here too; only the box's own counts.
  const onEnd = (event: TransitionEvent) => {
    if (event.target === element && event.propertyName === "height") unpin();
  };
  // A transition that never runs fires no transitionend.
  const bound = () => {
    window.clearTimeout(timer);
    timer = window.setTimeout(unpin, HEIGHT_TWEEN_MS + 400);
  };

  // The tween starts two frames later, after the frame that draws the new
  // content: a 100-row page takes one frame of ~250 ms to lay out and
  // paint, and a transition started inside it is ~99% done by the first
  // frame anyone sees (verify on #916). The first callback runs before that
  // frame is drawn, the second in the frame after it.
  frame = window.requestAnimationFrame(() => {
    frame = window.requestAnimationFrame(() => {
      started = true;
      element.style.height = `${target()}px`;
      bound();
    });
  });

  element.addEventListener("transitionend", onEnd);
  state.tween = {
    stop: unpin,
    retarget: () => {
      // Before the start the box stays pinned: the content is still being
      // drawn, and the start reads its height then.
      if (!started) return;
      const height = target();

      // Its own frames report too; a pixel of rounding is not a change.
      if (Math.abs(parseFloat(element.style.height) - height) < 2) return;
      element.style.height = `${height}px`;
      bound();
    },
  };
}

/**
 * #916: a box whose content changes height in one commit (a search narrows
 * 100 rows to 2, or clearing it brings them back) tweens to the new height
 * instead of stepping. A ResizeObserver keeps the box's last settled height;
 * when `trigger` changes, the layout effect pins the box at that height, then
 * sets the new one, and the height transition tweens between the two. The pin
 * comes off when the tween ends, so at rest the box is `height: auto` and an
 * inline editor or an error reveal inside it sizes it at once, as before. The
 * same pin-then-release as SignInClient (#752) and the /download panel (#767).
 * Under reduced motion nothing is pinned: the box takes its new height in the
 * same frame as the new content, as before.
 *
 * Only a change from one value of `trigger` to another tweens: the first
 * value after `undefined` (the first load's rows replacing the skeleton)
 * lands at once, as before.
 *
 * `transition` is any other transition the box keeps (the table's in-flight
 * dim); it is listed beside height, never as `all` (#668).
 *
 * Shared by the admin Customers (#916) and Devices (#2757) pages.
 */
export function HeightTween({
  trigger,
  className,
  transition,
  children,
}: {
  trigger: unknown;
  className?: string;
  transition?: string;
  children: ReactNode;
}) {
  const box = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const state = useRef<HeightTweenState>({ settledHeight: null, tween: null });
  const previousTrigger = useRef(trigger);

  useEffect(() => {
    const element = box.current;
    const inner = content.current;
    const current = state.current;

    if (!element || !inner) return;

    // Reports after layout and before paint, so it always holds the height
    // the last painted frame showed. Mid-tween the box is pinned, so a change
    // of the content's own size (an editor opening) moves the pin instead:
    // nothing is clipped, and nothing jumps when the pin comes off.
    const observer = new ResizeObserver(() => {
      if (current.tween) current.tween.retarget();
      else current.settledHeight = element.offsetHeight;
    });

    observer.observe(element);
    observer.observe(inner);

    return () => {
      observer.disconnect();
      current.tween?.stop();
    };
  }, []);

  useLayoutEffect(() => {
    const element = box.current;
    const inner = content.current;
    const previous = previousTrigger.current;

    previousTrigger.current = trigger;
    if (!element || !inner) return;
    tweenOnTriggerChange(state.current, element, inner, previous);
  }, [trigger]);

  const style: CSSProperties = {
    transition: transition
      ? `${transition}, ${HEIGHT_TRANSITION}`
      : HEIGHT_TRANSITION,
  };

  // flow-root: the content's own margins count in its measured height.
  return (
    <div ref={box} className={className} style={style}>
      <div ref={content} className="flow-root">
        {children}
      </div>
    </div>
  );
}
