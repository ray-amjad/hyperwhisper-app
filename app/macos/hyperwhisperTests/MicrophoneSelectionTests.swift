//
//  MicrophoneSelectionTests.swift
//  hyperwhisperTests
//
//  The microphone picker keeps ONE saved choice for HyperWhisper, separate from the
//  macOS default input. These pin how that choice resolves against the connected
//  devices: present, absent (kept, with a fallback to the default), and "Default".
//

import Foundation
import Testing
@testable import HyperWhisper

private let builtIn = AudioDevice(id: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone", uid: "BuiltInMicrophoneDevice")
private let scarlett = AudioDevice(id: "AppleUSBAudioEngine:Focusrite:Scarlett 2i2 USB", name: "Scarlett 2i2 USB", uid: "AppleUSBAudioEngine:Focusrite:Scarlett 2i2 USB")

struct MicrophoneSelectionTests {

    // MARK: - Resolution

    @Test func noSavedChoiceFollowsTheSystemDefault() {
        #expect(MicrophoneResolution.resolve(savedUID: nil, savedName: nil, devices: [builtIn, scarlett]) == .systemDefault)
        // "" is how `selectedMicrophoneId` has always encoded "Default".
        #expect(MicrophoneResolution.resolve(savedUID: "", savedName: "Scarlett 2i2 USB", devices: [builtIn, scarlett]) == .systemDefault)
    }

    @Test func savedChoiceThatIsConnectedIsUsed() {
        let resolution = MicrophoneResolution.resolve(savedUID: scarlett.uid, savedName: "Old name", devices: [builtIn, scarlett])
        // The connected device wins over the saved name, so a renamed device shows its current name.
        #expect(resolution == .chosen(scarlett))
        #expect(resolution.missingChoice == nil)
    }

    @Test func savedChoiceThatIsUnpluggedIsKeptWithItsName() {
        let resolution = MicrophoneResolution.resolve(savedUID: scarlett.uid, savedName: "Scarlett 2i2 USB", devices: [builtIn])
        #expect(resolution == .chosenNotConnected(MicrophoneChoice(uid: scarlett.uid, name: "Scarlett 2i2 USB")))
        #expect(resolution.missingChoice?.name == "Scarlett 2i2 USB")
    }

    @Test func unpluggedChoiceSavedWithoutANameFallsBackToItsUID() {
        // Builds before this feature saved only the UID.
        let resolution = MicrophoneResolution.resolve(savedUID: scarlett.uid, savedName: "", devices: [])
        #expect(resolution.missingChoice == MicrophoneChoice(uid: scarlett.uid, name: scarlett.uid))
    }

    @Test func savedValuesFromEarlierBuildsStillMatch() {
        // `selectedMicrophoneId` always stored `AudioDevice.id`, which is the CoreAudio UID.
        #expect(builtIn.id == builtIn.uid)
        let resolution = MicrophoneResolution.resolve(savedUID: builtIn.id, savedName: nil, devices: [builtIn, scarlett])
        #expect(resolution == .chosen(builtIn))
    }

    // MARK: - Pinning

    @Test func onlyAChosenNonDefaultDeviceIsPinned() {
        #expect(MicrophoneResolution.chosen(scarlett).pinnedDeviceUID(systemDefaultUID: builtIn.uid) == scarlett.uid)
        // The chosen device is the current default: the default path already records it.
        #expect(MicrophoneResolution.chosen(builtIn).pinnedDeviceUID(systemDefaultUID: builtIn.uid) == nil)
        // An unknown default still pins the chosen device.
        #expect(MicrophoneResolution.chosen(scarlett).pinnedDeviceUID(systemDefaultUID: nil) == scarlett.uid)
        #expect(MicrophoneResolution.systemDefault.pinnedDeviceUID(systemDefaultUID: builtIn.uid) == nil)
        let missing = MicrophoneResolution.chosenNotConnected(MicrophoneChoice(uid: scarlett.uid, name: scarlett.name))
        #expect(missing.pinnedDeviceUID(systemDefaultUID: builtIn.uid) == nil)
    }

    // MARK: - Labels

    @Test func defaultDeviceNameIsTheCurrentDefaultsName() {
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: [builtIn, scarlett], systemDefaultUID: scarlett.uid) == "Scarlett 2i2 USB")
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: [builtIn], systemDefaultUID: "gone") == "audio.device.default".localized)
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: [builtIn], systemDefaultUID: nil) == "audio.device.default".localized)
    }

    @Test func labelsCarryTheDeviceNames() {
        let choice = MicrophoneChoice(uid: scarlett.uid, name: "Scarlett 2i2 USB")
        let defaultLabel = MicrophonePickerLabels.defaultOption(defaultDeviceName: "MacBook Pro Microphone")
        let notConnected = MicrophonePickerLabels.notConnected(choice)
        let notice = MicrophonePickerLabels.fallbackNotice(fallbackName: "MacBook Pro Microphone", choice: choice)

        #expect(defaultLabel == "microphone.picker.default".localized(arguments: "MacBook Pro Microphone"))
        #expect(defaultLabel.contains("MacBook Pro Microphone"))
        #expect(notConnected.contains("Scarlett 2i2 USB"))
        #expect(notice.contains("MacBook Pro Microphone"))
        #expect(notice.contains("Scarlett 2i2 USB"))
        // Every key is present in the Base table, not echoed back as the key itself.
        #expect(!defaultLabel.hasPrefix("microphone.picker"))
        #expect(!notConnected.hasPrefix("microphone.picker"))
        #expect(!notice.hasPrefix("microphone.picker"))
    }

    // MARK: - Level bar

    @Test func idleLevelLightsNoSegments() {
        #expect(MicrophoneLevelMeter.litSegments(level: 0, segmentCount: 24) == 0)
        #expect(MicrophoneLevelMeter.litSegments(level: -1, segmentCount: 24) == 0)
        #expect(MicrophoneLevelMeter.litSegments(level: .nan, segmentCount: 24) == 0)
        #expect(MicrophoneLevelMeter.litSegments(level: 0.5, segmentCount: 0) == 0)
    }

    @Test func levelLightsAProportionOfSegments() {
        #expect(MicrophoneLevelMeter.litSegments(level: 0.01, segmentCount: 24) == 1)
        #expect(MicrophoneLevelMeter.litSegments(level: 0.5, segmentCount: 24) == 12)
        #expect(MicrophoneLevelMeter.litSegments(level: 1, segmentCount: 24) == 24)
        #expect(MicrophoneLevelMeter.litSegments(level: 3, segmentCount: 24) == 24)
    }

    // MARK: - Routing store

    @Test func routingStoreRoundTrips() {
        let store = MicrophoneRoutingStore()
        #expect(store.preferredDeviceUID == nil)
        store.preferredDeviceUID = scarlett.uid
        #expect(store.preferredDeviceUID == scarlett.uid)
        store.preferredDeviceUID = nil
        #expect(store.preferredDeviceUID == nil)
    }
}
