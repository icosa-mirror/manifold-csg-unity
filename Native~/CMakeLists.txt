cmake_minimum_required(VERSION 3.24)
project(manifold_unity_mobile LANGUAGES CXX)

if(NOT ANDROID AND NOT CMAKE_SYSTEM_NAME STREQUAL "iOS")
    message(FATAL_ERROR "This build supports Android and iOS only")
endif()

# Keep the native ABI in sync with Runtime/ManifoldNative.cs and desktop binaries.
include(FetchContent)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(MANIFOLD_TEST OFF CACHE BOOL "" FORCE)
set(MANIFOLD_CBIND ON CACHE BOOL "" FORCE)
set(MANIFOLD_CROSS_SECTION ON CACHE BOOL "" FORCE)
set(MANIFOLD_PAR OFF CACHE BOOL "" FORCE)
set(MANIFOLD_PYBIND OFF CACHE BOOL "" FORCE)
set(MANIFOLD_USE_BUILTIN_CLIPPER2 ON CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
FetchContent_Declare(manifold
    URL https://codeload.github.com/elalish/manifold/zip/refs/tags/v3.5.3
    URL_HASH SHA256=7e29242783102b9847b4693effd5b6d770916a7b010086a8b346e9aefbc8cc83
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(manifold)

if(ANDROID)
    # Compile the C bindings as the sole shared library. Manifold, Clipper2 and
    # the C++ runtime are static, so Unity needs no companion .so files.
    get_target_property(c_sources manifoldc SOURCES)
    list(TRANSFORM c_sources PREPEND "${manifold_SOURCE_DIR}/bindings/c/")
    add_library(manifold_unity SHARED ${c_sources})
    target_include_directories(manifold_unity PRIVATE
        "${manifold_SOURCE_DIR}/bindings/c/include")
    target_link_libraries(manifold_unity PRIVATE manifold)
    target_compile_features(manifold_unity PRIVATE cxx_std_17)
    get_target_property(c_flags manifoldc COMPILE_OPTIONS)
    target_compile_options(manifold_unity PRIVATE ${c_flags})
    set_target_properties(manifold_unity PROPERTIES OUTPUT_NAME manifoldc)
    # Also support Android devices with 16 KB memory pages on older NDKs.
    target_link_options(manifold_unity PRIVATE
        "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
else()
    # Unity links one archive into the generated Xcode project. Include the
    # transitive static dependencies rather than shipping manifoldc alone.
    add_custom_command(OUTPUT "${CMAKE_BINARY_DIR}/libmanifoldc.a"
        COMMAND xcrun libtool -static -o "${CMAKE_BINARY_DIR}/libmanifoldc.a"
            "$<TARGET_FILE:manifoldc>" "$<TARGET_FILE:manifold>"
            "$<TARGET_FILE:Clipper2>"
        DEPENDS manifoldc manifold Clipper2 VERBATIM)
    add_custom_target(manifold_unity ALL DEPENDS "${CMAKE_BINARY_DIR}/libmanifoldc.a")
endif()
