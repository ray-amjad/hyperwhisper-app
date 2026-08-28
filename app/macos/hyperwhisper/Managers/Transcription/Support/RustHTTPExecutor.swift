//
//  RustHTTPExecutor.swift
//  hyperwhisper
//
//  Shared sans-I/O HTTP plumbing for the Rust shared core (Wave 3 / M3-B).
//
//  The Rust core builds a fully-described `HttpRequest` value (URL, method,
//  headers, body) and parses an `HttpResponse` value; the PLATFORM owns all
//  actual network I/O. Audio bytes NEVER cross the FFI boundary — a file is
//  referenced by path (`Body.fileStream` / `HwPart.fileRef` /
//  `Body.jsonWithBase64File`) and the platform streams it from disk. That holds
//  even for the inline-base64 body: the core names the path and the surrounding
//  JSON, and the platform does the encoding.
//
//  This executor takes a binding `HttpRequest`, performs the I/O with
//  `URLSession`, and returns a binding `HttpResponse`. It is reused by EVERY
//  cloud STT provider, so the five `Body` cases below must be exactly right —
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

        case let .jsonWithBase64File(prefix, path, suffix):
            // === INLINE-BASE64 JSON PATH (Gemini 3.5 Transcribe) ===
            // `/v1beta/interactions` has no file-reference form, so the core
            // hands us the two JSON fragments that surround the audio and we
            // splice the base64 in between. Same temp-file discipline as the
            // multipart path: reserve the URL and register cleanup BEFORE
            // writing, and encode in chunks so a 14 MB file is never held twice
            // in memory. Content-Type is applied in `buildURLRequest`.
            let bodyFileURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("hw-jsonb64-\(UUID().uuidString).tmp")
            defer { try? FileManager.default.removeItem(at: bodyFileURL) }
            try writeJSONWithBase64Body(to: bodyFileURL, prefix: prefix, path: path, suffix: suffix)
            (data, response) = try await session.upload(for: urlRequest, fromFile: bodyFileURL)
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
    /// Materialise a core request into a `URLRequest` with its body attached.
    ///
    /// Use this instead of `execute` when the caller owns the I/O: the LLM
    /// post-processing paths each set their own timeout, run their own retry
    /// policy, and one of them reads an SSE stream line by line. They still get
    /// the URL, the headers and the body from the core (issue #282).
    ///
    /// Only inline bodies are attached. A file-backed body must go through
    /// `execute`, which streams it from disk — and asking for one here THROWS
    /// rather than returning a bodyless request. Dropping it silently would put
    /// a fully-formed, correctly-authenticated request with an empty payload on
    /// the wire, which reads at the call site as a provider rejecting valid
    /// input. Unreachable today (every caller builds a JSON `.bytes` body), so
    /// the switch is exhaustive with no `default` arm: a new `Body` case has to
    /// be classified here rather than falling into the silent path.
    static func buildInlineURLRequest(from request: HttpRequest) throws -> URLRequest {
        var urlRequest = try buildURLRequest(from: request)
        switch request.body {
        case .empty:
            break
        case let .bytes(_, payload):
            urlRequest.httpBody = payload
        case .fileStream, .multipart, .jsonWithBase64File:
            throw TranscriptionError.serverError(
                statusCode: 0,
                message: "buildInlineURLRequest cannot attach a file-backed body — route this request through RustHTTPExecutor.execute"
            )
        }
        return urlRequest
    }

    static func buildURLRequest(from request: HttpRequest) throws -> URLRequest {
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
        case .jsonWithBase64File:
            // The spliced body is a JSON document by construction. The core also
            // emits an explicit `Content-Type` header, so this is belt-and-braces
            // for the same reason the bytes/fileStream arms are.
            urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
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

    // MARK: - Inline-base64 JSON assembly (Gemini 3.5 Transcribe)

    /// Read size for the chunked base64 encoder. 3-byte-aligned so a full chunk
    /// encodes without padding; 1.5 MB in → 2 MB of base64 out. Not `private`
    /// so the tests can straddle the boundary the carry logic turns on.
    static let base64ChunkSize = 3 * 512 * 1024

    /// Write `prefix` ++ base64(file at `path`) ++ `suffix` into `tempURL`.
    ///
    /// The encoding is the STANDARD alphabet, padded, with no line breaks —
    /// `Data.base64EncodedData()`'s default — which is what the core's
    /// `Body.JsonWithBase64File` contract specifies. Anything else (URL-safe
    /// alphabet, wrapped lines, missing padding) makes Google reject the audio.
    ///
    /// The file is encoded in chunks so a 14 MB recording is never resident
    /// twice. Padding may only appear at the very END of a base64 stream, so
    /// each chunk written out must be a multiple of 3 raw bytes; the remainder
    /// is carried over to the next read and the final tail (which may be 1 or 2
    /// bytes) is encoded last, where its padding is correct. A short read from
    /// `FileHandle` therefore cannot corrupt the encoding.
    ///
    /// The caller owns `tempURL`'s lifecycle (creates the cleanup `defer` before
    /// calling), matching `writeMultipartBody`.
    ///
    /// Not `private`, so `RustHTTPExecutorBase64Tests` can exercise the carry
    /// across the chunk boundary. A carry bug corrupts only recordings larger
    /// than `chunkSize` (1.5 MB), which no other test reaches.
    static func writeJSONWithBase64Body(
        to tempURL: URL,
        prefix: Data,
        path: String,
        suffix: Data
    ) throws {
        FileManager.default.createFile(atPath: tempURL.path, contents: nil)

        let handle = try FileHandle(forWritingTo: tempURL)
        defer { try? handle.close() }

        try handle.write(contentsOf: prefix)

        let fileHandle = try FileHandle(forReadingFrom: URL(fileURLWithPath: path))
        defer { try? fileHandle.close() }

        let chunkSize = base64ChunkSize
        var pending = Data()
        while true {
            let chunk = try fileHandle.read(upToCount: chunkSize) ?? Data()
            if chunk.isEmpty { break }
            pending.append(chunk)

            let encodable = pending.count - (pending.count % 3)
            if encodable > 0 {
                try handle.write(contentsOf: Data(pending.prefix(encodable)).base64EncodedData())
                pending.removeFirst(encodable)
            }
        }
        // Final 1–2 byte tail: the only place padding is allowed.
        if !pending.isEmpty {
            try handle.write(contentsOf: pending.base64EncodedData())
        }

        try handle.write(contentsOf: suffix)
    }
}
