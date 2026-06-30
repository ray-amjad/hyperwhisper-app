//
//  RustHTTPExecutor.swift
//  hyperwhisper
//
//  Shared sans-I/O HTTP plumbing for the Rust shared core (Wave 3 / M3-B).
//
//  The Rust core builds a fully-described `HttpRequest` value (URL, method,
//  headers, body) and parses an `HttpResponse` value; the PLATFORM owns all
//  actual network I/O. Audio bytes NEVER cross the FFI boundary — a file is
//  referenced by path (`Body.fileStream` / `HwPart.fileRef`) and the platform
//  streams it from disk.
//
//  This executor takes a binding `HttpRequest`, performs the I/O with
//  `URLSession`, and returns a binding `HttpResponse`. It is reused by EVERY
//  cloud STT provider, so the four `Body` cases below must be exactly right —
//  every later provider inherits any bug here.
//
//  `HttpRequest`, `HttpResponse`, `Header`, `HttpMethod`, `Body`, `HwPart` are
//  all UniFFI-generated types in the `HyperWhisper` module (see
//  `RustCore/hyperwhisper_core.swift`).
//

import Foundation

enum RustHTTPExecutor {

    /// Sentinel multipart field name marking a single-`fileRef` body that must be
    /// streamed as the **raw** request body (not a `multipart/form-data`
    /// envelope). MUST stay byte-identical to `RAW_BODY_FIELD` in
    /// `shared-core-rs/crates/hw-net/src/providers/hyperwhisper_cloud.rs`:
    ///
    ///     pub const RAW_BODY_FIELD: &str = "@raw";
    ///
    /// HyperWhisper Cloud + the routed (Azure-MAI / Google-Chirp) providers
    /// encode their raw-streamed upload as `Body.multipart` carrying exactly one
    /// `HwPart.fileRef(field: "@raw", …)`. We detect that shape and `upload`
    /// the file as the bare request body, with `Content-Type = fileRef.mime`.
    static let rawBodyField = "@raw"

    /// Perform `request` over `session` and capture an `HttpResponse` for the
    /// core to parse.
    ///
    /// - Throws `CancellationError` if the Swift `Task` is cancelled before the
    ///   request is issued. URLSession network errors propagate untranslated so
    ///   the retry wrapper can classify them.
    static func execute(_ request: HttpRequest, session: URLSession) async throws -> HttpResponse {
        if Task.isCancelled { throw CancellationError() }

        var urlRequest = try buildURLRequest(from: request)

        let data: Data
        let response: URLResponse

        switch request.body {
        case .empty:
            (data, response) = try await session.data(for: urlRequest)

        case let .bytes(_, payload):
            // Content-Type for `.bytes` is applied in `buildURLRequest` (below).
            urlRequest.httpBody = payload
            (data, response) = try await session.data(for: urlRequest)

        case let .fileStream(path, _):
            // Raw file body. Content-Type is applied in `buildURLRequest`.
            // `upload(for:fromFile:)` streams from disk — audio is never buffered
            // into memory across FFI.
            let fileURL = URL(fileURLWithPath: path)
            (data, response) = try await session.upload(for: urlRequest, fromFile: fileURL)

        case let .multipart(boundary, parts):
            if let rawFile = rawStreamFileRef(in: parts) {
                // === @raw SENTINEL PATH (HW Cloud / routed) ===
                // A single fileRef whose field == "@raw" means: stream this file
                // as the RAW request body, NOT a multipart envelope. The
                // Content-Type is the fileRef's own mime (set in buildURLRequest
                // when it detected the @raw shape).
                let fileURL = URL(fileURLWithPath: rawFile.path)
                (data, response) = try await session.upload(for: urlRequest, fromFile: fileURL)
            } else {
                // === REAL multipart/form-data PATH (used by the next sub-module:
                // OpenAI / Groq / ElevenLabs / Mistral / Grok) ===
                // Assemble the envelope from the core-provided parts, in order,
                // using the core-provided boundary. File parts are streamed from
                // disk via a temp envelope so audio bytes still never cross FFI.
                //
                // Reserve the temp URL and register cleanup BEFORE writing, so a
                // mid-write throw (e.g. a missing fileRef.path) does not leak the
                // partial temp file.
                let bodyFileURL = FileManager.default.temporaryDirectory
                    .appendingPathComponent("hw-multipart-\(UUID().uuidString).tmp")
                defer { try? FileManager.default.removeItem(at: bodyFileURL) }
                try writeMultipartBody(to: bodyFileURL, boundary: boundary, parts: parts)
                urlRequest.setValue(
                    "multipart/form-data; boundary=\(boundary)",
                    forHTTPHeaderField: "Content-Type"
                )
                (data, response) = try await session.upload(for: urlRequest, fromFile: bodyFileURL)
            }
        }

        guard let http = response as? HTTPURLResponse else {
            throw TranscriptionError.invalidResponse(details: "Non-HTTP response")
        }

        return HttpResponse(
            status: UInt16(clamping: http.statusCode),
            headers: responseHeaders(from: http),
            body: data
        )
    }

    // MARK: - Request construction

    /// Build the base `URLRequest`: URL, method, all core headers, and the
    /// `Content-Type` for body shapes that carry it inline (`.bytes`,
    /// `.fileStream`, and the `@raw` multipart). For real multipart the
    /// `Content-Type` (with boundary) is set at upload time.
    private static func buildURLRequest(from request: HttpRequest) throws -> URLRequest {
        guard let url = URL(string: request.url) else {
            throw TranscriptionError.serverError(statusCode: 0, message: "Invalid request URL: \(request.url)")
        }

        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = methodString(request.method)

        // Apply every core-provided header verbatim, in order.
        for header in request.headers {
            urlRequest.setValue(header.value, forHTTPHeaderField: header.name)
        }

        // Body-derived Content-Type for the inline-typed shapes. The core also
        // emits an explicit `Content-Type` header for the @raw multipart (the
        // audio mime), so the loop above already covers that; we additionally
        // guarantee the bytes/fileStream cases set it even if a builder relied on
        // the body's content_type rather than a header.
        switch request.body {
        case let .bytes(contentType, _):
            urlRequest.setValue(contentType, forHTTPHeaderField: "Content-Type")
        case let .fileStream(_, contentType):
            urlRequest.setValue(contentType, forHTTPHeaderField: "Content-Type")
        case let .multipart(_, parts):
            if let rawFile = rawStreamFileRef(in: parts) {
                urlRequest.setValue(rawFile.mime, forHTTPHeaderField: "Content-Type")
            }
            // real multipart: Content-Type set at upload time (needs boundary).
        case .empty:
            break
        }

        return urlRequest
    }

    /// Detect the `@raw` sentinel: a multipart body with exactly ONE part that is
    /// a `fileRef` whose field name equals `rawBodyField`. Returns the file's
    /// `(path, mime, filename)` when matched, else `nil`.
    ///
    /// This is the load-bearing branch the verifier must confirm — getting it
    /// wrong wraps the audio in a multipart envelope and the backend 400s /
    /// transcribes garbage.
    private static func rawStreamFileRef(in parts: [HwPart]) -> (path: String, mime: String, filename: String)? {
        guard parts.count == 1, case let .fileRef(field, path, mime, filename) = parts[0],
              field == rawBodyField else {
            return nil
        }
        return (path: path, mime: mime, filename: filename)
    }

    /// Map the binding `HttpMethod` to the URLRequest method string.
    private static func methodString(_ method: HttpMethod) -> String {
        switch method {
        case .get: return "GET"
        case .post: return "POST"
        case .put: return "PUT"
        case .delete: return "DELETE"
        }
    }

    /// Flatten an `HTTPURLResponse`'s header fields into the binding `Header`
    /// list the core expects. The core's `HttpResponse.header(_:)` lookup is
    /// case-insensitive, so casing here is irrelevant.
    private static func responseHeaders(from http: HTTPURLResponse) -> [Header] {
        http.allHeaderFields.compactMap { key, value in
            guard let name = key as? String else { return nil }
            return Header(name: name, value: String(describing: value))
        }
    }

    // MARK: - Real multipart assembly (next sub-module's providers)

    /// Assemble a `multipart/form-data` body into `tempURL`, streaming each
    /// `fileRef` part from disk so audio bytes never live fully in memory across
    /// FFI. Parts are written in the core-provided order using the core-provided
    /// boundary. The caller owns `tempURL`'s lifecycle (creates the cleanup
    /// `defer` before calling), so a throw mid-write never leaks the partial file.
    private static func writeMultipartBody(to tempURL: URL, boundary: String, parts: [HwPart]) throws {
        FileManager.default.createFile(atPath: tempURL.path, contents: nil)

        let handle = try FileHandle(forWritingTo: tempURL)
        defer { try? handle.close() }

        let crlf = "\r\n"
        func write(_ string: String) throws {
            guard let data = string.data(using: .utf8) else { return }
            try handle.write(contentsOf: data)
        }

        for part in parts {
            switch part {
            case let .field(name, value):
                try write("--\(boundary)\(crlf)")
                try write("Content-Disposition: form-data; name=\"\(name)\"\(crlf)\(crlf)")
                try write("\(value)\(crlf)")

            case let .fileRef(field, path, mime, filename):
                try write("--\(boundary)\(crlf)")
                try write("Content-Disposition: form-data; name=\"\(field)\"; filename=\"\(filename)\"\(crlf)")
                try write("Content-Type: \(mime)\(crlf)\(crlf)")
                // Stream the file in chunks rather than loading it whole.
                let fileHandle = try FileHandle(forReadingFrom: URL(fileURLWithPath: path))
                defer { try? fileHandle.close() }
                while true {
                    let chunk = try fileHandle.read(upToCount: 1 << 20) ?? Data()
                    if chunk.isEmpty { break }
                    try handle.write(contentsOf: chunk)
                }
                try write(crlf)
            }
        }

        try write("--\(boundary)--\(crlf)")
    }
}
