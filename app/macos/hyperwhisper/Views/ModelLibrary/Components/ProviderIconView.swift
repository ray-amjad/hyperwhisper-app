//
//  ProviderIconView.swift
//  hyperwhisper
//
//  Round-rect provider tile with built-in status overlay used in Library
//  rows and modals. Renders the vendor brand mark from
//  `Assets.xcassets/Providers/` when one exists, else an SF Symbol.
//

import SwiftUI

struct ProviderIconView: View {
    let providerKey: LibraryProviderKey
    var size: CGFloat = 28
    var status: LibraryModelStatus = .enabled
    var location: LibraryModelLocation = .cloud

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.22, style: .continuous)
                .fill(LinearGradient(
                    colors: [tileColor.opacity(0.85), tileColor.opacity(0.55)],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                ))
                .frame(width: size, height: size)
                .overlay(
                    RoundedRectangle(cornerRadius: size * 0.22, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5)
                )
                .opacity(isLocked ? 0.45 : 1.0)

            if let asset = providerKey.brandAssetName {
                brandMark(asset)
                    .opacity(isLocked ? 0.45 : 1.0)
            } else {
                Image(systemName: providerKey.fallbackSymbol)
                    .font(.system(size: size * 0.5, weight: .semibold))
                    .foregroundColor(.white)
                    .opacity(isLocked ? 0.45 : 1.0)
            }

            if let badge = overlayBadge {
                ZStack {
                    Circle()
                        .fill(Color(NSColor.windowBackgroundColor).opacity(0.55))
                        .frame(width: size * 0.7, height: size * 0.7)
                        .blur(radius: size * 0.06)
                    Image(systemName: badge.symbol)
                        .font(.system(size: size * 0.55, weight: .bold))
                        .foregroundColor(badge.color)
                }
            }
        }
        .frame(width: size, height: size)
    }

    @ViewBuilder
    private func brandMark(_ asset: String) -> some View {
        let edge = size * 0.66
        if providerKey.brandAssetIsMulticolor {
            Image(asset)
                .resizable()
                .renderingMode(.original)
                .interpolation(.high)
                .scaledToFit()
                .frame(width: edge, height: edge)
        } else {
            Image(asset)
                .resizable()
                .renderingMode(.template)
                .interpolation(.high)
                .scaledToFit()
                .frame(width: edge, height: edge)
                .foregroundStyle(.white)
        }
    }

    private var isLocked: Bool {
        if case .locked = status { return true }
        return false
    }

    private struct Badge {
        let symbol: String
        let color: Color
    }

    private var overlayBadge: Badge? {
        switch status {
        case .locked, .downloadable:
            return Badge(symbol: "lock.fill", color: .primary.opacity(0.85))
        case .error:
            return Badge(symbol: "exclamationmark.triangle.fill", color: .orange)
        case .enabled:
            return nil
        }
    }

    /// Per-provider brand colors (hex from each vendor's official brand/site),
    /// used as the icon tile background.
    private enum Brand {
        static let openai     = Color(red: 0.063, green: 0.639, blue: 0.498) // #10A37F
        static let anthropic  = Color(red: 0.851, green: 0.467, blue: 0.341) // #D97757
        static let gemini     = Color(red: 0.557, green: 0.459, blue: 0.698) // #8E75B2
        static let groq       = Color(red: 0.961, green: 0.314, blue: 0.212) // #F55036
        static let grok       = Color(red: 0.059, green: 0.059, blue: 0.059) // #0F0F0F
        static let deepgram   = Color(red: 0.075, green: 0.937, blue: 0.576) // #13EF93
        static let assemblyAI = Color(red: 0.420, green: 0.357, blue: 1.000) // #6B5BFF
        static let elevenLabs = Color(red: 0.059, green: 0.059, blue: 0.059) // #0F0F0F
        static let mistral    = Color(red: 0.980, green: 0.314, blue: 0.059) // #FA500F
        static let soniox     = Color(red: 0.165, green: 0.427, blue: 0.957) // #2A6DF4
        static let cerebras   = Color(red: 0.945, green: 0.353, blue: 0.153) // #F15A27
    }

    private var tileColor: Color {
        switch providerKey {
        case .cloud(let provider):
            switch provider {
            case .hyperwhisper: return .accentColor
            case .openai: return Brand.openai
            case .groq: return Brand.groq
            case .deepgram: return Brand.deepgram
            case .assemblyAI: return Brand.assemblyAI
            case .elevenLabs: return Brand.elevenLabs
            case .mistral: return Brand.mistral
            case .soniox: return Brand.soniox
            case .gemini: return Brand.gemini
            case .grok: return Brand.grok
            case .microsoftAzureSpeech: return .blue
            case .googleSpeech: return .red
            }
        case .postProcessing(let provider):
            switch provider {
            case .hyperwhisper: return .accentColor
            case .openai: return Brand.openai
            case .anthropic: return Brand.anthropic
            case .gemini: return Brand.gemini
            case .groq: return Brand.groq
            case .grok: return Brand.grok
            case .cerebras: return Brand.cerebras
            case .mistral: return Brand.mistral
            case .localLLM: return .gray
            }
        case .appleSpeech:
            return .gray
        case .localWhisper:
            return .blue
        case .parakeet:
            return .green
        case .qwen3ASR:
            return .purple
        case .nemotron:
            return .teal
        }
    }
}
