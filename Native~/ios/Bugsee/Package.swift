// swift-tools-version:5.9
import PackageDescription

// Local Swift package used by the Unity iOS post-process until nextgen Bugsee
// is published to https://github.com/bugsee/spm.
//
// Populate Bugsee.xcframework via:
//   ./Tools~/scripts/update-native-sdks.sh

let package = Package(
    name: "Bugsee",
    platforms: [
        .iOS(.v13),
        .tvOS(.v13),
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
