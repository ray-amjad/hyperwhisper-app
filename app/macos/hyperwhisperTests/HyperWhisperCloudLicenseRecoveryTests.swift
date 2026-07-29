//
//  HyperWhisperCloudLicenseRecoveryTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

struct HyperWhisperCloudLicenseRecoveryTests {

    @Test func retriesUnauthorizedLicensedRequestWithValidatedLicensedIdentity() async throws {
        let recorder = LicenseRecoveryCallRecorder()
        let success = HttpResponse(status: 200, headers: [], body: Data())

        let result = try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
            identifier: "stale-license",
            isLicensed: true,
            send: { identifier, isLicensed in
                let attempt = await recorder.recordSend(identifier: identifier, isLicensed: isLicensed)
                if attempt == 1 {
                    throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                }
                return success
            },
            revalidate: { identifier in
                await recorder.recordRevalidation(identifier)
                return Self.validationResult(isValid: true, status: .active)
            },
            currentIdentifier: {
                await recorder.recordIdentityResolution()
                return ("refreshed-license", true)
            },
            refreshServerAuthCache: { identifier in
                await recorder.recordServerCacheRefresh(identifier)
            }
        )

        #expect(result.response == success)
        #expect(result.identifier == "refreshed-license")
        #expect(result.isLicensed)
        #expect(await recorder.sends == [
            .init(identifier: "stale-license", isLicensed: true),
            .init(identifier: "refreshed-license", isLicensed: true)
        ])
        #expect(await recorder.revalidations == ["stale-license"])
        #expect(await recorder.identityResolutions == 1)
        #expect(await recorder.serverCacheRefreshes == ["refreshed-license"])
    }

    @Test func revokedLicenseDoesNotRetryAsGuest() async {
        let recorder = LicenseRecoveryCallRecorder()

        do {
            _ = try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
                identifier: "revoked-license",
                isLicensed: true,
                send: { identifier, isLicensed in
                    _ = await recorder.recordSend(identifier: identifier, isLicensed: isLicensed)
                    throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                },
                revalidate: { identifier in
                    await recorder.recordRevalidation(identifier)
                    return Self.validationResult(isValid: false, status: .expired)
                },
                currentIdentifier: {
                    await recorder.recordIdentityResolution()
                    return ("guest-device", false)
                },
                refreshServerAuthCache: { identifier in
                    await recorder.recordServerCacheRefresh(identifier)
                }
            )
            Issue.record("Expected the original unauthorized error")
        } catch let error as TranscriptionError {
            guard case .unauthorized = error else {
                Issue.record("Expected unauthorized, got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.unauthorized, got \(error)")
        }

        #expect(await recorder.sends == [
            .init(identifier: "revoked-license", isLicensed: true)
        ])
        #expect(await recorder.revalidations == ["revoked-license"])
        #expect(await recorder.identityResolutions == 0)
        #expect(await recorder.serverCacheRefreshes.isEmpty)
    }

    @Test func unauthorizedGuestRequestDoesNotTriggerLicenseRecovery() async {
        let recorder = LicenseRecoveryCallRecorder()

        do {
            _ = try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
                identifier: "guest-device",
                isLicensed: false,
                send: { identifier, isLicensed in
                    _ = await recorder.recordSend(identifier: identifier, isLicensed: isLicensed)
                    throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                },
                revalidate: { identifier in
                    await recorder.recordRevalidation(identifier)
                    return Self.validationResult(isValid: true, status: .active)
                },
                currentIdentifier: {
                    await recorder.recordIdentityResolution()
                    return ("unexpected-license", true)
                },
                refreshServerAuthCache: { identifier in
                    await recorder.recordServerCacheRefresh(identifier)
                }
            )
            Issue.record("Expected unauthorized")
        } catch let error as TranscriptionError {
            guard case .unauthorized = error else {
                Issue.record("Expected unauthorized, got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.unauthorized, got \(error)")
        }

        #expect(await recorder.sends == [
            .init(identifier: "guest-device", isLicensed: false)
        ])
        #expect(await recorder.revalidations.isEmpty)
        #expect(await recorder.identityResolutions == 0)
        #expect(await recorder.serverCacheRefreshes.isEmpty)
    }

    @Test func serverCacheRefreshFailurePreservesUnauthorizedWithoutRetry() async {
        let recorder = LicenseRecoveryCallRecorder()

        do {
            _ = try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
                identifier: "stale-license",
                isLicensed: true,
                send: { identifier, isLicensed in
                    _ = await recorder.recordSend(identifier: identifier, isLicensed: isLicensed)
                    throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                },
                revalidate: { identifier in
                    await recorder.recordRevalidation(identifier)
                    return Self.validationResult(isValid: true, status: .active)
                },
                currentIdentifier: {
                    await recorder.recordIdentityResolution()
                    return ("refreshed-license", true)
                },
                refreshServerAuthCache: { identifier in
                    await recorder.recordServerCacheRefresh(identifier)
                    throw ServerCacheRefreshError.unavailable
                }
            )
            Issue.record("Expected the original unauthorized error")
        } catch let error as TranscriptionError {
            guard case .unauthorized = error else {
                Issue.record("Expected unauthorized, got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.unauthorized, got \(error)")
        }

        #expect(await recorder.sends == [
            .init(identifier: "stale-license", isLicensed: true)
        ])
        #expect(await recorder.revalidations == ["stale-license"])
        #expect(await recorder.identityResolutions == 1)
        #expect(await recorder.serverCacheRefreshes == ["refreshed-license"])
    }

    @Test func serverCacheRefreshCancellationPropagatesWithoutRetry() async {
        let recorder = LicenseRecoveryCallRecorder()

        do {
            _ = try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
                identifier: "stale-license",
                isLicensed: true,
                send: { identifier, isLicensed in
                    _ = await recorder.recordSend(identifier: identifier, isLicensed: isLicensed)
                    throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                },
                revalidate: { identifier in
                    await recorder.recordRevalidation(identifier)
                    return Self.validationResult(isValid: true, status: .active)
                },
                currentIdentifier: {
                    await recorder.recordIdentityResolution()
                    return ("refreshed-license", true)
                },
                refreshServerAuthCache: { identifier in
                    await recorder.recordServerCacheRefresh(identifier)
                    throw URLError(.cancelled)
                }
            )
            Issue.record("Expected cancellation")
        } catch is CancellationError {
            // Expected: explicit user cancellation must not become unauthorized.
        } catch {
            Issue.record("Expected CancellationError, got \(error)")
        }

        #expect(await recorder.sends == [
            .init(identifier: "stale-license", isLicensed: true)
        ])
        #expect(await recorder.revalidations == ["stale-license"])
        #expect(await recorder.identityResolutions == 1)
        #expect(await recorder.serverCacheRefreshes == ["refreshed-license"])
    }

    @Test func creditRefreshRecognizesURLSessionCancellation() {
        #expect(HyperWhisperCloudManager.isCancellationError(URLError(.cancelled)))
        #expect(!HyperWhisperCloudManager.isCancellationError(URLError(.timedOut)))
    }

    private static func validationResult(isValid: Bool, status: LicenseStatus) -> LicenseValidationResult {
        LicenseValidationResult(
            isValid: isValid,
            status: status,
            customerId: nil,
            customerEmail: nil,
            customerName: nil,
            errorMessage: isValid ? nil : "License is no longer active"
        )
    }
}

private enum ServerCacheRefreshError: Error {
    case unavailable
}

private actor LicenseRecoveryCallRecorder {
    struct Send: Equatable {
        let identifier: String
        let isLicensed: Bool
    }

    private(set) var sends: [Send] = []
    private(set) var revalidations: [String] = []
    private(set) var identityResolutions = 0
    private(set) var serverCacheRefreshes: [String] = []

    func recordSend(identifier: String, isLicensed: Bool) -> Int {
        sends.append(.init(identifier: identifier, isLicensed: isLicensed))
        return sends.count
    }

    func recordRevalidation(_ identifier: String) {
        revalidations.append(identifier)
    }

    func recordIdentityResolution() {
        identityResolutions += 1
    }

    func recordServerCacheRefresh(_ identifier: String) {
        serverCacheRefreshes.append(identifier)
    }
}
