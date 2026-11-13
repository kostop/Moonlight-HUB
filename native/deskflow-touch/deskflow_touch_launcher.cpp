#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <iostream>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

#include "deskflow_touch_filter_shared.h"

namespace {

struct LaunchPaths {
  std::wstring directory;
  std::wstring real_executable;
  std::wstring filter_library;
};

constexpr wchar_t kDeskflowServerConfig[] =
    L"C:\\ProgramData\\Deskflow\\deskflow-server.conf";

// path/content: 要读取的文件路径，以及接收完整内容的字符串。
bool read_binary_file(const std::filesystem::path &path, std::string &content) {
  std::ifstream input(path, std::ios::binary);
  if (!input) {
    return false;
  }
  content = std::string{
      std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
  return input.good() || input.eof();
}

// content: UTF-8 编码的 Windows 文件路径；返回为空表示路径无效。
std::wstring utf8_to_wide_path(std::string content) {
  const auto first = content.find_first_not_of(" \t\r\n");
  if (first == std::string::npos) {
    return {};
  }
  const auto last = content.find_last_not_of(" \t\r\n");
  content = content.substr(first, last - first + 1);
  const auto size = MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, content.data(), static_cast<int>(content.size()), nullptr, 0);
  if (size <= 0) {
    return {};
  }
  std::wstring result(static_cast<std::size_t>(size), L'\0');
  if (MultiByteToWideChar(
          CP_UTF8,
          MB_ERR_INVALID_CHARS,
          content.data(),
          static_cast<int>(content.size()),
          result.data(),
          size) != size) {
    return {};
  }
  return result;
}

// 参数 launcher_directory：启动器所在目录；路径文件中的相对路径按此目录解析。
std::wstring configured_real_core_path(const std::wstring &launcher_directory) {
  std::string content;
  const auto path = std::filesystem::path(launcher_directory) / L"deskflow-core.real-path.txt";
  if (!read_binary_file(path, content)) {
    return {};
  }
  const auto configured = utf8_to_wide_path(std::move(content));
  if (configured.empty()) {
    return {};
  }
  auto candidate = (std::filesystem::path(launcher_directory) / configured).lexically_normal();
  candidate.make_preferred();
  if (GetFileAttributesW(candidate.c_str()) == INVALID_FILE_ATTRIBUTES) {
    return {};
  }
  return candidate.wstring();
}

// path/content: 要原子替换的文件路径和完整文件内容。
bool write_binary_file(const std::filesystem::path &path, const std::string &content) {
  auto temporary = path;
  temporary += L".native-touch-filter.tmp";
  std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
  if (!output) {
    return false;
  }
  output.write(content.data(), static_cast<std::streamsize>(content.size()));
  output.close();
  if (!output) {
    DeleteFileW(temporary.c_str());
    return false;
  }
  if (!MoveFileExW(
          temporary.c_str(),
          path.c_str(),
          MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
    DeleteFileW(temporary.c_str());
    return false;
  }
  return true;
}

// content: 即将交给 Deskflow 核心读取的服务器配置全文。
bool add_f24_local_release_rule(std::string &content) {
  constexpr std::string_view rule =
      "keystroke(f24) = switchToScreen(Desktop-Lin)";
  if (content.find(rule) != std::string::npos) {
    return true;
  }
  if (content.find("Desktop-Lin:") == std::string::npos ||
      content.find("qingxian:") == std::string::npos) {
    return false;
  }

  const auto options = content.find("section: options");
  if (options == std::string::npos) {
    return false;
  }
  const auto end = content.find("\nend", options);
  if (end == std::string::npos) {
    return false;
  }

  const auto newline = content.find("\r\n") == std::string::npos ? "\n" : "\r\n";
  content.insert(end + 1, "\t" + std::string(rule) + newline);
  return true;
}

// config_path: 仅用于验证或维护的服务器配置文件路径。
bool ensure_f24_local_release_rule(const std::wstring &config_path) {
  const std::filesystem::path path(config_path);
  std::string content;
  if (!read_binary_file(path, content) ||
      !add_f24_local_release_rule(content)) {
    return false;
  }
  return write_binary_file(path, content);
}

std::string utf8_path(const std::filesystem::path &path) {
  auto value = path.wstring();
  for (auto &character : value) {
    if (character == L'\\') {
      character = L'/';
    }
  }
  const auto size = WideCharToMultiByte(
      CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
  if (size <= 0) {
    return {};
  }
  std::string result(static_cast<std::size_t>(size), '\0');
  WideCharToMultiByte(
      CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
  return result;
}

// settings/server_config: GUI 设置全文、服务器私有配置路径。
bool replace_external_config_path(
    std::string &settings,
    const std::filesystem::path &server_config) {
  const auto section = settings.find("[server]");
  if (section == std::string::npos) {
    return false;
  }
  const auto section_end = settings.find("\n[", section + 1);
  const auto key = settings.find("externalConfigFile=", section);
  if (key == std::string::npos ||
      (section_end != std::string::npos && key >= section_end)) {
    return false;
  }
  const auto value = key + std::strlen("externalConfigFile=");
  auto line_end = settings.find_first_of("\r\n", value);
  if (line_end == std::string::npos) {
    line_end = settings.size();
  }
  const auto replacement = utf8_path(server_config);
  if (replacement.empty()) {
    return false;
  }
  settings.replace(value, line_end - value, replacement);
  return true;
}

// arguments/first_index: Deskflow 原始参数，以及开始转发的参数下标。
bool prepare_private_core_settings(
    std::vector<std::wstring> &arguments,
    std::size_t first_index) {
  std::size_t settings_index = arguments.size();
  for (auto index = first_index; index + 1 < arguments.size(); ++index) {
    if (arguments[index] == L"--settings") {
      settings_index = index + 1;
      break;
    }
  }
  if (settings_index >= arguments.size()) {
    return true;
  }

  wchar_t local_app_data[MAX_PATH] {};
  const auto length = GetEnvironmentVariableW(
      L"LOCALAPPDATA", local_app_data, ARRAYSIZE(local_app_data));
  if (length == 0 || length >= ARRAYSIZE(local_app_data)) {
    return false;
  }
  const std::filesystem::path private_directory =
      std::filesystem::path(local_app_data) / L"DeskflowTouchFilter";
  std::error_code directory_error;
  std::filesystem::create_directories(private_directory, directory_error);
  if (directory_error) {
    return false;
  }

  const auto private_settings = private_directory / L"core.ini";
  const auto private_server = private_directory / L"server.conf";
  std::string settings;
  std::string server;
  if (!read_binary_file(arguments[settings_index], settings) ||
      !read_binary_file(kDeskflowServerConfig, server) ||
      !add_f24_local_release_rule(server) ||
      !replace_external_config_path(settings, private_server) ||
      !write_binary_file(private_server, server) ||
      !write_binary_file(private_settings, settings)) {
    return false;
  }
  arguments[settings_index] = private_settings.wstring();
  return true;
}

std::wstring quote_argument(const std::wstring &argument) {
  if (argument.find_first_of(L" \t\"") == std::wstring::npos) {
    return argument;
  }

  std::wstring quoted = L"\"";
  std::size_t backslashes = 0;
  for (const auto character : argument) {
    if (character == L'\\') {
      ++backslashes;
      continue;
    }
    if (character == L'\"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted.push_back(L'\"');
      backslashes = 0;
      continue;
    }
    quoted.append(backslashes, L'\\');
    backslashes = 0;
    quoted.push_back(character);
  }
  quoted.append(backslashes * 2, L'\\');
  quoted.push_back(L'\"');
  return quoted;
}

// arguments/first_index: 子进程参数列表，以及需要开始转发的参数下标。
std::wstring build_command_line(
    const std::vector<std::wstring> &arguments,
    std::size_t first_index,
    const std::wstring &real_executable) {
  std::wstring command_line = quote_argument(real_executable);
  for (auto index = first_index; index < arguments.size(); ++index) {
    command_line.push_back(L' ');
    command_line.append(quote_argument(arguments[index]));
  }
  return command_line;
}

LaunchPaths default_launch_paths() {
  wchar_t module_path[MAX_PATH] {};
  const auto length = GetModuleFileNameW(nullptr, module_path, ARRAYSIZE(module_path));
  std::wstring launcher_directory(module_path, length);
  const auto separator = launcher_directory.find_last_of(L"\\/");
  launcher_directory.resize(separator == std::wstring::npos ? 0 : separator);
  // Hub 发布目录整体移动后，核心与 Qt/OpenSSL 依赖仍从自身 native 子目录加载。
  auto real_executable = launcher_directory + L"\\native\\deskflow-core.exe";
  if (const auto configured = configured_real_core_path(launcher_directory); !configured.empty()) {
    real_executable = configured;
  }
  const auto real_separator = real_executable.find_last_of(L"\\/");
  const auto working_directory = real_separator == std::wstring::npos
                                     ? launcher_directory
                                     : real_executable.substr(0, real_separator);
  return {
      working_directory,
      real_executable,
      launcher_directory + L"\\deskflow_touch_filter.dll",
  };
}

int print_status() {
  const auto mapping = OpenFileMappingW(
      FILE_MAP_READ, FALSE, deskflow_touch_filter::kStateMappingName);
  if (mapping == nullptr) {
    std::wcout << L"stopped\n";
    return 1;
  }
  const auto state = static_cast<const deskflow_touch_filter::SharedState *>(
      MapViewOfFile(
          mapping,
          FILE_MAP_READ,
          0,
          0,
          sizeof(deskflow_touch_filter::SharedState)));
  if (state == nullptr || state->version != deskflow_touch_filter::kStateVersion) {
    if (state != nullptr) {
      UnmapViewOfFile(state);
    }
    CloseHandle(mapping);
    std::wcerr << L"incompatible_state\n";
    return 2;
  }

  std::wcout << L"running pid=" << state->process_id
              << L" filter_installed=" << state->filter_installed
              << L" mouse_filter=" << state->mouse_filter_installed
              << L" keyboard_filter=" << state->keyboard_filter_installed
              << L" local_keyboard=" << state->local_keyboard_mode
              << L" touch_mouse=" << state->touch_mouse_events
              << L" bridged_mouse=" << state->bridged_mouse_events
              << L" bridge_failures=" << state->bridge_send_failures
              << L" pen_mouse=" << state->pen_mouse_events
              << L" keyboard_activations=" << state->local_release_sent
              << L" local_keyboard_events=" << state->local_keyboard_events
              << L" physical_mouse_reclaims=" << state->physical_mouse_reclaims
              << L" touch_route=" << state->touch_route_events
              << L" last_pointer=" << state->last_pointer_x << L',' << state->last_pointer_y
              << L" send_error=" << state->last_send_error
              << L" mode=native-touch+compat-mouse-bridged+local-keyboard-bypass\n";
  UnmapViewOfFile(state);
  CloseHandle(mapping);
  return 0;
}

int self_test() {
  const bool passed =
       deskflow_touch_filter::is_promoted_touch(
           deskflow_touch_filter::kPointerSignature | deskflow_touch_filter::kTouchFlag) &&
       !deskflow_touch_filter::is_promoted_touch(deskflow_touch_filter::kPointerSignature) &&
       deskflow_touch_filter::is_promoted_pen_or_touch(
           deskflow_touch_filter::kPointerSignature) &&
       !deskflow_touch_filter::is_promoted_pen_or_touch(0) &&
       deskflow_touch_filter::kStateVersion == 4 &&
       deskflow_touch_filter::kTouchRouteVersion == 1;
  std::wcout << L"self-test=" << (passed ? L"passed" : L"failed")
              << L" mode=native-touch+compat-mouse-bridged+local-keyboard-bypass\n";
  return passed ? 0 : 3;
}

// config_path: 仅用于验证和维护的 Deskflow 外部配置文件路径。
int repair_config(const std::wstring &config_path) {
  const bool repaired = ensure_f24_local_release_rule(config_path);
  std::wcout << L"config-repair=" << (repaired ? L"passed" : L"failed")
             << L" path=" << config_path << L'\n';
  return repaired ? 0 : 72;
}

// process/library_path: 已暂停的 Deskflow 子进程，以及要注入的过滤 DLL 路径。
bool inject_filter(HANDLE process, const std::wstring &library_path, DWORD &error) {
  const auto bytes = (library_path.size() + 1) * sizeof(wchar_t);
  const auto remote_path = VirtualAllocEx(
      process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  if (remote_path == nullptr) {
    error = GetLastError();
    return false;
  }

  SIZE_T written = 0;
  if (!WriteProcessMemory(
          process, remote_path, library_path.c_str(), bytes, &written) ||
      written != bytes) {
    error = GetLastError();
    VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
    return false;
  }

  const auto kernel32 = GetModuleHandleW(L"kernel32.dll");
  const auto load_library = reinterpret_cast<LPTHREAD_START_ROUTINE>(
      GetProcAddress(kernel32, "LoadLibraryW"));
  if (load_library == nullptr) {
    error = GetLastError();
    VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
    return false;
  }

  const auto remote_thread = CreateRemoteThread(
      process, nullptr, 0, load_library, remote_path, 0, nullptr);
  if (remote_thread == nullptr) {
    error = GetLastError();
    VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
    return false;
  }

  const auto wait_result = WaitForSingleObject(remote_thread, 10000);
  DWORD module_handle = 0;
  const bool loaded = wait_result == WAIT_OBJECT_0 &&
                      GetExitCodeThread(remote_thread, &module_handle) &&
                      module_handle != 0;
  error = loaded ? ERROR_SUCCESS : GetLastError();
  CloseHandle(remote_thread);
  VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
  return loaded;
}

// paths/arguments/first_index: 启动路径、原始参数列表和开始转发的参数下标。
int launch_filtered_core(
    const LaunchPaths &paths,
    std::vector<std::wstring> arguments,
    std::size_t first_index) {
  if (GetFileAttributesW(paths.real_executable.c_str()) == INVALID_FILE_ATTRIBUTES ||
      GetFileAttributesW(paths.filter_library.c_str()) == INVALID_FILE_ATTRIBUTES) {
    std::wcerr << L"missing_real_core_or_filter\n";
    return 2;
  }
  auto command_line = build_command_line(arguments, first_index, paths.real_executable);
  std::vector<wchar_t> command_buffer(command_line.begin(), command_line.end());
  command_buffer.push_back(L'\0');

  STARTUPINFOW startup {};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION child {};
  if (!CreateProcessW(
          paths.real_executable.c_str(),
          command_buffer.data(),
          nullptr,
          nullptr,
          TRUE,
          CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
          nullptr,
          paths.directory.c_str(),
          &startup,
          &child)) {
    std::wcerr << L"create_core_error=" << GetLastError() << L'\n';
    return 2;
  }

  DWORD injection_error = ERROR_SUCCESS;
  if (!inject_filter(child.hProcess, paths.filter_library, injection_error)) {
    TerminateProcess(child.hProcess, 70);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    std::wcerr << L"inject_filter_error=" << injection_error << L'\n';
    return 70;
  }

  if (ResumeThread(child.hThread) == static_cast<DWORD>(-1)) {
    const auto error = GetLastError();
    TerminateProcess(child.hProcess, 71);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    std::wcerr << L"resume_core_error=" << error << L'\n';
    return 71;
  }
  CloseHandle(child.hThread);

  WaitForSingleObject(child.hProcess, INFINITE);
  DWORD exit_code = 1;
  GetExitCodeProcess(child.hProcess, &exit_code);
  CloseHandle(child.hProcess);
  return static_cast<int>(exit_code);
}

}  // namespace

int wmain(int argc, wchar_t **argv) {
  std::vector<std::wstring> arguments;
  for (int index = 0; index < argc; ++index) {
    arguments.emplace_back(argv[index]);
  }

  if (argc >= 2 && std::wstring_view(argv[1]) == L"--touch-filter-status") {
    return print_status();
  }
  if (argc >= 2 && std::wstring_view(argv[1]) == L"--touch-filter-self-test") {
    return self_test();
  }
  if (argc >= 2 && std::wstring_view(argv[1]) == L"--touch-filter-paths") {
    const auto paths = default_launch_paths();
    std::wcout << L"core=" << paths.real_executable << L'\n'
               << L"working_directory=" << paths.directory << L'\n'
               << L"filter=" << paths.filter_library << L'\n';
    return GetFileAttributesW(paths.real_executable.c_str()) != INVALID_FILE_ATTRIBUTES &&
                   GetFileAttributesW(paths.filter_library.c_str()) != INVALID_FILE_ATTRIBUTES
               ? 0 : 2;
  }
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--touch-filter-repair-config") {
    return repair_config(argv[2]);
  }

  auto paths = default_launch_paths();
  std::size_t first_index = 1;
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--touch-filter-real") {
    paths.real_executable = argv[2];
    const auto separator = paths.real_executable.find_last_of(L"\\/");
    paths.directory = paths.real_executable.substr(0, separator);
    first_index = 3;
  }
  if (!prepare_private_core_settings(arguments, first_index)) {
    std::wcerr << L"prepare_private_settings_warning\n";
  }
  return launch_filtered_core(paths, arguments, first_index);
}
