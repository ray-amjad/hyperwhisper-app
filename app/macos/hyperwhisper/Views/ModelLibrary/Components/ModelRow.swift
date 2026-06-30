//
//  ModelRow.swift
//  hyperwhisper
//

import SwiftUI

struct ModelRow: View {
    let model: LibraryModel
    let onLockTap: () -> Void
    let onCloudTap: () -> Void
    let onCancelTap: (() -> Void)?

    init(
        model: LibraryModel,
        onLockTap: @escaping () -> Void,
        onCloudTap: @escaping () -> Void,
        onCancelTap: (() -> Void)? = nil
    ) {
        self.model = model
        self.onLockTap = onLockTap
        self.onCloudTap = onCloudTap
        self.onCancelTap = onCancelTap
    }

    var body: some View {
        HStack(spacing: 14) {
            HStack(spacing: 10) {
                providerTile
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(model.displayName)
                            .font(.system(size: 13, weight: .medium))
                            .foregroundColor(.primary)
                        if let tag = model.tag {
                            Text(tag)
                                .font(.system(size: 10, weight: .semibold))
                                .padding(.horizontal, 5)
                                .padding(.vertical, 1.5)
                                .background(Color.primary.opacity(0.08))
                                .clipShape(Capsule())
                                .foregroundColor(.secondary)
                        }
                    }
                    Text(model.providerKey.displayName)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            kindBadge
                .frame(width: 50, alignment: .leading)

            gauges
                .frame(width: 110, alignment: .leading)

            cloudCell
                .frame(width: 130, alignment: .trailing)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(rowBackground)
        .contentShape(Rectangle())
    }

    // MARK: - Cells

    @ViewBuilder
    private var providerTile: some View {
        let icon = ProviderIconView(
            providerKey: model.providerKey,
            size: 28,
            status: model.status,
            location: model.location
        )

        switch model.status {
        case .locked:
            Button(action: onLockTap) { icon }
                .buttonStyle(.plain)
                .help("Connect to unlock")
        case .error:
            Button(action: onLockTap) { icon }
                .buttonStyle(.plain)
                .help("Reconnect to fix")
        case .enabled, .downloadable:
            icon
        }
    }

    @ViewBuilder
    private var kindBadge: some View {
        Image(systemName: model.kind == .voice ? "waveform" : "text.alignleft")
            .font(.system(size: 12, weight: .semibold))
            .foregroundColor(.secondary)
            .help(model.kind == .voice ? "Voice model" : "Text model")
    }

    private var gauges: some View {
        VStack(alignment: .leading, spacing: 3) {
            gaugeBar(rating: model.speed)
                .help("Speed: \(model.speed)/5")
            gaugeBar(rating: model.accuracy)
                .help("Accuracy: \(model.accuracy)/5")
        }
    }

    private func gaugeBar(rating: Int) -> some View {
        HStack(spacing: 3) {
            ForEach(0..<5, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1)
                    .fill(i < rating ? gaugeColor : Color.primary.opacity(0.12))
                    .frame(width: 12, height: 4)
            }
        }
    }

    private var gaugeColor: Color {
        switch model.status {
        case .enabled:
            return .accentColor
        case .locked, .downloadable:
            return .secondary
        case .error:
            return .orange
        }
    }

    @ViewBuilder
    private var cloudCell: some View {
        switch model.location {
        case .cloud:
            HStack(spacing: 0) {
                Spacer()
                Image(systemName: "cloud.fill")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .help("Cloud model")
            }
        case .offline(let sizeDescription, let installed, let progress):
            HStack(spacing: 8) {
                Spacer()
                if let size = sizeDescription {
                    Text(size)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                }
                if let progress = progress {
                    let ring = ZStack {
                        Circle()
                            .stroke(Color.secondary.opacity(0.2), lineWidth: 2)
                            .frame(width: 16, height: 16)
                        Circle()
                            .trim(from: 0, to: max(0.02, min(1.0, CGFloat(progress))))
                            .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                            .rotationEffect(.degrees(-90))
                            .frame(width: 16, height: 16)
                        if onCancelTap != nil {
                            Image(systemName: "xmark")
                                .font(.system(size: 7, weight: .bold))
                                .foregroundColor(.secondary)
                        }
                    }

                    if let cancel = onCancelTap {
                        Button(action: cancel) { ring }
                            .buttonStyle(.plain)
                            .help("Cancel download")
                    } else {
                        ring
                    }
                } else if installed && model.allowsOfflineRemoval {
                    Button(action: onCloudTap) {
                        Image(systemName: "trash")
                            .font(.system(size: 12))
                            .foregroundColor(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("Remove downloaded model")
                } else if installed {
                    // Built-in / non-removable model — already available, no action to offer.
                    EmptyView()
                } else if case .error(let message) = model.status {
                    // Capability gate (e.g. Intel "Requires Apple Silicon", or a Rosetta
                    // relaunch nudge): the model can't run here, so offer no download —
                    // surface the reason instead.
                    Text(message)
                        .font(.system(size: 10))
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.trailing)
                        .lineLimit(2)
                        .help(message)
                } else {
                    Button(action: onCloudTap) {
                        Image(systemName: "arrow.down.circle.fill")
                            .font(.system(size: 14))
                            .foregroundColor(.accentColor)
                    }
                    .buttonStyle(.plain)
                    .help("Download model")
                }
            }
        }
    }

    @ViewBuilder
    private var rowBackground: some View {
        switch model.status {
        case .locked:
            Color.primary.opacity(0.02)
        case .error:
            Color.orange.opacity(0.05)
        case .downloadable:
            Color.accentColor.opacity(0.03)
        case .enabled:
            Color.clear
        }
    }
}
