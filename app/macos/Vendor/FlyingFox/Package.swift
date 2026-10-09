// swift-tools-version:6.0

// Vendored copy of FlyingFox 0.27.1 for HyperWhisper. See VENDORED.md.
// The upstream test targets are not vendored, so they are removed here.

import PackageDescription

let package = Package(
    name: "FlyingFox",
    platforms: [
        .macOS(.v10_15), .iOS(.v13), .tvOS(.v13), .watchOS(.v8)
    ],
    products: [
        .library(
            name: "FlyingFox",
            targets: ["FlyingFox"]
        ),
        .library(
            name: "FlyingSocks",
            targets: ["FlyingSocks"]
        )
    ],
    targets: [
        .target(
            name: "FlyingFox",
            dependencies: ["FlyingSocks"],
            path: "FlyingFox/Sources",
            swiftSettings: .upcomingFeatures
        ),
        .target(
            name: "FlyingSocks",
            dependencies: [.target(name: "CSystemLinux", condition: .when(platforms: [.linux, .android]))],
            path: "FlyingSocks/Sources",
            swiftSettings: .upcomingFeatures
        ),
        .target(
            name: "CSystemLinux",
            path: "CSystemLinux"
        )
    ]
)

extension Array where Element == SwiftSetting {

    static var upcomingFeatures: [SwiftSetting] {
        [
            .enableUpcomingFeature("ExistentialAny"),
            .swiftLanguageMode(.v6)
        ]
    }
}
