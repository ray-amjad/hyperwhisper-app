//
//  MemoryPressureMonitor.swift
//  hyperwhisper
//
//  Registers a process-wide macOS memory-pressure source and reclaims idle
//  local models when the system signals distress, via `ModelResidencyRegistry`.
//
//  This is behavior-neutral when there is no pressure: it acts ONLY on
//  .warning / .critical events — i.e. exactly the situations where an app
//  sitting on 1–2 GB of unused model weights is being a bad citizen and the
//  user's foreground work matters more than a future cold-load. It never
//  evicts a model that is actively transcribing (busy entries are skipped by
//  the registry).
//
//  Owned by AppDelegate for the process lifetime.
//

import Foundation
import os

final class MemoryPressureMonitor {

    private let source: DispatchSourceMemoryPressure
    private let log = Logger(subsystem: "com.hyperwhisper.app", category: "memory")

    init() {
        source = DispatchSource.makeMemoryPressureSource(
            eventMask: [.warning, .critical],
            queue: .global(qos: .utility)
        )
        source.setEventHandler { [weak self] in
            self?.handleEvent()
        }
        source.resume()
        log.info("memory.pressure.monitor.started footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
    }

    deinit {
        source.cancel()
    }

    private func handleEvent() {
        let isCritical = source.data.contains(.critical)
        let level = isCritical ? "critical" : "warning"
        log.notice("memory.pressure event=\(level, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")

        Task {
            if isCritical {
                // Critical: reclaim everything idle, including the local LLM. One
                // cold reload later is an acceptable price for not contributing to
                // a system-wide stall (and for not being OOM-killed ourselves).
                await ModelResidencyRegistry.shared.evict(
                    aggressive: true, reason: "pressure.critical", minIdle: 0
                )
            } else {
                // Warning: softer — only STT runtimes idle for >30s, leave the LLM
                // and anything recently used alone so warm latency is preserved
                // during active work.
                await ModelResidencyRegistry.shared.evict(
                    aggressive: false, reason: "pressure.warning", minIdle: 30
                )
            }
        }
    }
}
