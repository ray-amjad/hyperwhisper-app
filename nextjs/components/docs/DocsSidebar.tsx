"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight } from "lucide-react";

import {
  DOCS_NAV,
  type DocNode,
  findDocByPath,
  normalizeDocsPath,
} from "@/lib/docs/nav";

// The docs sidebar, from lib/docs/nav.ts. Rendered twice: a sticky column
// from lg up, and a closed disclosure above the article below that (CSS
// shows one). A nested group (Audio Input, Local API Reference …) is a
// <details>, open when it holds the current page, as Mintlify's were.

function containsHref(node: DocNode, href: string): boolean {
  if (node.kind === "group")
    return node.items.some((n) => containsHref(n, href));

  return node.href === href;
}

function Items({ nodes, current }: { nodes: DocNode[]; current: string }) {
  return (
    <ul>
      {nodes.map((node) => {
        if (node.kind === "group") {
          return (
            <li key={node.title}>
              <details
                className="docs-nav-sub"
                open={containsHref(node, current) || undefined}
              >
                <summary>
                  <ChevronRight
                    aria-hidden="true"
                    className="docs-nav-chevron"
                  />
                  {node.title}
                </summary>
                <Items current={current} nodes={node.items} />
              </details>
            </li>
          );
        }
        const active = node.href === current;

        return (
          <li key={node.href}>
            <Link
              aria-current={active ? "page" : undefined}
              className={active ? "docs-nav-link active" : "docs-nav-link"}
              href={node.href}
            >
              {node.kind === "endpoint" ? (
                <span className="docs-method" data-method={node.method}>
                  {node.method === "DELETE" ? "DEL" : node.method}
                </span>
              ) : null}
              <span>{node.title}</span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

function NavList({ current }: { current: string }) {
  return (
    <nav aria-label="Docs">
      {DOCS_NAV.map((group) => (
        <div key={group.title} className="docs-nav-group">
          <p className="docs-nav-heading">{group.title}</p>
          <Items current={current} nodes={group.items} />
        </div>
      ))}
    </nav>
  );
}

export function DocsSidebar() {
  const current = normalizeDocsPath(usePathname() ?? "/docs");
  const title = findDocByPath(current)?.title ?? "Contents";

  return (
    <>
      <aside className="docs-side">
        <NavList current={current} />
      </aside>
      {/* key: a navigation closes the menu, so the next page is not hidden under it. */}
      <details key={current} className="docs-side-mobile">
        <summary>
          <span className="docs-side-label">Docs</span>
          <span className="docs-side-current">{title}</span>
          <ChevronRight aria-hidden="true" className="docs-nav-chevron" />
        </summary>
        <NavList current={current} />
      </details>
    </>
  );
}
