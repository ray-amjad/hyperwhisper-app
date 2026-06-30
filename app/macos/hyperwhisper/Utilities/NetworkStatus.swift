//
//  NetworkStatus.swift
//  hyperwhisper
//
//  Lightweight reachability helper using Network framework.
//  Provides a simple way to check if the device is currently online.
//

import Foundation
import Network
import Combine

final class NetworkStatus: ObservableObject {
    static let shared = NetworkStatus()

    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "NetworkStatusMonitor")

    @Published private(set) var isOnline: Bool = true

    private init() {
        monitor.pathUpdateHandler = { [weak self] path in
            DispatchQueue.main.async {
                self?.isOnline = (path.status == .satisfied)
            }
        }
        monitor.start(queue: queue)
    }
}
