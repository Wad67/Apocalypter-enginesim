cmake_minimum_required(VERSION 3.16)
project(enginesim_native CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if (NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif ()

# upstream is MSVC code; mingw already defines __forceinline
if (NOT MSVC AND NOT WIN32)
    add_compile_definitions("__forceinline=inline __attribute__((always_inline))")
endif ()

# ...and relies on MSVC's transitive standard headers
if (NOT MSVC)
    add_compile_options(-Wno-changes-meaning "SHELL:-include cstring" "SHELL:-include cstdint" "SHELL:-include algorithm" "SHELL:-include cmath")
endif ()

set(ES ${CMAKE_CURRENT_SOURCE_DIR}/engine-sim)
set(SCS ${ES}/dependencies/submodules/simple-2d-constraint-solver)

file(GLOB SCS_SOURCES ${SCS}/src/*.cpp)
add_library(scs STATIC ${SCS_SOURCES})
target_include_directories(scs PUBLIC ${SCS}/include)

# engine-sim core (the simulator + synthesizer, no app / UI / scripting)
file(GLOB ES_SOURCES ${ES}/src/*.cpp)
# The impulse-response convolution is a long float dot product per sample; strict float
# ordering keeps GCC from vectorising it. Reordering a sum is harmless for audio.
if (NOT MSVC)
    set_source_files_properties(${ES}/src/convolution_filter.cpp PROPERTIES COMPILE_OPTIONS "-O3;-ffast-math")
endif ()
add_library(engine-sim-core STATIC ${ES_SOURCES})
target_include_directories(engine-sim-core PUBLIC ${ES}/include ${ES}/dependencies/submodules)
target_link_libraries(engine-sim-core PUBLIC scs)

# the plugin's native library: C API + engine builder
add_library(enginesim SHARED bridge/enginesim_api.cpp bridge/engine_builder.cpp bridge/effects.cpp bridge/induction.cpp)
target_link_libraries(enginesim PRIVATE engine-sim-core)
# not "enginesim.dll": Windows file names are case-insensitive and EngineSim.dll is the plugin
set_target_properties(enginesim PROPERTIES PREFIX "" OUTPUT_NAME enginesim_native CXX_VISIBILITY_PRESET hidden)
if (WIN32)
    # one self-contained DLL: no libstdc++ / libgcc / winpthread DLLs to ship
    target_link_options(enginesim PRIVATE -static -static-libgcc -static-libstdc++ -s)
endif ()

add_executable(render_wav tools/render_wav.cpp)
target_include_directories(render_wav PRIVATE bridge)
target_link_libraries(render_wav PRIVATE enginesim)
if (WIN32)
    target_link_options(render_wav PRIVATE -static -static-libgcc -static-libstdc++)
endif ()

add_executable(cosim tools/cosim.cpp)
target_include_directories(cosim PRIVATE bridge)
target_link_libraries(cosim PRIVATE enginesim)
if (WIN32)
    target_link_options(cosim PRIVATE -static -static-libgcc -static-libstdc++)
endif ()

add_executable(probe tools/probe.cpp)
target_include_directories(probe PRIVATE bridge)
target_link_libraries(probe PRIVATE enginesim)
if (WIN32)
    target_link_options(probe PRIVATE -static -static-libgcc -static-libstdc++)
endif ()
