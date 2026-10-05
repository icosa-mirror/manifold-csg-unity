# Mobile native builds

GitHub Actions builds these libraries on Linux (Android) and macOS (iOS). See
`.github/workflows/mobile-native.yml`. Download `mobile-native-plugins` from a successful run and
copy its `Plugins/` folder into the package root, preserving importer metadata.

The native version is pinned to Manifold v3.5.3 with a SHA-256 checked source archive. Clipper2 is
pinned by that version's upstream CMake configuration. Parallel Manifold/TBB is disabled, matching
the upstream default. Build inputs and outputs are kept under the ignored `Build/` directory.

## Local builds

Install Python 3.9+, CMake 3.24+, Ninja and Git. Network access is needed on the first configure.
Use Android NDK r28b (28.1.13356709), as CI does, or a compatible newer NDK.

```powershell
python 'Native~/build.py' android --abi arm64-v8a --ndk 'C:/path/to/android-ndk'
python 'Native~/build.py' android --abi armeabi-v7a --ndk 'C:/path/to/android-ndk'
python 'Native~/build.py' android --abi x86_64 --ndk 'C:/path/to/android-ndk'
```

On macOS with Xcode and its iPhoneOS SDK installed:

```sh
python3 'Native~/build.py' ios
```

The default output is `Build/mobile/Plugins/`. Use `--output .` to stage into the package itself.
Folder and plugin `.meta` files have stable GUIDs derived from their package paths. Android plugins
are enabled only for Android with the matching CPU; the iOS archive is enabled only for iOS ARM64
and declares the `libc++.tbd` dependency. Neither is enabled in the Editor.

Android builds one `libmanifoldc.so` per ABI, statically linking the core, Clipper2 and libc++.
Verification checks the C# P/Invoke exports, ELF load segment alignment (at least 16 KB) and the
absence of non-system shared dependencies. iOS combines all three static archives into one
`libmanifoldc.a`, checks the P/Invoke symbols and links all objects against the iPhoneOS SDK to
check for unresolved dependencies. It targets iOS 13+ ARM64 devices; Simulator is not
included. Validate boolean operations in an IL2CPP player on each target before release.
