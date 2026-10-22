//
//  MicrophoneSelectionTests.swift
//  hyperwhisperTests
//
//  The microphone picker keeps ONE saved choice for HyperWhisper, separate from the
//  macOS default input. These pin how that choice resolves against the connected
//  devices: present, absent (kept, with a fallback to the automatic pick), and "Default",
//  and how the automatic pick passes over a virtual default input (#2408).
//

import Foundation
import Testing
@testable import HyperWhisper

private let builtIn = AudioDevice(id: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone", uid: "BuiltInMicrophoneDevice")
private let scarlett = AudioDevice(id: "AppleUSBAudioEngine:Focusrite:Scarlett 2i2 USB", name: "Scarlett 2i2 USB", uid: "AppleUSBAudioEngine:Focusrite:Scarlett 2i2 USB")
private let blackHole = AudioDevice(id: "BlackHole2ch_UID", name: "BlackHole 2ch", uid: "BlackHole2ch_UID")
private let loopback = AudioDevice(id: "com.rogueamoeba.Loopback:1", name: "Loopback Audio", uid: "com.rogueamoeba.Loopback:1")
private let krisp = AudioDevice(id: "KrispMicrophone_UID", name: "Krisp Microphone", uid: "KrispMicrophone_UID")

/// The device a capture opens first instead of the macOS default, or `nil` for the default.
private func pinnedUID(_ resolution: MicrophoneResolution, systemDefaultUID: String?, devices: [AudioDevice]) -> String? {
    MicrophoneCapturePlan.resolve(resolution, devices: devices, systemDefaultUID: systemDefaultUID).deviceUIDs.first
}

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
        let devices = [builtIn, scarlett]
        #expect(pinnedUID(MicrophoneResolution.chosen(scarlett), systemDefaultUID: builtIn.uid, devices: devices) == scarlett.uid)
        // The chosen device is the current default: the default path already records it.
        #expect(pinnedUID(MicrophoneResolution.chosen(builtIn), systemDefaultUID: builtIn.uid, devices: devices) == nil)
        // An unknown default still pins the chosen device.
        #expect(pinnedUID(MicrophoneResolution.chosen(scarlett), systemDefaultUID: nil, devices: devices) == scarlett.uid)
        #expect(pinnedUID(MicrophoneResolution.systemDefault, systemDefaultUID: builtIn.uid, devices: devices) == nil)
        let missing = MicrophoneResolution.chosenNotConnected(MicrophoneChoice(uid: scarlett.uid, name: scarlett.name))
        #expect(pinnedUID(missing, systemDefaultUID: builtIn.uid, devices: [builtIn]) == nil)
    }

    // MARK: - Virtual inputs (#2408)

    @Test func virtualDefaultWithAPhysicalInputPicksThePhysicalOne() {
        // CoreAudio order: the virtual device first, so "first non-virtual" is not just "first".
        let devices = [blackHole, builtIn, scarlett]
        let pick = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: blackHole.uid)
        #expect(pick.device == builtIn)
        #expect(pick.skippedVirtualDefault == blackHole)
        #expect(pinnedUID(MicrophoneResolution.systemDefault, systemDefaultUID: blackHole.uid, devices: devices) == builtIn.uid)
    }

    @Test func onlyVirtualInputsKeepTheDefault() {
        let devices = [blackHole, loopback]
        let pick = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: loopback.uid)
        #expect(pick.device == loopback)
        #expect(pick.skippedVirtualDefault == nil)
        #expect(pinnedUID(MicrophoneResolution.systemDefault, systemDefaultUID: loopback.uid, devices: devices) == nil)
    }

    @Test func aVirtualMicrophoneChosenByHandIsNeverOverridden() {
        let devices = [builtIn, blackHole]
        // Chosen while another device is the default: pinned.
        #expect(MicrophoneResolution.resolve(savedUID: blackHole.uid, savedName: nil, devices: devices) == .chosen(blackHole))
        #expect(pinnedUID(MicrophoneResolution.chosen(blackHole), systemDefaultUID: builtIn.uid, devices: devices) == blackHole.uid)
        // Chosen and also the default: the default path records it, nothing replaces it.
        #expect(pinnedUID(MicrophoneResolution.chosen(blackHole), systemDefaultUID: blackHole.uid, devices: devices) == nil)
    }

    @Test func aPhysicalDefaultIsUnchanged() {
        let devices = [blackHole, builtIn, scarlett]
        let pick = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: scarlett.uid)
        #expect(pick.device == scarlett)
        #expect(pick.skippedVirtualDefault == nil)
        #expect(pick.pinnedDeviceUID == nil)
        #expect(pinnedUID(MicrophoneResolution.systemDefault, systemDefaultUID: scarlett.uid, devices: devices) == nil)
    }

    @Test func anUnpluggedChoiceWithAVirtualDefaultUsesAPhysicalStandIn() {
        let devices = [blackHole, builtIn]
        let resolution = MicrophoneResolution.resolve(savedUID: scarlett.uid, savedName: scarlett.name, devices: devices)
        #expect(resolution.missingChoice != nil)
        #expect(pinnedUID(resolution, systemDefaultUID: blackHole.uid, devices: devices) == builtIn.uid)
        // The fallback notice names the stand-in, not the virtual default.
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: devices, systemDefaultUID: blackHole.uid) == builtIn.name)
    }

    @Test func anUnknownDefaultIsLeftToMacOS() {
        let pick = AutomaticMicrophonePick.resolve(devices: [blackHole, builtIn], systemDefaultUID: nil)
        #expect(pick.device == nil)
        #expect(pick.pinnedDeviceUID == nil)
        #expect(AutomaticMicrophonePick.resolve(devices: [builtIn], systemDefaultUID: "gone").device == nil)
    }

    @Test func virtualDevicesAreDetectedByName() {
        let virtualNames = [
            "BlackHole 2ch",
            "BlackHole 16ch",
            "Soundflower (2ch)",
            "Loopback Audio",
            "Loopback Audio 2",
            "VB-Audio Virtual Cable",
            "VB-Cable",
            "CABLE Output (VB-Audio Virtual Cable)",
            // Windows WaveIn cuts names at 31 characters; the list still matches.
            "CABLE Output (VB-Audio Virtual ",
            "CABLE-A Output (VB-Audio Cable A)",
            "CABLE-B Output (VB-Audio Cable B)",
            "VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)",
            "Line 1 (Virtual Audio Cable)",
            "ZoomAudioDevice",
            "Microsoft Teams Audio",
            "Background Music",
            "eqMac",
            "Stereo Mix (Realtek(R) Audio)",
            "What U Hear (Sound Blaster)",
            "Wave Out Mix",
            // Case-insensitive.
            "blackhole 2ch",
            "STEREO MIX",
        ]
        for name in virtualNames {
            #expect(VirtualMicrophoneDetector.isVirtual(name: name), "\(name) should be virtual")
        }
    }

    @Test func voiceProcessingAndPhysicalDevicesAreNotVirtual() {
        let notVirtual = [
            "MacBook Pro Microphone",
            "Scarlett 2i2 USB",
            "AirPods Pro",
            // These carry the user's own processed voice.
            "Krisp Microphone",
            "NVIDIA Broadcast",
            "Elgato Wave Link MicrophoneFX",
            "Aggregate Device",
            "",
        ]
        for name in notVirtual {
            #expect(!VirtualMicrophoneDetector.isVirtual(name: name), "\(name) should not be virtual")
        }
    }

    @Test func aKrispDefaultIsKeptEvenWithAPhysicalMicrophone() {
        let pick = AutomaticMicrophonePick.resolve(devices: [builtIn, krisp], systemDefaultUID: krisp.uid)
        #expect(pick.device == krisp)
        #expect(pick.skippedVirtualDefault == nil)
    }

    @Test func virtualDevicesStayInThePickerList() {
        // The automatic pick only chooses among the devices; it never removes any.
        let devices = [blackHole, builtIn]
        _ = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: blackHole.uid)
        #expect(MicrophoneResolution.resolve(savedUID: blackHole.uid, savedName: nil, devices: devices) == .chosen(blackHole))
    }

    // MARK: - Capture plan (open-failure fallbacks, #2408)

    @Test func aSkippedVirtualDefaultIsNeverTheFallback() {
        // BlackHole is the default and the built-in mic cannot open: the next physical
        // input is tried, and then the start fails rather than record BlackHole.
        let devices = [blackHole, builtIn, loopback, scarlett]
        let plan = MicrophoneCapturePlan.resolve(chosenUID: nil, devices: devices, systemDefaultUID: blackHole.uid)
        #expect(plan.deviceUIDs == [builtIn.uid, scarlett.uid])
        #expect(!plan.fallsBackToSystemDefault)
        #expect(MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: blackHole.uid) == plan)
    }

    @Test func anUnpluggedChoiceWithAVirtualDefaultNeverFallsBackToIt() {
        let devices = [blackHole, builtIn]
        let resolution = MicrophoneResolution.resolve(savedUID: scarlett.uid, savedName: scarlett.name, devices: devices)
        let plan = MicrophoneCapturePlan.resolve(resolution, devices: devices, systemDefaultUID: blackHole.uid)
        #expect(plan.deviceUIDs == [builtIn.uid])
        #expect(!plan.fallsBackToSystemDefault)
    }

    @Test func aChosenMicrophoneThatFailsTriesTheStandInsBeforeAVirtualDefault() {
        let devices = [blackHole, builtIn, scarlett]
        let plan = MicrophoneCapturePlan.resolve(.chosen(scarlett), devices: devices, systemDefaultUID: blackHole.uid)
        #expect(plan.deviceUIDs == [scarlett.uid, builtIn.uid])
        #expect(!plan.fallsBackToSystemDefault)
    }

    @Test func aChosenMicrophoneWithAPhysicalDefaultKeepsTheDefaultFallback() {
        let devices = [builtIn, scarlett, blackHole]
        let plan = MicrophoneCapturePlan.resolve(.chosen(scarlett), devices: devices, systemDefaultUID: builtIn.uid)
        #expect(plan == MicrophoneCapturePlan(deviceUIDs: [scarlett.uid], fallsBackToSystemDefault: true))
    }

    @Test func aVirtualMicrophoneChosenByHandKeepsItsBehaviour() {
        let devices = [builtIn, blackHole]
        // Chosen, not the default: opened first, the default is still the fallback.
        #expect(MicrophoneCapturePlan.resolve(.chosen(blackHole), devices: devices, systemDefaultUID: builtIn.uid)
            == MicrophoneCapturePlan(deviceUIDs: [blackHole.uid], fallsBackToSystemDefault: true))
        // Chosen and the default: recorded from the default, nothing skips it.
        #expect(MicrophoneCapturePlan.resolve(.chosen(blackHole), devices: devices, systemDefaultUID: blackHole.uid) == .systemDefault)
    }

    @Test func anAllVirtualMacStillRecordsFromTheDefault() {
        let devices = [blackHole, loopback]
        #expect(MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: blackHole.uid) == .systemDefault)
    }

    @Test func aPhysicalDefaultNeedsNoPlan() {
        let devices = [blackHole, builtIn, scarlett]
        #expect(MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: scarlett.uid) == .systemDefault)
        #expect(MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: nil) == .systemDefault)
    }

    @Test func aRestartOfASkippingCaptureAvoidsTheVirtualDefaultWhenNoPhysicalInputIsLeft() {
        // The physical stand-in was unplugged mid-recording: nothing to skip BlackHole
        // for any more, but this capture still must not move onto it.
        let restart = MicrophoneCapturePlan.resolve(
            chosenUID: nil, devices: [blackHole], systemDefaultUID: blackHole.uid, avoidingVirtualDefault: true
        )
        #expect(restart == MicrophoneCapturePlan(deviceUIDs: [], fallsBackToSystemDefault: false))
        // An unknown default is not trusted either.
        #expect(!MicrophoneCapturePlan.resolve(
            chosenUID: nil, devices: [blackHole], systemDefaultUID: nil, avoidingVirtualDefault: true
        ).fallsBackToSystemDefault)
        // The user switched the macOS default to a physical mic meanwhile: that is fine.
        #expect(MicrophoneCapturePlan.resolve(
            chosenUID: nil, devices: [blackHole, scarlett], systemDefaultUID: scarlett.uid, avoidingVirtualDefault: true
        ) == .systemDefault)
        // Without the flag (no virtual default was skipped), restarts keep the default.
        #expect(MicrophoneCapturePlan.resolve(chosenUID: nil, devices: [blackHole], systemDefaultUID: blackHole.uid) == .systemDefault)
    }

    @Test func thePlanCarriesTheVirtualDefaultItSkipped() {
        let devices = [blackHole, builtIn, scarlett]
        let skipping = MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: blackHole.uid)
        #expect(skipping.virtualDefaultSkip?.skippedVirtualDefault == blackHole)
        #expect(skipping.virtualDefaultSkip?.device == builtIn)
        #expect(MicrophoneCapturePlan.resolve(.systemDefault, devices: devices, systemDefaultUID: scarlett.uid).virtualDefaultSkip == nil)
    }

    @Test func routeFlagsFollowThePlan() {
        #expect(MicrophoneCaptureRoute.systemDefault.primaryDeviceID == nil)
        #expect(!MicrophoneCaptureRoute.systemDefault.avoidsVirtualDefault)
        let skipping = MicrophoneCaptureRoute(deviceIDs: [52, 73], fallsBackToSystemDefault: false)
        #expect(skipping.primaryDeviceID == 52)
        #expect(skipping.avoidsVirtualDefault)
    }

    // MARK: - Labels

    @Test func defaultDeviceNameNamesTheAutomaticPick() {
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: [blackHole, builtIn], systemDefaultUID: blackHole.uid) == "MacBook Pro Microphone")
        #expect(MicrophonePickerLabels.defaultDeviceName(devices: [blackHole], systemDefaultUID: blackHole.uid) == "BlackHole 2ch")
    }

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
