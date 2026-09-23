add_executable(UwpImGuiFrontendTests
	FrontendTests.cpp
)

add_library(UwpImGuiFrontendTestImGui STATIC
	"${UWP_IMGUI_FRONTEND_IMGUI_DIR}/imgui.cpp"
	"${UWP_IMGUI_FRONTEND_IMGUI_DIR}/imgui_draw.cpp"
	"${UWP_IMGUI_FRONTEND_IMGUI_DIR}/imgui_tables.cpp"
	"${UWP_IMGUI_FRONTEND_IMGUI_DIR}/imgui_widgets.cpp"
)
target_include_directories(UwpImGuiFrontendTestImGui PUBLIC
	"${UWP_IMGUI_FRONTEND_IMGUI_DIR}"
)
target_compile_features(UwpImGuiFrontendTestImGui PRIVATE cxx_std_20)

target_compile_features(UwpImGuiFrontendTests PRIVATE cxx_std_20)
target_link_libraries(UwpImGuiFrontendTests PRIVATE
	UwpImGuiFrontend
	UwpImGuiFrontendTestImGui
)

if(MSVC)
	set_property(TARGET UwpImGuiFrontendTestImGui PROPERTY
		MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>$<$<BOOL:${UWP_IMGUI_FRONTEND_UWP_BUILD}>:DLL>")
	set_property(TARGET UwpImGuiFrontendTests PROPERTY
		MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>$<$<BOOL:${UWP_IMGUI_FRONTEND_UWP_BUILD}>:DLL>")
endif()

set_target_properties(UwpImGuiFrontendTestImGui PROPERTIES FOLDER "tests")

add_test(NAME UwpImGuiFrontendTests COMMAND UwpImGuiFrontendTests)
