//
//  CloudProviderPathParityTests.swift
//  hyperwhisperTests
//
//  Issue #808: the router resolved a cloud provider twice, once for the app
//  (`selectProvider(for:vocabulary:)`, from a Mode) and once for the Local API
//  (`resolveProvider(engine:model:language:)`), each with its own BYOK-key
//  switch and instance switch. The copies had drifted. Both paths now share
//  one switch per concern; this pins that, for every `CloudProvider`, the two
//  entry points hand back the SAME provider instance, or fail the same way.
//
//  No license or credit manager is wired, so HyperWhisper Cloud fails to
//  initialise on both paths, which is the error path the shared helper now
//  owns. Every BYOK provider gets a fixture key.
//
//  The Local API's engine string goes through `CloudSTTCatalog` first, which
//  folds the retired standalone `googleSpeech` / `microsoftAzureSpeech`
//  spellings into HyperWhisper Cloud accuracy tiers. So the app side is
//  driven with the provider the Local API actually resolves the engine to.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Answers a fixed, obviously fake key for every provider and never reads the
/// Keychain. The router takes the concrete type, so this subclasses it.
@MainActor
private final class EveryProviderKeySettings: SettingsManager {
    override func apiKey(for provider: CloudProvider) -> String {
        "fixture-\(provider.rawValue)-not-a-secret"
    }
}

/// What one entry point resolved to: the instance and the cloud identity, or
/// the error it threw.
private enum Resolution: Equatable, CustomStringConvertible {
    case provider(ObjectIdentifier, name: String, cloud: CloudProvider?)
    case failure(String)

    var description: String {
        switch self {
        case let .provider(_, name, cloud): return "provider \(name) (\(cloud?.rawValue ?? "nil"))"
        case let .failure(error): return "error \(error)"
        }
    }

    init(_ resolve: () async throws -> TranscriptionProviderRouter.ProviderSelection) async {
        do {
            let selection = try await resolve()
            self = .provider(
                ObjectIdentifier(selection.provider as AnyObject),
                name: selection.provider.name,
                cloud: selection.cloudProviderType
            )
        } catch {
            self = .failure(String(describing: error))
        }
    }
}

@MainActor
@Suite("The app and the Local API resolve the same cloud provider (#808)", .serialized)
struct CloudProviderPathParityTests {

    @Test(arguments: CloudProvider.allCases)
    func bothEntryPointsResolveTheSameProvider(_ cloudProvider: CloudProvider) async {
        let persistence = PersistenceController(inMemory: true)
        let settings = EveryProviderKeySettings()
        let router = TranscriptionProviderRouter()
        router.setManagers(healthManager: nil, licenseManager: nil, creditManager: nil, settingsManager: settings)

        let engine = cloudProvider.rawValue
        let resolvedByLocalAPI = CloudProvider.parse(CloudSTTCatalog.shared.normalizeCloudProvider(engine).provider)
            ?? cloudProvider

        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "Parity \(cloudProvider.rawValue)"
        mode.model = "cloud"
        mode.cloudProvider = resolvedByLocalAPI.rawValue
        mode.createdDate = Date()
        mode.modifiedDate = Date()

        let app = await Resolution { try await router.selectProvider(for: mode, vocabulary: []) }
        let localAPI = await Resolution { try await router.resolveProvider(engine: engine, model: nil, language: nil) }

        #expect(app == localAPI, "\(engine) → \(resolvedByLocalAPI.rawValue): app → \(app), Local API → \(localAPI)")
        if cloudProvider.requiresAPIKey {
            // A BYOK provider with a key must resolve, not just fail alike.
            guard case let .provider(_, _, cloud) = app else {
                Issue.record("\(cloudProvider.rawValue) with a key did not resolve: \(app)")
                return
            }
            #expect(cloud == cloudProvider)
        }
        withExtendedLifetime((persistence, settings)) {}
    }
}
