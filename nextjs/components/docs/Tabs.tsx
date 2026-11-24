"use client";

import {
  Children,
  isValidElement,
  useId,
  useState,
  type KeyboardEvent,
  type ReactElement,
  type ReactNode,
} from "react";

// Mintlify's <Tabs>/<Tab title="…">. Every panel is in the server HTML (only
// the inactive ones carry `hidden`), so crawlers and no-JS readers get all of
// it. Keyboard: the WAI-ARIA tabs pattern (arrows, Home, End).

type TabProps = { title: string; children?: ReactNode };

export function Tab({ children }: TabProps) {
  return <>{children}</>;
}

export function Tabs({ children }: { children?: ReactNode }) {
  const tabs = Children.toArray(children).filter(
    (child): child is ReactElement<TabProps> =>
      isValidElement<TabProps>(child) && typeof child.props.title === "string",
  );
  const [active, setActive] = useState(0);
  const id = useId();

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    const last = tabs.length - 1;
    const next =
      event.key === "ArrowRight"
        ? active === last
          ? 0
          : active + 1
        : event.key === "ArrowLeft"
          ? active === 0
            ? last
            : active - 1
          : event.key === "Home"
            ? 0
            : event.key === "End"
              ? last
              : null;

    if (next === null) return;
    event.preventDefault();
    setActive(next);
    document.getElementById(`${id}-tab-${next}`)?.focus();
  };

  return (
    <div className="docs-tabs">
      <div aria-label="Tabs" className="docs-tabs-list" role="tablist">
        {tabs.map((tab, i) => (
          <button
            key={tab.props.title}
            aria-controls={`${id}-panel-${i}`}
            aria-selected={i === active}
            className="docs-tab"
            id={`${id}-tab-${i}`}
            role="tab"
            tabIndex={i === active ? 0 : -1}
            type="button"
            onClick={() => setActive(i)}
            onKeyDown={onKeyDown}
          >
            {tab.props.title}
          </button>
        ))}
      </div>
      {tabs.map((tab, i) => (
        <div
          key={tab.props.title}
          aria-labelledby={`${id}-tab-${i}`}
          className="docs-tab-panel"
          hidden={i !== active}
          id={`${id}-panel-${i}`}
          role="tabpanel"
        >
          {tab.props.children}
        </div>
      ))}
    </div>
  );
}
