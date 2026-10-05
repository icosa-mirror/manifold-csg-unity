"""Build and stage mobile native plugins, including Unity importer settings."""

import argparse
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import uuid


ROOT = Path(__file__).resolve().parent.parent
ANDROID_CPUS = {"arm64-v8a": "ARM64", "armeabi-v7a": "ARMv7", "x86_64": "x86_64"}


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)


def capture(*args):
    return subprocess.check_output([str(arg) for arg in args], text=True)


def guid(relative):
    return uuid.uuid5(uuid.NAMESPACE_URL, f"com.johannhotzel.manifoldcsg/{relative}").hex


def stage(binary, relative, output, target, cpu):
    destination = output / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(binary, destination)
    parent = Path(relative).parent
    while parent != Path("."):
        # Plugins.meta is already supplied by the package.
        if parent != Path("Plugins"):
            (output / f"{parent.as_posix()}.meta").write_text(
                f"fileFormatVersion: 2\nguid: {guid(parent.as_posix())}\n"
                "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
                "  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
                encoding="utf-8",
            )
        parent = parent.parent
    extra = "        AddToEmbeddedBinaries: false\n        CompileFlags:\n        FrameworkDependencies: libc++.tbd\n" if target == "iPhone" else ""
    Path(f"{destination}.meta").write_text(
        f"fileFormatVersion: 2\nguid: {guid(relative)}\n"
        "PluginImporter:\n  externalObjects: {}\n  serializedVersion: 3\n"
        "  iconMap: {}\n  executionOrder: {}\n  defineConstraints: []\n"
        "  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n"
        "  validateReferences: 1\n  platformData:\n"
        "    Any:\n      enabled: 0\n      settings: {}\n"
        "    Editor:\n      enabled: 0\n      settings:\n"
        "        CPU: AnyCPU\n        DefaultValueInitialized: true\n        OS: AnyOS\n"
        f"    {target}:\n      enabled: 1\n      settings:\n        CPU: {cpu}\n{extra}"
        "  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
        encoding="utf-8",
    )
    if output.resolve() != ROOT:
        shutil.copy2(ROOT / "Plugins/THIRD_PARTY_LICENSES.txt", output / "Plugins/THIRD_PARTY_LICENSES.txt")
        shutil.copy2(ROOT / "Plugins/THIRD_PARTY_LICENSES.txt.meta", output / "Plugins/THIRD_PARTY_LICENSES.txt.meta")


def verify_symbols(nm, binary, android):
    flags = ["--dynamic", "--defined-only"] if android else ["-g", "-U"]
    symbols = capture(nm, *flags, binary)
    exported = {line.split()[-1].removeprefix("_") for line in symbols.splitlines() if line.split()}
    required = set(re.findall(r"extern\s+\w+\s+(manifold_\w+)\(",
                             (ROOT / "Runtime/ManifoldNative.cs").read_text(encoding="utf-8")))
    missing = required - exported
    if missing:
        raise RuntimeError(f"Missing C API symbols: {', '.join(sorted(missing))}")
    print(f"Verified {len(required)} P/Invoke exports in {binary.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("platform", choices=["android", "ios"])
    parser.add_argument("--abi", choices=ANDROID_CPUS, default="arm64-v8a")
    parser.add_argument("--ndk", default=os.environ.get("ANDROID_NDK_HOME") or os.environ.get("ANDROID_NDK_ROOT"))
    parser.add_argument("--output", type=Path, default=ROOT / "Build/mobile")
    parser.add_argument("--jobs", type=int, default=4)
    args = parser.parse_args()
    build = ROOT / "Build/native" / f"{args.platform}-{args.abi if args.platform == 'android' else 'arm64'}"
    config = ["cmake", "-S", ROOT / "Native~", "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release"]
    if args.platform == "android":
        if not args.ndk:
            parser.error("Set ANDROID_NDK_HOME or pass --ndk with an installed Android NDK")
        ndk = Path(args.ndk).resolve()
        toolchain = ndk / "build/cmake/android.toolchain.cmake"
        if not toolchain.is_file():
            parser.error(f"Android toolchain not found: {toolchain}")
        config += [f"-DCMAKE_TOOLCHAIN_FILE={toolchain}", f"-DANDROID_ABI={args.abi}",
                   "-DANDROID_PLATFORM=android-23", "-DANDROID_STL=c++_static",
                   "-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON"]
    else:
        if platform.system() != "Darwin":
            parser.error("iOS builds require macOS with Xcode")
        config += ["-DCMAKE_SYSTEM_NAME=iOS", "-DCMAKE_OSX_SYSROOT=iphoneos",
                   "-DCMAKE_OSX_ARCHITECTURES=arm64", "-DCMAKE_OSX_DEPLOYMENT_TARGET=13.0"]
    run(*config)
    run("cmake", "--build", build, "--target", "manifold_unity", "--parallel", args.jobs)
    output = args.output.resolve()
    if args.platform == "android":
        host = {"Windows": "windows-x86_64", "Linux": "linux-x86_64", "Darwin": "darwin-x86_64"}[platform.system()]
        suffix = ".exe" if platform.system() == "Windows" else ""
        tools = ndk / f"toolchains/llvm/prebuilt/{host}/bin"
        binary = build / "libmanifoldc.so"
        verify_symbols(tools / f"llvm-nm{suffix}", binary, True)
        headers = capture(tools / f"llvm-readelf{suffix}", "--program-headers", "--wide", binary)
        loads = [line.split() for line in headers.splitlines() if line.strip().startswith("LOAD ")]
        if not loads or any(int(fields[-1], 16) < 16384 for fields in loads):
            raise RuntimeError("Android ELF load segments must have at least 16 KB alignment")
        dynamic = capture(tools / f"llvm-readelf{suffix}", "--dynamic", binary)
        dependencies = set(re.findall(r"Shared library: \[(.*?)\]", dynamic))
        unexpected = dependencies - {"libc.so", "libm.so", "libdl.so", "liblog.so"}
        if unexpected:
            raise RuntimeError(f"Unexpected shared dependencies: {', '.join(sorted(unexpected))}")
        print(f"Verified 16 KB alignment; shared dependencies: {', '.join(sorted(dependencies))}")
        run(tools / f"llvm-strip{suffix}", "--strip-unneeded", binary)
        stage(binary, f"Plugins/Android/{args.abi}/libmanifoldc.so", output, "Android", ANDROID_CPUS[args.abi])
    else:
        binary = build / "libmanifoldc.a"
        verify_symbols(capture("xcrun", "--find", "nm").strip(), binary, False)
        if capture("xcrun", "lipo", "-archs", binary).strip() != "arm64":
            raise RuntimeError("iOS archive must contain only the ARM64 device architecture")
        # Force every object into a link against the device SDK to catch missing
        # transitive dependencies that an archive symbol check cannot detect.
        sdk = capture("xcrun", "--sdk", "iphoneos", "--show-sdk-path").strip()
        run("xcrun", "--sdk", "iphoneos", "clang++", "-arch", "arm64",
            "-isysroot", sdk, "-miphoneos-version-min=13.0", "-dynamiclib",
            "-Wl,-all_load", binary, "-o", build / "verify-ios.dylib")
        print("Verified iOS ARM64 archive links with the iPhoneOS SDK")
        stage(binary, "Plugins/iOS/libmanifoldc.a", output, "iPhone", "ARM64")
    print(f"Staged {args.platform} plugin in {output}")


if __name__ == "__main__":
    main()
