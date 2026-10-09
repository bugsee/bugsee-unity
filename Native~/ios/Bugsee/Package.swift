// swift-tools-version:5.9
import PackageDescription

// Local Swift package used by the Unity iOS post-process.
// Native SDK pin: https://github.com/bugsee/bugsee-cocoa (nextgen, see Tools~/versions.env).
//
// Populate Bugsee.xcframework via:
//   IOS_XCFRAMEWORK_PATH=... ./Tools~/scripts/update-native-sdks.sh

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
