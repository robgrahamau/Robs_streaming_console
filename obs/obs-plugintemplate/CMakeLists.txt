cmake_minimum_required(VERSION 3.28...3.30)

include("${CMAKE_CURRENT_SOURCE_DIR}/cmake/common/bootstrap.cmake" NO_POLICY_SCOPE)

project(${_name} VERSION ${_version})

option(ENABLE_FRONTEND_API "Use obs-frontend-api for UI functionality" OFF)
option(ENABLE_QT "Use Qt functionality" OFF)
option(STEAMING_AUTO_DEPLOY_TO_OBS "Copy the built plugin into the local OBS plugin folder after each build" ON)

set(
  STEAMING_OBS_PLUGIN_DIR
  "C:/ProgramData/obs-studio/plugins/steaming-plugin/bin/64bit"
  CACHE PATH
  "OBS plugin deployment directory for steaming-plugin.dll"
)

set(
  STEAMING_OBS_LOCALE_DIR
  "C:/ProgramData/obs-studio/plugins/steaming-plugin/data/locale"
  CACHE PATH
  "OBS plugin locale deployment directory"
)

include(compilerconfig)
include(defaults)
include(helpers)

add_library(${CMAKE_PROJECT_NAME} MODULE)

find_package(libobs REQUIRED)
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE OBS::libobs)

if(WIN32)
  target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE gdi32 windowscodecs ole32 propsys)
endif()

if(ENABLE_FRONTEND_API)
  find_package(obs-frontend-api REQUIRED)
  target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE OBS::obs-frontend-api)
endif()

if(ENABLE_QT)
  find_package(Qt6 COMPONENTS Widgets Core)
  target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE Qt6::Core Qt6::Widgets)
  target_compile_options(
    ${CMAKE_PROJECT_NAME}
    PRIVATE $<$<C_COMPILER_ID:Clang,AppleClang>:-Wno-quoted-include-in-framework-header -Wno-comma>
  )
  set_target_properties(
    ${CMAKE_PROJECT_NAME}
    PROPERTIES AUTOMOC ON AUTOUIC ON AUTORCC ON
  )
endif()

target_sources(
  ${CMAKE_PROJECT_NAME}
  PRIVATE
    src/plugin-main.cpp
    src/pipe_client.cpp
    src/alert_source.cpp
    src/chat_source.cpp
    src/label_source.cpp
    src/goal_source.cpp
    src/emoji_rain_source.cpp
    src/music_source.cpp
    src/lyrics_source.cpp
    src/music_render.cpp
    src/renderer.cpp
    src/layout_renderer.cpp
)

target_compile_definitions(
  ${CMAKE_PROJECT_NAME}
  PRIVATE
    STEAMING_PLUGIN_VERSION="${PROJECT_VERSION}"
)

set_target_properties_plugin(${CMAKE_PROJECT_NAME} PROPERTIES OUTPUT_NAME ${_name})

if(WIN32 AND STEAMING_AUTO_DEPLOY_TO_OBS)
  add_custom_command(
    TARGET ${CMAKE_PROJECT_NAME}
    POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${STEAMING_OBS_PLUGIN_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:${CMAKE_PROJECT_NAME}>
            "${STEAMING_OBS_PLUGIN_DIR}/$<TARGET_FILE_NAME:${CMAKE_PROJECT_NAME}>"
    COMMAND ${CMAKE_COMMAND} -E make_directory "${STEAMING_OBS_LOCALE_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_CURRENT_SOURCE_DIR}/data/locale/en-US.ini"
            "${STEAMING_OBS_LOCALE_DIR}/en-US.ini"
    COMMENT "Deploying steaming plugin into OBS plugin folder"
  )

  message(STATUS "Steaming OBS auto-deploy: ON")
  message(STATUS "Steaming OBS DLL deploy dir: ${STEAMING_OBS_PLUGIN_DIR}")
  message(STATUS "Steaming OBS locale deploy dir: ${STEAMING_OBS_LOCALE_DIR}")
endif()
