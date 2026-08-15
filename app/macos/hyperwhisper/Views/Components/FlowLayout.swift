//
//  FlowLayout.swift
//  hyperwhisper
//
//  A left-aligned wrapping row: subviews are placed side by side until the next
//  one no longer fits the available width, then the layout starts a new line.
//
//  Use it instead of an HStack for badge/pill strips. An HStack keeps every
//  badge on one line and squeezes them, so a long label wraps *inside* its own
//  pill. Here each badge keeps its natural one-line width and the strip itself
//  grows to a second line.
//

import SwiftUI

struct FlowLayout: Layout {

    /// Horizontal gap between two subviews on the same line.
    var spacing: CGFloat = 8
    /// Vertical gap between two lines.
    var lineSpacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) -> CGSize {
        let lines = layoutLines(maxWidth: proposal.width ?? .infinity, subviews: subviews)
        let width = lines.map(\.width).max() ?? 0
        let height = lines.map(\.height).reduce(0, +) + lineSpacing * CGFloat(max(lines.count - 1, 0))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) {
        let lines = layoutLines(maxWidth: bounds.width, subviews: subviews)
        var y = bounds.minY

        for line in lines {
            var x = bounds.minX
            for item in line.items {
                // Place each subview at its own measured size so its text stays
                // on one line.
                subviews[item.index].place(
                    at: CGPoint(x: x, y: y + (line.height - item.size.height) / 2),
                    proposal: ProposedViewSize(item.size)
                )
                x += item.size.width + spacing
            }
            y += line.height + lineSpacing
        }
    }

    // MARK: - Line breaking

    private struct Item {
        let index: Int
        let size: CGSize
    }

    private struct Line {
        var items: [Item] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    /// Measures every subview at its natural width and groups the subviews into
    /// lines that fit `maxWidth`. A subview wider than `maxWidth` gets a line of
    /// its own and is measured against `maxWidth`, so it wraps internally rather
    /// than overflow the container.
    private func layoutLines(maxWidth: CGFloat, subviews: Subviews) -> [Line] {
        var lines: [Line] = []
        var current = Line()

        for index in subviews.indices {
            var size = subviews[index].sizeThatFits(ProposedViewSize(width: maxWidth, height: nil))
            size.width = min(size.width, maxWidth)

            let needsNewLine = !current.items.isEmpty && current.width + spacing + size.width > maxWidth
            if needsNewLine {
                lines.append(current)
                current = Line()
            }

            current.width += current.items.isEmpty ? size.width : spacing + size.width
            current.height = max(current.height, size.height)
            current.items.append(Item(index: index, size: size))
        }

        if !current.items.isEmpty {
            lines.append(current)
        }
        return lines
    }
}
