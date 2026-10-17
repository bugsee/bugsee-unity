plugins {
    id("com.android.application")
}

apply(from = "setupSymbols.gradle")
apply(from = "../shared/keepUnitySymbols.gradle")

dependencies {
    implementation(project(":unityLibrary"))
}

android {
    namespace = "com.bugsee.minigame"
    ndkPath = "/Applications/Unity/Hub/Editor/6000.0.84f1/PlaybackEngines/AndroidPlayer/NDK"
    ndkVersion = "27.2.12479018"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "com.bugsee.minigame"
        versionName = "0.1.0"
        minSdk = 23
        targetSdk = 36
        versionCode = 2

        ndk {
            abiFilters += listOf("armeabi-v7a")
            debugSymbolLevel = "none"
        }
    }

    lint {
        abortOnError = false
    }

    androidResources {
        ignoreAssetsPattern = "!.svn:!.git:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~"
        noCompress += listOf(".unity3d", ".ress", ".resource", ".obb", ".bundle", ".unityexp")
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            isJniDebuggable = true
            signingConfig = signingConfigs.getByName("debug")
        }

        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    bundle {
        language {
            enableSplit = false
        }
        density {
            enableSplit = false
        }
        abi {
            enableSplit = true
        }
        texture {
            enableSplit = true
        }
    }
}
