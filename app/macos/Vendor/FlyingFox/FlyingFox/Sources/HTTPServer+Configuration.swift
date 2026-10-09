//
//  HTTPServer+Configuration.swift
//  FlyingFox
//
//  Created by Simon Whitty on 06/08/2024.
//  Copyright © 2024 Simon Whitty. All rights reserved.
//
//  Distributed under the permissive MIT license
//  Get the latest version from here:
//
//  https://github.com/swhitty/FlyingFox
//
//  Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in all
//  copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//  SOFTWARE.
//

import Foundation
import FlyingSocks

public extension HTTPServer {

    struct Configuration: Sendable {
        public var address: any SocketAddress
        public var timeout: TimeInterval
        public var sharedRequestBufferSize: Int
        public var sharedRequestReplaySize: Int
        public var pool: any AsyncSocketPool
        public var logger: any Logging
        // HyperWhisper patch (#1463): bounds on reading a request head.
        public var requestHeadLimits: RequestHeadLimits

        public init(address: some SocketAddress,
                    timeout: TimeInterval = 15,
                    sharedRequestBufferSize: Int = 4_096,
                    sharedRequestReplaySize: Int = 2_097_152,
                    pool: any AsyncSocketPool = HTTPServer.defaultPool(),
                    logger: any Logging = HTTPServer.defaultLogger(),
                    requestHeadLimits: RequestHeadLimits = .unlimited) {
            self.address = address
            self.timeout = timeout
            self.sharedRequestBufferSize = sharedRequestBufferSize
            self.sharedRequestReplaySize = sharedRequestReplaySize
            self.pool = pool
            self.logger = logger
            self.requestHeadLimits = requestHeadLimits
        }
    }

    // HyperWhisper patch (#1463).
    //
    // Bounds on reading a request head: the request line and the header lines,
    // up to and including the blank line that ends them. A line is the bytes
    // before its LF, less one trailing CR. A head that breaks a byte limit is
    // refused with 431 Request Header Fields Too Large and the connection is
    // closed. A head that is not complete within `readTimeout` seconds of the
    // server starting to wait for it gets no answer: the connection is closed
    // and its task ends. On a keep-alive connection that wait starts after the
    // previous response, so the timeout is also the idle limit.
    struct RequestHeadLimits: Sendable, Equatable {
        public var maxLineBytes: Int
        public var maxHeadBytes: Int
        public var readTimeout: TimeInterval?

        public init(maxLineBytes: Int, maxHeadBytes: Int, readTimeout: TimeInterval?) {
            self.maxLineBytes = maxLineBytes
            self.maxHeadBytes = maxHeadBytes
            self.readTimeout = readTimeout
        }

        /// Upstream FlyingFox behaviour: no byte limits and no deadline. The head
        /// is still read in linear time.
        public static let unlimited = RequestHeadLimits(maxLineBytes: .max, maxHeadBytes: .max, readTimeout: nil)
    }
}

extension HTTPServer.Configuration {

    init(port: UInt16,
         timeout: TimeInterval = 15,
         logger: any Logging = HTTPServer.defaultLogger()
    ) {
#if canImport(WinSDK)
        let address = sockaddr_in.inet(port: port)
#else
        let address = sockaddr_in6.inet6(port: port)
#endif
        self.init(
            address: address,
            timeout: timeout,
            logger: logger
        )
    }
}
