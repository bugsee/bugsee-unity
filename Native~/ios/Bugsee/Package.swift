// swift-tools-version:5.9
import PackageDescription

// Local stub kept for offline / UseRemoteSpm = false.
// Default iOS integration is remote SPM: https://github.com/bugsee/spm @ 7.0.0-beta5
// (see Tools~/versions.env and Editor/BugseeIosSpmPostProcess.cs).

let package = Package(
    name: "Bugsee",
    platforms: [
        .iOS(.v15),
        .tvOS(.v15),
        .visionOS(.v1)
    ],
    products: [
        .library(name: "Bugsee", targets: ["Bugsee"])
    ],
    targets: [
        .binaryTarget(
            name: "Bugsee",
            path: "Bugsee.xcframework"
        )
    ]
)
