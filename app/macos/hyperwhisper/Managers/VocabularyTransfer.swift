//
//  VocabularyTransfer.swift
//  hyperwhisper
//
//  Vocabulary import and export as CSV or TXT (issue #2409).
//
//  The format is shared by every head and pinned by
//  `shared-conformance/vocabulary-transfer-vectors.json`:
//
//  - CSV: UTF-8, RFC 4180, header `word,replacement`, CRLF rows. A field is
//    quoted only when it holds a comma, a double quote, CR or LF. The reader
//    accepts a BOM, LF or CRLF, no header, one column, extra columns (ignored)
//    and blank rows (ignored). The first row is the header when its first
//    field is `word` (any case).
//  - TXT: UTF-8, LF rows, one term per line, a replacement after the first
//    tab. In the replacement a backslash is written `\\` and a line break
//    `\n`; any other backslash reads as itself. A tab in a word is written as
//    a space.
//  - Both readers trim words and replacements, treat a blank replacement as
//    none, split a word that spans lines into one word per line with the same
//    replacement (#1777, via `PersistenceController.vocabularyWordLines`), skip
//    blank words, and skip (and count) words over
//    `PersistenceController.vocabularyMaxWordChars` characters (#2390).
//
//  Pure value code: the merge into the store lives in
//  `PersistenceController+VocabularyTransfer.swift`.
//

import Foundation
import UniformTypeIdentifiers

/// One vocabulary term as it travels through a CSV or TXT file.
struct VocabularyTransferEntry: Equatable, Sendable {
    let word: String
    /// `nil` means the term has no replacement.
    let replacement: String?
}

/// The file formats the Vocabulary page reads and writes.
enum VocabularyTransferFormat: Sendable {
    case csv
    case txt

    /// The format for a file name's extension, or `nil` for anything else.
    init?(fileExtension: String) {
        switch fileExtension.lowercased() {
        case "csv": self = .csv
        case "txt": self = .txt
        default: return nil
        }
    }

    var fileExtension: String {
        switch self {
        case .csv: return "csv"
        case .txt: return "txt"
        }
    }

    var contentType: UTType {
        switch self {
        case .csv: return .commaSeparatedText
        case .txt: return .plainText
        }
    }
}

/// The parsed rows of a file, before the merge into the store.
struct VocabularyTransferParseResult: Equatable, Sendable {
    /// Rows in file order. A word that spans lines is already one row per line.
    let entries: [VocabularyTransferEntry]
    /// Words dropped for being longer than `vocabularyMaxWordChars`.
    let skipped: Int
}

/// Why a vocabulary file could not be read.
enum VocabularyTransferError: Error, Equatable {
    /// The bytes are not valid UTF-8.
    case notUTF8
}

enum VocabularyTransfer {

    /// Default file name the Export panel offers.
    static let defaultExportFileName = "HyperWhisper Vocabulary.csv"

    // MARK: - Writing

    static func write(_ entries: [VocabularyTransferEntry], as format: VocabularyTransferFormat) -> String {
        switch format {
        case .csv: return writeCSV(entries)
        case .txt: return writeTXT(entries)
        }
    }

    /// `word,replacement` header, then one CRLF row per entry.
    static func writeCSV(_ entries: [VocabularyTransferEntry]) -> String {
        var out = "word,replacement\r\n"
        for entry in entries {
            out += csvField(entry.word)
            out += ","
            out += csvField(entry.replacement ?? "")
            out += "\r\n"
        }
        return out
    }

    /// One LF line per entry; a replacement follows a tab, with `\` and line
    /// breaks escaped.
    static func writeTXT(_ entries: [VocabularyTransferEntry]) -> String {
        var out = ""
        for entry in entries {
            out += entry.word.replacingOccurrences(of: "\t", with: " ")
            if let replacement = entry.replacement, !replacement.isEmpty {
                out += "\t"
                out += escapeTXTReplacement(replacement)
            }
            out += "\n"
        }
        return out
    }

    private static func csvField(_ value: String) -> String {
        let needsQuotes = value.unicodeScalars.contains { $0 == "," || $0 == "\"" || $0 == "\r" || $0 == "\n" }
        guard needsQuotes else { return value }
        return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    private static func escapeTXTReplacement(_ value: String) -> String {
        var out = String.UnicodeScalarView()
        var scalars = value.unicodeScalars.makeIterator()
        var pending = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            switch scalar {
            case "\\":
                out.append("\\")
                out.append("\\")
            case "\r":
                // CRLF and a lone CR are one line break, written `\n`.
                if pending == "\n" { pending = scalars.next() }
                out.append("\\")
                out.append("n")
            case "\n":
                out.append("\\")
                out.append("n")
            default:
                out.append(scalar)
            }
        }
        return String(out)
    }

    // MARK: - Reading

    /// Decodes `data` as UTF-8 (dropping a leading BOM) and parses it.
    static func read(_ data: Data, as format: VocabularyTransferFormat) throws -> VocabularyTransferParseResult {
        let text = try decode(data)
        switch format {
        case .csv: return readCSV(text)
        case .txt: return readTXT(text)
        }
    }

    /// Strict UTF-8 decode; a leading byte-order mark is dropped.
    static func decode(_ data: Data) throws -> String {
        guard let text = String(data: data, encoding: .utf8) else {
            throw VocabularyTransferError.notUTF8
        }
        return dropBOM(text)
    }

    static func readCSV(_ text: String) -> VocabularyTransferParseResult {
        let rows = csvRows(dropBOM(text))
            .filter { row in !row.allSatisfy { $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } }

        var builder = EntryBuilder()
        for (index, row) in rows.enumerated() {
            if index == 0,
               row[0].trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "word" {
                continue
            }
            builder.add(word: row[0], replacement: row.count > 1 ? row[1] : nil)
        }
        return builder.result
    }

    static func readTXT(_ text: String) -> VocabularyTransferParseResult {
        var builder = EntryBuilder()
        for line in lines(dropBOM(text)) {
            if let tab = line.unicodeScalars.firstIndex(of: "\t") {
                let word = String(line.unicodeScalars[..<tab])
                let rest = String(line.unicodeScalars[line.unicodeScalars.index(after: tab)...])
                builder.add(word: word, replacement: unescapeTXTReplacement(rest))
            } else {
                builder.add(word: line, replacement: nil)
            }
        }
        return builder.result
    }

    /// Applies the shared word rules to raw (word, replacement) pairs.
    private struct EntryBuilder {
        var entries: [VocabularyTransferEntry] = []
        var skipped = 0

        mutating func add(word: String, replacement: String?) {
            let trimmedReplacement = replacement?.trimmingCharacters(in: .whitespacesAndNewlines)
            let normalizedReplacement = (trimmedReplacement?.isEmpty ?? true) ? nil : trimmedReplacement
            for line in PersistenceController.vocabularyWordLines(from: word) {
                // UTF-16 units, as .NET's `string.Length` counts them, so every head
                // skips the same words.
                if line.utf16.count > PersistenceController.vocabularyMaxWordChars {
                    skipped += 1
                } else {
                    entries.append(VocabularyTransferEntry(word: line, replacement: normalizedReplacement))
                }
            }
        }

        var result: VocabularyTransferParseResult {
            VocabularyTransferParseResult(entries: entries, skipped: skipped)
        }
    }

    private static func dropBOM(_ text: String) -> String {
        guard text.unicodeScalars.first == "\u{FEFF}" else { return text }
        return String(text.unicodeScalars.dropFirst())
    }

    /// Splits on CRLF, LF or CR. Works on scalars: Swift treats CRLF as one
    /// `Character`, which a character split would miss.
    private static func lines(_ text: String) -> [String] {
        var result: [String] = []
        var current = String.UnicodeScalarView()
        var scalars = text.unicodeScalars.makeIterator()
        var pending = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            switch scalar {
            case "\r":
                if pending == "\n" { pending = scalars.next() }
                result.append(String(current))
                current = String.UnicodeScalarView()
            case "\n":
                result.append(String(current))
                current = String.UnicodeScalarView()
            default:
                current.append(scalar)
            }
        }
        if !current.isEmpty { result.append(String(current)) }
        return result
    }

    /// RFC 4180 rows. Lenient where the RFC is strict: a quote inside an
    /// unquoted field is literal, text after a closing quote joins the field,
    /// and an unterminated quote runs to the end of the file.
    private static func csvRows(_ text: String) -> [[String]] {
        var rows: [[String]] = []
        var row: [String] = []
        var field = String.UnicodeScalarView()
        var inQuotes = false
        var fieldStarted = false
        var rowStarted = false

        func endField() {
            row.append(String(field))
            field = String.UnicodeScalarView()
            fieldStarted = false
        }
        func endRow() {
            endField()
            rows.append(row)
            row = []
            rowStarted = false
        }

        var scalars = text.unicodeScalars.makeIterator()
        var pending = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            if inQuotes {
                if scalar == "\"" {
                    if pending == "\"" {
                        field.append("\"")
                        pending = scalars.next()
                    } else {
                        inQuotes = false
                    }
                } else {
                    field.append(scalar)
                }
                continue
            }
            switch scalar {
            case "\"" where !fieldStarted:
                inQuotes = true
                fieldStarted = true
                rowStarted = true
            case ",":
                endField()
                rowStarted = true
            case "\r":
                if pending == "\n" { pending = scalars.next() }
                endRow()
            case "\n":
                endRow()
            default:
                field.append(scalar)
                fieldStarted = true
                rowStarted = true
            }
        }
        if rowStarted || !field.isEmpty || !row.isEmpty {
            endRow()
        }
        return rows
    }

    /// `\\` is a backslash, `\n` a line break; any other backslash is itself.
    private static func unescapeTXTReplacement(_ value: String) -> String {
        var out = String.UnicodeScalarView()
        var scalars = value.unicodeScalars.makeIterator()
        var pending = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            guard scalar == "\\" else {
                out.append(scalar)
                continue
            }
            if pending == "\\" {
                out.append("\\")
                pending = scalars.next()
            } else if pending == "n" {
                out.append("\n")
                pending = scalars.next()
            } else {
                out.append("\\")
            }
        }
        return String(out)
    }
}
