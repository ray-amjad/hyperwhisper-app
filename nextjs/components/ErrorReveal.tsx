import type { ReactNode } from "react";

/**
 * #749, shared since #2286: an error reveals instead of mounting at full size
 * in one frame. The wrapper is always in the DOM (`.t-error-reveal` in
 * globals.css) and opens while it has content; the `role="alert"` element
 * inside it still mounts with its message, so the screen-reader path is
 * unchanged. A region that must stay mounted (an `aria-live` status) passes
 * `open` instead.
 *
 * `gap` is the space the error had above or below it: it sits inside the clip
 * as padding, so it grows with the tween. `className` is for the wrapper and
 * defaults to `mb-0`, which drops a parent `space-y-*` margin (Tailwind 4 puts
 * it on the earlier sibling's margin-block-end) so a closed wrapper takes no
 * space.
 */
export function ErrorReveal({
  open,
  gap,
  className = "mb-0",
  children,
}: {
  open?: boolean;
  gap?: string;
  className?: string;
  children: ReactNode;
}) {
  const isOpen = open ?? Boolean(children);

  return (
    <div
      className={className ? `t-error-reveal ${className}` : "t-error-reveal"}
      data-open={isOpen ? "" : undefined}
    >
      <div>
        <div className={gap}>{children}</div>
      </div>
    </div>
  );
}

/**
 * #749: shake the field a failed submit is about. Removing the class and
 * forcing a reflow before adding it back is what replays the shake on a
 * second failure; the class goes when the animation ends. The element needs
 * `.t-input` (globals.css), which also stops the shake under reduced motion.
 */
export function shake(element: HTMLElement | null) {
  if (!element) return;
  element.classList.remove("is-shaking");
  void element.offsetWidth;
  element.classList.add("is-shaking");
  element.addEventListener(
    "animationend",
    () => element.classList.remove("is-shaking"),
    { once: true },
  );
}
