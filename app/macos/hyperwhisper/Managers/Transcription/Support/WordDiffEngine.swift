//
//  WordDiffEngine.swift
//  hyperwhisper
//
//  Finds word substitutions between pasted transcript text and user-edited text.
//

import Foundation

struct WordDiffSubstitution: Equatable {
    let original: String
    let replacement: String
}

enum WordDiffEngine {
    static func findSingleWordSubstitutions(original: String, edited: String) -> [WordDiffSubstitution] {
        let originalTokens = tokenize(original)
        let editedTokens = tokenize(edited)

        guard !originalTokens.isEmpty, !editedTokens.isEmpty else { return [] }

        let anchors = lcsIndexPairs(originalTokens, editedTokens)
        var substitutions: [WordDiffSubstitution] = []
        var previousOriginalIndex = 0
        var previousEditedIndex = 0

        for (originalIndex, editedIndex) in anchors {
            let originalSegment = Array(originalTokens[previousOriginalIndex..<originalIndex])
            let editedSegment = Array(editedTokens[previousEditedIndex..<editedIndex])
            substitutions.append(contentsOf: pairSegments(originalSegment, editedSegment))

            previousOriginalIndex = originalIndex + 1
            previousEditedIndex = editedIndex + 1
        }

        if previousOriginalIndex < originalTokens.count || previousEditedIndex < editedTokens.count {
            let originalSegment = Array(originalTokens[previousOriginalIndex..<originalTokens.count])
            let editedSegment = Array(editedTokens[previousEditedIndex..<editedTokens.count])
            substitutions.append(contentsOf: pairSegments(originalSegment, editedSegment))
        }

        return substitutions
    }

    static func tokenize(_ text: String) -> [String] {
        text
            .split(whereSeparator: \.isWhitespace)
            .map { token in
                token.trimmingCharacters(in: .punctuationCharacters)
            }
            .filter { !$0.isEmpty }
    }

    private static func pairSegments(_ original: [String], _ edited: [String]) -> [WordDiffSubstitution] {
        guard !original.isEmpty, !edited.isEmpty else { return [] }

        if original.count == edited.count {
            return zip(original, edited).compactMap { originalWord, replacementWord in
                guard originalWord.caseInsensitiveCompare(replacementWord) != .orderedSame else { return nil }
                return WordDiffSubstitution(original: originalWord, replacement: replacementWord)
            }
        }

        var substitutions: [WordDiffSubstitution] = []
        for originalWord in original {
            for replacementWord in edited {
                guard originalWord.caseInsensitiveCompare(replacementWord) != .orderedSame else { continue }
                substitutions.append(WordDiffSubstitution(original: originalWord, replacement: replacementWord))
            }
        }
        return substitutions
    }

    private static func lcsIndexPairs(_ original: [String], _ edited: [String]) -> [(Int, Int)] {
        let originalCount = original.count
        let editedCount = edited.count
        var table = Array(
            repeating: Array(repeating: 0, count: editedCount + 1),
            count: originalCount + 1
        )

        for originalIndex in stride(from: originalCount - 1, through: 0, by: -1) {
            for editedIndex in stride(from: editedCount - 1, through: 0, by: -1) {
                if original[originalIndex].caseInsensitiveCompare(edited[editedIndex]) == .orderedSame {
                    table[originalIndex][editedIndex] = table[originalIndex + 1][editedIndex + 1] + 1
                } else {
                    table[originalIndex][editedIndex] = max(
                        table[originalIndex + 1][editedIndex],
                        table[originalIndex][editedIndex + 1]
                    )
                }
            }
        }

        var pairs: [(Int, Int)] = []
        var originalIndex = 0
        var editedIndex = 0

        while originalIndex < originalCount, editedIndex < editedCount {
            if original[originalIndex].caseInsensitiveCompare(edited[editedIndex]) == .orderedSame {
                pairs.append((originalIndex, editedIndex))
                originalIndex += 1
                editedIndex += 1
            } else if table[originalIndex + 1][editedIndex] >= table[originalIndex][editedIndex + 1] {
                originalIndex += 1
            } else {
                editedIndex += 1
            }
        }

        return pairs
    }
}
