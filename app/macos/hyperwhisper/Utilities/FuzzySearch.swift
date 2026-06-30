//
//  FuzzySearch.swift
//  hyperwhisper
//
//  Centralized helpers for fuzzy text matching and normalization.
//  Current implementation mirrors existing behavior (subsequence match
//  on a normalized string) to avoid behavior changes while enabling
//  future scoring improvements.
//

import Foundation

public enum FuzzySearch {
    // MARK: - Normalization
    public struct Normalized {
        public let text: String
        public let boundaryIndices: Set<Int>
    }

    /// Normalize for filename-only matching (remove spaces, hyphens, underscores; lowercase)
    public static func normalizeForNameWithBoundaries(_ input: String) -> Normalized {
        let lowered = input.lowercased()
        var result = String()
        result.reserveCapacity(lowered.count)
        var boundary = Set<Int>()
        var lastWasSeparator = true // start is boundary
        var normalizedIndex = 0
        for ch in lowered {
            let isSep = ch == " " || ch == "-" || ch == "_"
            if isSep {
                lastWasSeparator = true
                continue
            }
            if lastWasSeparator {
                boundary.insert(normalizedIndex)
            }
            result.append(ch)
            normalizedIndex += 1
            lastWasSeparator = false
        }
        return Normalized(text: result, boundaryIndices: boundary)
    }

    /// Normalize for path-aware matching (keep '/', remove spaces, hyphens, underscores; lowercase)
    public static func normalizeForPathWithBoundaries(_ input: String) -> Normalized {
        let lowered = input.lowercased()
        var result = String()
        result.reserveCapacity(lowered.count)
        var boundary = Set<Int>()
        var lastWasComponentSep = true // start is boundary
        var normalizedIndex = 0
        for ch in lowered {
            if ch == "/" {
                // Keep path separator and mark next as boundary
                result.append(ch)
                normalizedIndex += 1
                lastWasComponentSep = true
                continue
            }
            let isRemovableSep = ch == " " || ch == "-" || ch == "_"
            if isRemovableSep {
                // skip removable separators but do not force boundary unless we see '/'
                continue
            }
            if lastWasComponentSep {
                boundary.insert(normalizedIndex)
            }
            result.append(ch)
            normalizedIndex += 1
            lastWasComponentSep = false
        }
        return Normalized(text: result, boundaryIndices: boundary)
    }

    /// Backwards-compatible simple normalization used by older call sites
    public static func normalize(_ text: String) -> String {
        text
            .lowercased()
            .replacingOccurrences(of: " ", with: "")
            .replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: "_", with: "")
    }

    // MARK: - Matching and Scoring
    /// Return matched indices for subsequence; indices refer to `text` string positions
    public static func matchPositions(query: String, in text: String) -> [Int]? {
        guard !query.isEmpty else { return [] }
        var positions: [Int] = []
        var qi = query.startIndex
        var ti = text.startIndex
        var idx = 0
        while qi < query.endIndex && ti < text.endIndex {
            if query[qi] == text[ti] {
                positions.append(idx)
                qi = query.index(after: qi)
            }
            ti = text.index(after: ti)
            idx += 1
        }
        return qi == query.endIndex ? positions : nil
    }

    /// Simple subsequence check
    public static func isSubsequence(query: String, in text: String) -> Bool {
        matchPositions(query: query, in: text) != nil
    }

    /// Compute a 0..1 score for query vs normalized candidate
    /// Heuristics: early position bonus, contiguity bonus, gap penalty, boundary bonuses
    public static func score(query: String, candidate: Normalized) -> Double {
        guard let pos = matchPositions(query: query, in: candidate.text) else { return 0 }
        let qlen = max(1, query.count)
        let clen = max(1, candidate.text.count)

        // Start position: earlier is better
        let start = pos.first ?? 0
        let startBonus = max(0.0, 1.0 - Double(start) / Double(clen)) // [0..1]

        // Contiguity: reward consecutive runs
        var contiguous = 0
        var gaps = 0
        for i in 1..<pos.count {
            let diff = pos[i] - pos[i - 1]
            if diff == 1 { contiguous += 1 } else if diff > 1 { gaps += (diff - 1) }
        }
        let contigBonus = qlen > 1 ? Double(contiguous) / Double(qlen - 1) : 1.0 // [0..1]

        // Gap penalty: fewer gaps is better
        let gapScore = 1.0 / (1.0 + Double(gaps)) // (0,1]

        // Boundary bonus: matches at boundaries are better
        let boundaryHits = pos.filter { candidate.boundaryIndices.contains($0) }.count
        let boundaryBonus = Double(boundaryHits) / Double(qlen) // [0..1]

        // Composite score
        var score = 0.35 * contigBonus + 0.25 * startBonus + 0.30 * gapScore + 0.10 * boundaryBonus

        // Strong boost for pure prefix
        if start == 0 && contiguous >= (qlen - 1) {
            score = min(1.0, score + 0.2)
        }

        return max(0.0, min(1.0, score))
    }
}
