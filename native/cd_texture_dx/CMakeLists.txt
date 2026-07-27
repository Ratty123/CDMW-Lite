cmake_minimum_required(VERSION 3.24)
project(cd_texture_dx LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(CDMW_FETCH_DIRECTXTEX "Fetch Microsoft DirectXTex when it is not provided by DirectXTex_DIR" ON)
set(CDMW_DIRECTXTEX_COMMIT "bf256afaed1c789ddd444fb45105ffbcab283efe" CACHE STRING "Pinned DirectXTex commit")

set(BUILD_TOOLS OFF CACHE BOOL "Build DirectXTex command-line tools" FORCE)
set(BUILD_SAMPLE OFF CACHE BOOL "Build DirectXTex samples" FORCE)
set(BC_USE_OPENMP ON CACHE BOOL "Build DirectXTex BC6H/BC7 codecs with OpenMP" FORCE)
find_package(OpenMP REQUIRED COMPONENTS CXX)

find_package(DirectXTex CONFIG QUIET)
if(NOT DirectXTex_FOUND AND CDMW_FETCH_DIRECTXTEX)
    include(FetchContent)
    # DirectXTex builds its BC6H/BC7 shaders with a custom command that names
    # "CompileShaders.cmd" bare and relies on WORKING_DIRECTORY to find it. CMake 3.31
    # resolves that program against PATH only, never the working directory, so the step
    # fails with "no such file or directory" on a clean tree. Upstream main still carries
    # the same invocation, so moving the pin does not help, and MSBuild always runs a
    # custom build step on its first pass, so pre-generating the outputs is not enough.
    #
    # Take the shader build over instead, through the supported USE_PREBUILT_SHADERS
    # option. That needs the shaders to exist before DirectXTex is configured, so the
    # source is populated first: SOURCE_SUBDIR names a directory with no CMakeLists.txt,
    # which makes FetchContent_MakeAvailable download without calling add_subdirectory.
    FetchContent_Declare(
        directxtex
        GIT_REPOSITORY https://github.com/microsoft/DirectXTex.git
        GIT_TAG ${CDMW_DIRECTXTEX_COMMIT}
        UPDATE_DISCONNECTED TRUE
        SOURCE_SUBDIR cdmw-populate-only
    )
    FetchContent_MakeAvailable(directxtex)

    set(CDMW_SHADER_DIR "${directxtex_SOURCE_DIR}/DirectXTex/Shaders")
    set(CDMW_SHADER_SCRIPT "${CDMW_SHADER_DIR}/CompileShaders.cmd")
    set(CDMW_SHADER_OUTPUT "${directxtex_BINARY_DIR}/Shaders/Compiled")
    set(CDMW_SHADER_SENTINEL "${CDMW_SHADER_OUTPUT}/BC6HEncode_EncodeBlockCS.inc")
    set(CDMW_SHADERS_READY OFF)

    if(WIN32 AND EXISTS "${CDMW_SHADER_SCRIPT}")
        # Mirrors the hints DirectXTex uses for its own legacy shader compiler lookup.
        find_program(CDMW_FXC_TOOL FXC.EXE
            HINTS "C:/Program Files (x86)/Windows Kits/10/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64"
                  "C:/Program Files (x86)/Windows Kits/10/bin/${CMAKE_SYSTEM_VERSION}/x64"
                  "C:/Program Files (x86)/Windows Kits/10/bin/${CMAKE_SYSTEM_VERSION}.0/x64")
        if(NOT CDMW_FXC_TOOL)
            message(FATAL_ERROR
                "FXC.EXE was not found. Install the Windows SDK Desktop C++ workload, "
                "or set CDMW_FXC_TOOL to the legacy shader compiler.")
        endif()

        if(NOT EXISTS "${CDMW_SHADER_SENTINEL}"
           OR "${CDMW_SHADER_SCRIPT}" IS_NEWER_THAN "${CDMW_SHADER_SENTINEL}")
            message(STATUS "Building DirectXTex HLSL shaders with ${CDMW_FXC_TOOL}")
            file(MAKE_DIRECTORY "${CDMW_SHADER_OUTPUT}")
            # Routed through cmd.exe with an explicit script path, which is exactly what
            # the upstream invocation cannot do.
            execute_process(
                COMMAND ${CMAKE_COMMAND} -E env
                    "CompileShadersOutput=${CDMW_SHADER_OUTPUT}"
                    "LegacyShaderCompiler=${CDMW_FXC_TOOL}"
                    cmd /c "${CDMW_SHADER_SCRIPT}"
                WORKING_DIRECTORY "${CDMW_SHADER_DIR}"
                RESULT_VARIABLE CDMW_SHADER_RESULT
                OUTPUT_VARIABLE CDMW_SHADER_LOG
                ERROR_VARIABLE CDMW_SHADER_LOG
            )
            if(NOT CDMW_SHADER_RESULT EQUAL 0 OR NOT EXISTS "${CDMW_SHADER_SENTINEL}")
                message(FATAL_ERROR
                    "Building DirectXTex HLSL shaders failed (${CDMW_SHADER_RESULT}).\n${CDMW_SHADER_LOG}")
            endif()
        endif()
        set(CDMW_SHADERS_READY ON)
    endif()

    if(CDMW_SHADERS_READY)
        set(COMPILED_SHADERS "${CDMW_SHADER_OUTPUT}" CACHE PATH "Externally built DirectXTex shaders" FORCE)
        set(USE_PREBUILT_SHADERS ON CACHE BOOL "Use externally built HLSL shaders" FORCE)
    endif()
    add_subdirectory("${directxtex_SOURCE_DIR}" "${directxtex_BINARY_DIR}")
endif()

set(TEXTURE_DX_OWNER_SOURCES
    src/common.cpp
    src/preview.cpp
    src/encode.cpp
    src/self_test.cpp
    src/texture_tool.h
    src/texture_tool_internal.h
)

add_executable(cd-texture-dx src/main.cpp ${TEXTURE_DX_OWNER_SOURCES})
target_compile_definitions(cd-texture-dx PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(cd-texture-dx PRIVATE windowscodecs OpenMP::OpenMP_CXX)

if(TARGET Microsoft::DirectXTex)
    target_link_libraries(cd-texture-dx PRIVATE Microsoft::DirectXTex)
elseif(TARGET DirectXTex)
    target_link_libraries(cd-texture-dx PRIVATE DirectXTex)
else()
    message(FATAL_ERROR "DirectXTex was not found. Set DirectXTex_DIR or enable CDMW_FETCH_DIRECTXTEX.")
endif()
