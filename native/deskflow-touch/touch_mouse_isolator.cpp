#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellscalingapi.h>

#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <string_view>

#pragma comment(lib, "shcore.lib")

namespace {

constexpr wchar_t kSingletonName[] = L"Local\\MoonlightTouchMouseIsolator.Singleton";
constexpr wchar_t kStopEventName[] = L"Local\\MoonlightTouchMouseIsolator.Stop";
constexpr wchar_t kStateMappingName[] = L"Local\\MoonlightTouchMouseIsolator.State";
constexpr wchar_t kTouchRouteMappingName[] = L"Local\\MoonlightTouchInjection.Route";

constexpr ULONG_PTR kPointerSignature = 0xFF515700ULL;
constexpr ULONG_PTR kPointerSignatureMask = 0xFFFFFF00ULL;
constexpr ULONG_PTR kTouchFlag = 0x00000080ULL;
constexpr UINT kReleaseDeskflowKeyboardMessage = WM_APP + 24;
constexpr UINT kDeskflowKeyMessage = WM_APP + 0x0012;
constexpr WORD kDeskflowReleaseKey = VK_F24;
constexpr ULONGLONG kTouchSequenceGapMs = 250;
constexpr DWORD kStateVersion = 15;
constexpr DWORD kTouchRouteVersion = 1;

struct alignas(8) SharedState {
  DWORD version;
  DWORD process_id;
  volatile LONG64 observed_touch_mouse_events;
  volatile LONG64 observed_touch_route_events;
  volatile LONG64 deskflow_release_requests;
  volatile LONG64 deskflow_release_sent;
  volatile LONG64 deskflow_release_failures;
  volatile LONG64 passed_pen_mouse_events;
  volatile LONG64 passed_physical_mouse_events;
  volatile LONG last_touch_x;
  volatile LONG last_touch_y;
  volatile LONG last_send_count;
  volatile LONG last_send_error;
  volatile LONG mouse_hook_enabled;
  volatile LONG bridge_mode;
  volatile LONG64 bridged_mouse_events;
  volatile LONG64 bridge_send_failures;
};

// 桥接模式：拦截触摸兼容鼠标后，把等价的鼠标消息直接投递给触点下的窗口（与 Deskflow 触摸过滤器相同），真实光标不移动。
struct touch_mouse_bridge_state_t {
  bool active {};
  bool non_client {};
  HWND target_window {};
  HWND root_window {};
  LRESULT hit_test {HTCLIENT};
  WPARAM pressed_buttons {};
  bool snapped {};      // second press of a double tap: kept on the first tap's point
  POINT snap_point {};
  LONG snap_tolerance {};
};

// The previous bridged tap, the first half of a possible double tap.
struct touch_tap_t {
  bool valid {};
  WPARAM button {};  // its button-down message
  DWORD time {};
  POINT point {};
  HWND window {};
};

struct alignas(8) TouchRouteState {
  DWORD version;
  volatile LONG sequence;
  volatile LONG screen_x;
  volatile LONG screen_y;
  volatile LONG64 update_tick;
};

struct DesktopContext {
  HWINSTA station {};
  HDESK input {};
};

HHOOK g_mouse_hook = nullptr;
SharedState *g_state = nullptr;
HANDLE g_touch_route_mapping = nullptr;
TouchRouteState *g_touch_route_state = nullptr;
DWORD g_main_thread_id = 0;
volatile LONG g_release_pending = 0;
volatile LONG64 g_last_touch_event_tick = 0;
LONG g_last_touch_route_sequence = 0;
ULONGLONG g_last_touch_route_tick = 0;
bool g_bridge_mode = false;
touch_mouse_bridge_state_t g_touch_mouse_bridge;

void release_deskflow_keyboard();

bool open_touch_route() {
  g_touch_route_mapping = CreateFileMappingW(
      INVALID_HANDLE_VALUE,
      nullptr,
      PAGE_READWRITE,
      0,
      sizeof(TouchRouteState),
      kTouchRouteMappingName);
  if (g_touch_route_mapping == nullptr) {
    return false;
  }

  const bool created = GetLastError() != ERROR_ALREADY_EXISTS;
  g_touch_route_state = static_cast<TouchRouteState *>(MapViewOfFile(
      g_touch_route_mapping,
      FILE_MAP_ALL_ACCESS,
      0,
      0,
      sizeof(TouchRouteState)));
  if (g_touch_route_state == nullptr) {
    CloseHandle(g_touch_route_mapping);
    g_touch_route_mapping = nullptr;
    return false;
  }

  if (created) {
    *g_touch_route_state = {};
    g_touch_route_state->version = kTouchRouteVersion;
  }
  return g_touch_route_state->version == kTouchRouteVersion;
}

void close_touch_route() {
  if (g_touch_route_state != nullptr) {
    UnmapViewOfFile(g_touch_route_state);
    g_touch_route_state = nullptr;
  }
  if (g_touch_route_mapping != nullptr) {
    CloseHandle(g_touch_route_mapping);
    g_touch_route_mapping = nullptr;
  }
}

// desktop：返回已绑定到当前交互桌面的句柄，供全局鼠标钩子使用。
bool attach_to_input_desktop(DesktopContext &desktop) {
  SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

  desktop.station = OpenWindowStationW(L"WinSta0", FALSE, WINSTA_ALL_ACCESS);
  if (desktop.station == nullptr) {
    std::wcerr << L"failed to open WinSta0: " << GetLastError() << L'\n';
    return false;
  }
  if (!SetProcessWindowStation(desktop.station)) {
    std::wcerr << L"failed to attach to WinSta0: " << GetLastError() << L'\n';
    CloseWindowStation(desktop.station);
    desktop.station = nullptr;
    return false;
  }

  desktop.input = OpenInputDesktop(
      DF_ALLOWOTHERACCOUNTHOOK, FALSE,
      DESKTOP_HOOKCONTROL | DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
  if (desktop.input == nullptr) {
    std::wcerr << L"failed to open input desktop: " << GetLastError() << L'\n';
    return false;
  }
  if (!SetThreadDesktop(desktop.input)) {
    std::wcerr << L"failed to attach to input desktop: " << GetLastError() << L'\n';
    CloseDesktop(desktop.input);
    desktop.input = nullptr;
    return false;
  }
  return true;
}

// desktop：由 attach_to_input_desktop() 填充的桌面上下文。
void detach_from_input_desktop(DesktopContext &desktop) {
  desktop = {};
}

// extra_info：低级鼠标事件携带的 Windows 指针标记。
bool is_promoted_pen_or_touch(ULONG_PTR extra_info) {
  const auto value = extra_info & 0xFFFFFFFFULL;
  return (value & kPointerSignatureMask) == kPointerSignature;
}

// extra_info：低级鼠标事件携带的 Windows 指针标记。
bool is_touch_promoted_mouse(ULONG_PTR extra_info) {
  const auto value = extra_info & 0xFFFFFFFFULL;
  return is_promoted_pen_or_touch(value) && (value & kTouchFlag) != 0;
}

void queue_deskflow_keyboard_release() {
  if (g_state != nullptr) {
    InterlockedIncrement64(&g_state->deskflow_release_requests);
  }
  if (InterlockedCompareExchange(&g_release_pending, 1, 0) != 0) {
    return;
  }
  if (!PostThreadMessageW(g_main_thread_id, kReleaseDeskflowKeyboardMessage, 0, 0)) {
    InterlockedExchange(&g_release_pending, 0);
    if (g_state != nullptr) {
      InterlockedIncrement64(&g_state->deskflow_release_failures);
    }
  }
}

void poll_touch_route() {
  if (g_touch_route_state == nullptr || g_touch_route_state->version != kTouchRouteVersion) {
    return;
  }

  const LONG sequence = g_touch_route_state->sequence;
  if (sequence == g_last_touch_route_sequence) {
    return;
  }

  const auto tick = static_cast<ULONGLONG>(g_touch_route_state->update_tick);
  g_last_touch_route_sequence = sequence;
  if (g_state != nullptr) {
    InterlockedIncrement64(&g_state->observed_touch_route_events);
    InterlockedExchange(&g_state->last_touch_x, g_touch_route_state->screen_x);
    InterlockedExchange(&g_state->last_touch_y, g_touch_route_state->screen_y);
  }

  if (!g_bridge_mode && (g_last_touch_route_tick == 0 || tick < g_last_touch_route_tick ||
      tick - g_last_touch_route_tick > kTouchSequenceGapMs)) {
    queue_deskflow_keyboard_release();
  }
  g_last_touch_route_tick = tick;
}

bool is_deskflow_core_process(DWORD process_id) {
  HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
  if (process == nullptr) {
    return false;
  }

  wchar_t path[MAX_PATH] {};
  DWORD path_length = ARRAYSIZE(path);
  const bool queried = QueryFullProcessImageNameW(process, 0, path, &path_length) != FALSE;
  CloseHandle(process);
  if (!queried) {
    return false;
  }

  const wchar_t *filename = wcsrchr(path, L'\\');
  filename = filename == nullptr ? path : filename + 1;
  return _wcsicmp(filename, L"deskflow-core.exe") == 0;
}

// data：接收找到的 Deskflow 核心事件线程 ID。
BOOL CALLBACK find_deskflow_window(HWND window, LPARAM data) {
  wchar_t class_name[64] {};
  if (GetClassNameW(window, class_name, ARRAYSIZE(class_name)) == 0 ||
      wcscmp(class_name, L"DeskflowDesk") != 0) {
    return TRUE;
  }

  DWORD process_id = 0;
  const DWORD thread_id = GetWindowThreadProcessId(window, &process_id);
  if (thread_id == 0 || !is_deskflow_core_process(process_id)) {
    return TRUE;
  }

  *reinterpret_cast<DWORD *>(data) = thread_id;
  return FALSE;
}

// virtual_key/error：要投递的虚拟键码，以及失败时返回的 Windows 错误码。
LONG post_deskflow_key(WORD virtual_key, DWORD &error) {
  DWORD thread_id = 0;
  EnumWindows(find_deskflow_window, reinterpret_cast<LPARAM>(&thread_id));
  if (thread_id == 0) {
    error = ERROR_NOT_FOUND;
    return 0;
  }

  const auto scan_code = MapVirtualKeyW(virtual_key, MAPVK_VK_TO_VSC) & 0x1ffu;
  if (scan_code == 0) {
    error = ERROR_INVALID_DATA;
    return 0;
  }

  const WPARAM key = MAKEWPARAM(0, MAKEWORD(virtual_key & 0xffu, 0));
  const LPARAM down = 1u | (scan_code << 16);
  const LPARAM up = down | 0x80000000u;
  LONG sent = 0;
  if (PostThreadMessageW(thread_id, kDeskflowKeyMessage, key, down)) {
    ++sent;
  } else {
    error = GetLastError();
    return sent;
  }
  if (PostThreadMessageW(thread_id, kDeskflowKeyMessage, key, up)) {
    ++sent;
    error = ERROR_SUCCESS;
  } else {
    error = GetLastError();
  }
  return sent;
}

void release_deskflow_keyboard() {
  DWORD error = ERROR_SUCCESS;
  const LONG sent = post_deskflow_key(kDeskflowReleaseKey, error);
  if (g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_count, sent);
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(error));
  }
  if (sent == 2) {
    if (g_state != nullptr) {
      InterlockedIncrement64(&g_state->deskflow_release_sent);
    }
  } else if (g_state != nullptr) {
    InterlockedIncrement64(&g_state->deskflow_release_failures);
  }
}


// ---- 触摸兼容鼠标桥接（移植自 deskflow_touch_filter.cpp）

void record_bridge_result(bool forwarded, bool failed) {
  if (g_state == nullptr) {
    return;
  }
  if (forwarded) {
    InterlockedIncrement64(&g_state->bridged_mouse_events);
    InterlockedExchange(&g_state->last_send_error, ERROR_SUCCESS);
  } else if (failed) {
    InterlockedIncrement64(&g_state->bridge_send_failures);
  }
}

void set_last_error_state(DWORD error) {
  if (g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(error));
  }
}

// point：Sunshine 发布的当前触点（物理屏幕坐标）；只在序列新鲜时采用。
bool current_touch_route_point(POINT &point) {
  if (g_touch_route_state == nullptr || g_touch_route_state->version != kTouchRouteVersion) {
    return false;
  }
  for (int attempt = 0; attempt < 2; ++attempt) {
    const LONG sequence_before = InterlockedCompareExchange(&g_touch_route_state->sequence, 0, 0);
    const LONG x = InterlockedCompareExchange(&g_touch_route_state->screen_x, 0, 0);
    const LONG y = InterlockedCompareExchange(&g_touch_route_state->screen_y, 0, 0);
    const auto update_tick = static_cast<ULONGLONG>(
        InterlockedCompareExchange64(&g_touch_route_state->update_tick, 0, 0));
    const LONG sequence_after = InterlockedCompareExchange(&g_touch_route_state->sequence, 0, 0);
    const auto now = GetTickCount64();
    if (sequence_before != 0 && sequence_before == sequence_after && update_tick <= now &&
        now - update_tick <= kTouchSequenceGapMs) {
      point = {x, y};
      return true;
    }
  }
  return false;
}

void reset_touch_mouse_bridge(bool cancel) {
  if (cancel && g_touch_mouse_bridge.active && g_touch_mouse_bridge.root_window != nullptr &&
      IsWindow(g_touch_mouse_bridge.root_window)) {
    PostMessageW(g_touch_mouse_bridge.root_window, WM_CANCELMODE, 0, 0);
  }
  g_touch_mouse_bridge = {};
  g_touch_mouse_bridge.hit_test = HTCLIENT;
}

bool physical_to_target_screen(HWND window, POINT physical_point, POINT &target_point) {
  target_point = physical_point;
  if (PhysicalToLogicalPointForPerMonitorDPI(window, &target_point)) {
    return true;
  }
  set_last_error_state(GetLastError());
  return false;
}

bool physical_to_target_client(HWND window, POINT physical_point, POINT &client_point) {
  POINT target_screen_point {};
  if (!physical_to_target_screen(window, physical_point, target_screen_point)) {
    return false;
  }
  const DPI_AWARENESS_CONTEXT target_context = GetWindowDpiAwarenessContext(window);
  if (target_context == nullptr) {
    set_last_error_state(ERROR_INVALID_WINDOW_HANDLE);
    return false;
  }
  const DPI_AWARENESS_CONTEXT previous_context = SetThreadDpiAwarenessContext(target_context);
  if (previous_context == nullptr) {
    set_last_error_state(GetLastError());
    return false;
  }
  client_point = target_screen_point;
  const bool converted = ScreenToClient(window, &client_point) != FALSE;
  const DWORD error = converted ? ERROR_SUCCESS : GetLastError();
  SetThreadDpiAwarenessContext(previous_context);
  if (!converted) {
    set_last_error_state(error);
  }
  return converted;
}

HWND deepest_window_at(POINT physical_point) {
  HWND current = WindowFromPoint(physical_point);
  for (int depth = 0; current != nullptr && depth < 16; ++depth) {
    POINT client_point {};
    if (!physical_to_target_client(current, physical_point, client_point)) {
      break;
    }
    const HWND child = ChildWindowFromPointEx(
        current, client_point, CWP_SKIPDISABLED | CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
    if (child == nullptr || child == current) {
      break;
    }
    current = child;
  }
  return current;
}

bool query_hit_test(HWND window, POINT physical_point, LRESULT &hit_test) {
  POINT target_screen_point {};
  if (!physical_to_target_screen(window, physical_point, target_screen_point)) {
    return false;
  }
  DWORD_PTR result = HTCLIENT;
  if (SendMessageTimeoutW(window, WM_NCHITTEST, 0, MAKELPARAM(target_screen_point.x, target_screen_point.y),
                          SMTO_ABORTIFHUNG | SMTO_BLOCK, 20, &result) == 0) {
    set_last_error_state(GetLastError());
    return false;
  }
  hit_test = static_cast<LRESULT>(result);
  return true;
}

bool is_non_client_hit_test(LRESULT hit_test) {
  return hit_test != HTCLIENT && hit_test != HTNOWHERE && hit_test != HTERROR && hit_test != HTTRANSPARENT;
}

bool begin_touch_mouse_bridge(POINT point) {
  reset_touch_mouse_bridge(true);
  const HWND target_window = deepest_window_at(point);
  if (target_window == nullptr || !IsWindow(target_window)) {
    set_last_error_state(ERROR_INVALID_WINDOW_HANDLE);
    return false;
  }
  const HWND root_window = GetAncestor(target_window, GA_ROOT);
  if (root_window == nullptr || !IsWindow(root_window)) {
    set_last_error_state(ERROR_INVALID_WINDOW_HANDLE);
    return false;
  }
  LRESULT hit_test = HTCLIENT;
  query_hit_test(root_window, point, hit_test);
  g_touch_mouse_bridge.active = true;
  g_touch_mouse_bridge.non_client = is_non_client_hit_test(hit_test);
  g_touch_mouse_bridge.target_window = target_window;
  g_touch_mouse_bridge.root_window = root_window;
  g_touch_mouse_bridge.hit_test = hit_test;
  return true;
}

bool post_touch_mouse_bridge_message(UINT client_message, UINT non_client_message, WPARAM client_w_param,
                                     WPARAM non_client_w_param, POINT point) {
  const HWND target_window =
      g_touch_mouse_bridge.non_client ? g_touch_mouse_bridge.root_window : g_touch_mouse_bridge.target_window;
  if (target_window == nullptr || !IsWindow(target_window)) {
    set_last_error_state(ERROR_INVALID_WINDOW_HANDLE);
    return false;
  }
  LPARAM message_point = 0;
  UINT message = client_message;
  WPARAM w_param = client_w_param;
  if (g_touch_mouse_bridge.non_client) {
    POINT target_screen_point {};
    if (!physical_to_target_screen(target_window, point, target_screen_point)) {
      return false;
    }
    message = non_client_message;
    w_param = non_client_w_param;
    message_point = MAKELPARAM(target_screen_point.x, target_screen_point.y);
  } else {
    POINT client_point {};
    if (!physical_to_target_client(target_window, point, client_point)) {
      return false;
    }
    message_point = MAKELPARAM(client_point.x, client_point.y);
  }
  if (PostMessageW(target_window, message, w_param, message_point)) {
    return true;
  }
  set_last_error_state(GetLastError());
  return false;
}

bool post_touch_context_menu(POINT point) {
  const HWND target_window =
      g_touch_mouse_bridge.non_client ? g_touch_mouse_bridge.root_window : g_touch_mouse_bridge.target_window;
  if (target_window == nullptr || !IsWindow(target_window)) {
    set_last_error_state(ERROR_INVALID_WINDOW_HANDLE);
    return false;
  }
  POINT target_screen_point {};
  if (!physical_to_target_screen(target_window, point, target_screen_point)) {
    return false;
  }
  if (PostMessageW(target_window, WM_CONTEXTMENU, reinterpret_cast<WPARAM>(target_window),
                   MAKELPARAM(target_screen_point.x, target_screen_point.y))) {
    return true;
  }
  set_last_error_state(GetLastError());
  return false;
}

WPARAM mouse_button_mask(WPARAM message, const MSLLHOOKSTRUCT &mouse) {
  switch (message) {
    case WM_LBUTTONDOWN:
    case WM_LBUTTONUP:
      return MK_LBUTTON;
    case WM_RBUTTONDOWN:
    case WM_RBUTTONUP:
      return MK_RBUTTON;
    case WM_MBUTTONDOWN:
    case WM_MBUTTONUP:
      return MK_MBUTTON;
    case WM_XBUTTONDOWN:
    case WM_XBUTTONUP:
      return HIWORD(mouse.mouseData) == XBUTTON2 ? MK_XBUTTON2 : MK_XBUTTON1;
    default:
      return 0;
  }
}

bool is_mouse_button_down(WPARAM message) {
  return message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN ||
         message == WM_XBUTTONDOWN;
}

bool is_mouse_button_up(WPARAM message) {
  return message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP ||
         message == WM_XBUTTONUP;
}

UINT non_client_button_message(WPARAM message) {
  switch (message) {
    case WM_LBUTTONDOWN:
      return WM_NCLBUTTONDOWN;
    case WM_LBUTTONUP:
      return WM_NCLBUTTONUP;
    case WM_RBUTTONDOWN:
      return WM_NCRBUTTONDOWN;
    case WM_RBUTTONUP:
      return WM_NCRBUTTONUP;
    case WM_MBUTTONDOWN:
      return WM_NCMBUTTONDOWN;
    case WM_MBUTTONUP:
      return WM_NCMBUTTONUP;
    case WM_XBUTTONDOWN:
      return WM_NCXBUTTONDOWN;
    case WM_XBUTTONUP:
      return WM_NCXBUTTONUP;
    default:
      return 0;
  }
}

WPARAM non_client_button_w_param(WPARAM message, const MSLLHOOKSTRUCT &mouse) {
  if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP) {
    return MAKEWPARAM(g_touch_mouse_bridge.hit_test, HIWORD(mouse.mouseData));
  }
  return static_cast<WPARAM>(g_touch_mouse_bridge.hit_test);
}

WPARAM client_button_w_param(WPARAM message, const MSLLHOOKSTRUCT &mouse) {
  if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP) {
    return MAKEWPARAM(g_touch_mouse_bridge.pressed_buttons, HIWORD(mouse.mouseData));
  }
  return g_touch_mouse_bridge.pressed_buttons;
}

touch_tap_t g_last_tap;

// Double taps (same as deskflow_touch_filter.cpp). Windows turns a second press into WM_xBUTTONDBLCLK only while it
// processes real input, and it lines a touch double tap up with the first tap before promoting it to the mouse; posted
// taps got neither, so desktop icons (SysListView32, CS_DBLCLKS) and Explorer's item view (DirectUI pairs clicks itself
// within SM_CXDOUBLECLK = 4 px) never saw a double click (2026-10-10). A second tap of the same button on the same
// window within the double-click time and a finger-sized distance is posted at the first tap's point, as a double click
// where the class asks for one.
constexpr int kDoubleTapToleranceDip = 24;  // about 6 mm: the mouse's 4 px are far too tight for a finger

LONG double_tap_tolerance(POINT physical_point) {
  UINT dpi_x = USER_DEFAULT_SCREEN_DPI;
  UINT dpi_y = USER_DEFAULT_SCREEN_DPI;
  const HMONITOR monitor = MonitorFromPoint(physical_point, MONITOR_DEFAULTTONEAREST);
  if (monitor == nullptr || FAILED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpi_x, &dpi_y)) || dpi_x == 0) {
    dpi_x = USER_DEFAULT_SCREEN_DPI;
  }
  return MulDiv(kDoubleTapToleranceDip, static_cast<int>(dpi_x), USER_DEFAULT_SCREEN_DPI);
}

bool within(POINT a, POINT b, LONG tolerance) {
  return std::labs(a.x - b.x) <= tolerance && std::labs(a.y - b.y) <= tolerance;
}

// message/time/point: the button-down of a new tap; true when it completes a double tap with the previous one.
bool is_second_tap(WPARAM message, DWORD time, POINT point) {
  return g_last_tap.valid && g_last_tap.button == message && time - g_last_tap.time <= GetDoubleClickTime() &&
         within(g_last_tap.point, point, double_tap_tolerance(g_last_tap.point));
}

UINT double_click_message(WPARAM message) {
  switch (message) {
    case WM_LBUTTONDOWN:
      return WM_LBUTTONDBLCLK;
    case WM_RBUTTONDOWN:
      return WM_RBUTTONDBLCLK;
    case WM_MBUTTONDOWN:
      return WM_MBUTTONDBLCLK;
    case WM_XBUTTONDOWN:
      return WM_XBUTTONDBLCLK;
    default:
      return static_cast<UINT>(message);
  }
}

UINT non_client_double_click_message(WPARAM message) {
  switch (message) {
    case WM_LBUTTONDOWN:
      return WM_NCLBUTTONDBLCLK;
    case WM_RBUTTONDOWN:
      return WM_NCRBUTTONDBLCLK;
    case WM_MBUTTONDOWN:
      return WM_NCMBUTTONDBLCLK;
    case WM_XBUTTONDOWN:
      return WM_NCXBUTTONDBLCLK;
    default:
      return non_client_button_message(message);
  }
}

// Windows only generates client-area double clicks for classes with CS_DBLCLKS (non-client ones always).
bool wants_double_clicks(HWND window) { return (GetClassLongPtrW(window, GCL_STYLE) & CS_DBLCLKS) != 0; }

// While the second press of a double tap stays within reach of the first tap, its moves and release stay on that
// point too (no accidental drag); once the finger leaves it, the bridge follows the finger again.
POINT bridged_point(POINT point) {
  if (!g_touch_mouse_bridge.snapped) {
    return point;
  }
  if (within(g_touch_mouse_bridge.snap_point, point, g_touch_mouse_bridge.snap_tolerance)) {
    return g_touch_mouse_bridge.snap_point;
  }
  g_touch_mouse_bridge.snapped = false;
  return point;
}

bool bridge_promoted_touch_mouse(WPARAM message, const MSLLHOOKSTRUCT &mouse, POINT point) {
  if (is_mouse_button_down(message)) {
    bool double_click = false;
    if (is_second_tap(message, mouse.time, point) && begin_touch_mouse_bridge(g_last_tap.point) &&
        g_touch_mouse_bridge.target_window == g_last_tap.window) {
      double_click = true;
      g_touch_mouse_bridge.snapped = true;
      g_touch_mouse_bridge.snap_point = g_last_tap.point;
      g_touch_mouse_bridge.snap_tolerance = double_tap_tolerance(g_last_tap.point);
    } else if (!begin_touch_mouse_bridge(point)) {
      g_last_tap = {};
      record_bridge_result(false, true);
      return false;
    }
    const POINT press_point = double_click ? g_last_tap.point : point;
    // a double click ends the pair (a third tap starts a new one); any other tap may be the first of one
    g_last_tap = double_click ? touch_tap_t {}
                              : touch_tap_t {true, message, mouse.time, point, g_touch_mouse_bridge.target_window};
    const bool moved = post_touch_mouse_bridge_message(
        WM_MOUSEMOVE, WM_NCMOUSEMOVE, 0, static_cast<WPARAM>(g_touch_mouse_bridge.hit_test), press_point);
    g_touch_mouse_bridge.pressed_buttons |= mouse_button_mask(message, mouse);
    const bool pressed = post_touch_mouse_bridge_message(
        double_click && wants_double_clicks(g_touch_mouse_bridge.target_window) ? double_click_message(message)
                                                                                : static_cast<UINT>(message),
        double_click ? non_client_double_click_message(message) : non_client_button_message(message),
        client_button_w_param(message, mouse), non_client_button_w_param(message, mouse), press_point);
    record_bridge_result(moved || pressed, !moved || !pressed);
    return moved && pressed;
  }
  if (is_mouse_button_up(message)) {
    if (!g_touch_mouse_bridge.active) {
      return false;
    }
    const POINT release_point = bridged_point(point);
    g_touch_mouse_bridge.pressed_buttons &= ~mouse_button_mask(message, mouse);
    const bool released = post_touch_mouse_bridge_message(
        static_cast<UINT>(message), non_client_button_message(message), client_button_w_param(message, mouse),
        non_client_button_w_param(message, mouse), release_point);
    const bool context_menu = message != WM_RBUTTONUP || post_touch_context_menu(release_point);
    if (g_touch_mouse_bridge.pressed_buttons == 0) {
      reset_touch_mouse_bridge(false);
    }
    const bool forwarded = released && context_menu;
    record_bridge_result(forwarded, !forwarded);
    return forwarded;
  }
  if (message == WM_MOUSEMOVE && g_touch_mouse_bridge.active) {
    const bool moved = post_touch_mouse_bridge_message(WM_MOUSEMOVE, WM_NCMOUSEMOVE,
                                                       g_touch_mouse_bridge.pressed_buttons,
                                                       static_cast<WPARAM>(g_touch_mouse_bridge.hit_test),
                                                       bridged_point(point));
    record_bridge_result(moved, !moved);
    return moved;
  }
  if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL) {
    const HWND target_window =
        g_touch_mouse_bridge.active ? g_touch_mouse_bridge.target_window : deepest_window_at(point);
    if (target_window == nullptr || !IsWindow(target_window)) {
      record_bridge_result(false, true);
      return false;
    }
    const WPARAM wheel_param = MAKEWPARAM(g_touch_mouse_bridge.active ? g_touch_mouse_bridge.pressed_buttons : 0,
                                          HIWORD(mouse.mouseData));
    POINT target_screen_point {};
    if (!physical_to_target_screen(target_window, point, target_screen_point)) {
      record_bridge_result(false, true);
      return false;
    }
    const bool forwarded = PostMessageW(target_window, static_cast<UINT>(message), wheel_param,
                                        MAKELPARAM(target_screen_point.x, target_screen_point.y)) != FALSE;
    if (!forwarded) {
      set_last_error_state(GetLastError());
    }
    record_bridge_result(forwarded, !forwarded);
    return forwarded;
  }
  return false;
}

// code/message/data：Windows 低级鼠标钩子的状态、消息类型和事件数据。
LRESULT CALLBACK low_level_mouse_proc(int code, WPARAM message, LPARAM data) {
  if (code != HC_ACTION || data == 0) {
    return CallNextHookEx(g_mouse_hook, code, message, data);
  }

  const auto *mouse = reinterpret_cast<const MSLLHOOKSTRUCT *>(data);
  if (is_touch_promoted_mouse(mouse->dwExtraInfo)) {
    if (g_state != nullptr) {
      InterlockedIncrement64(&g_state->observed_touch_mouse_events);
      InterlockedExchange(&g_state->last_touch_x, mouse->pt.x);
      InterlockedExchange(&g_state->last_touch_y, mouse->pt.y);
    }
    if (g_bridge_mode) {
      // 真实光标不动：把点击 / 拖动 / 滚轮直接交给触点下的窗口
      POINT bridge_point = mouse->pt;
      current_touch_route_point(bridge_point);
      bridge_promoted_touch_mouse(message, *mouse, bridge_point);
      return 1;
    }
    const auto now = GetTickCount64();
    const auto previous = static_cast<ULONGLONG>(
        InterlockedExchange64(&g_last_touch_event_tick, static_cast<LONG64>(now)));
    if (previous == 0 || now < previous || now - previous > kTouchSequenceGapMs) {
      // 在事件进入 Deskflow 钩子前先切回本机，保留 Windows 原生触摸坐标与命中测试。
      release_deskflow_keyboard();
    }
    // 原生触摸已独立投递；兼容鼠标必须在进入 Deskflow 和系统光标前终止。
    return 1;
  }

  if (is_promoted_pen_or_touch(mouse->dwExtraInfo)) {
    if (g_state != nullptr) {
      InterlockedIncrement64(&g_state->passed_pen_mouse_events);
    }
  } else if ((mouse->flags & LLMHF_INJECTED) == 0) {
    if (is_mouse_button_down(message)) {
      g_last_tap = {};  // a real click in between: the next tap starts afresh
    }
    if (g_state != nullptr) {
      InterlockedIncrement64(&g_state->passed_physical_mouse_events);
    }
  }

  return CallNextHookEx(g_mouse_hook, code, message, data);
}

int print_status() {
  HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kStateMappingName);
  if (mapping == nullptr) {
    std::wcout << L"stopped\n";
    return 1;
  }

  const auto *state = static_cast<const SharedState *>(
      MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(SharedState)));
  if (state == nullptr || state->version != kStateVersion) {
    if (state != nullptr) {
      UnmapViewOfFile(state);
    }
    CloseHandle(mapping);
    std::wcerr << L"failed to read compatible state\n";
    return 2;
  }

  std::wcout << L"running pid=" << state->process_id
             << L" observed_touch_mouse=" << state->observed_touch_mouse_events
             << L" observed_touch_route=" << state->observed_touch_route_events
             << L" deskflow_release_requests=" << state->deskflow_release_requests
             << L" deskflow_release_sent=" << state->deskflow_release_sent
             << L" deskflow_release_failures=" << state->deskflow_release_failures
             << L" passed_pen_mouse=" << state->passed_pen_mouse_events
             << L" passed_physical_mouse=" << state->passed_physical_mouse_events
             << L" last_touch=" << state->last_touch_x << L',' << state->last_touch_y
             << L" last_send=" << state->last_send_count
             << L" send_error=" << state->last_send_error
             << L" bridged_mouse=" << state->bridged_mouse_events
             << L" bridge_failures=" << state->bridge_send_failures
             << L" mode="
             << (state->bridge_mode != 0 ? L"independent-touch+compat-mouse-bridged"
                 : state->mouse_hook_enabled != 0
                     ? L"independent-touch+compat-mouse-blocked+deskflow-keyboard-release"
                     : L"deskflow-keyboard-release")
             << L'\n';
  UnmapViewOfFile(state);
  CloseHandle(mapping);
  return 0;
}

int stop_isolator() {
  HANDLE stop_event = OpenEventW(EVENT_MODIFY_STATE, FALSE, kStopEventName);
  if (stop_event == nullptr) {
    std::wcout << L"already stopped\n";
    return 0;
  }

  const BOOL signaled = SetEvent(stop_event);
  const DWORD error = signaled ? ERROR_SUCCESS : GetLastError();
  CloseHandle(stop_event);
  if (!signaled) {
    std::wcerr << L"failed to stop: " << error << L'\n';
    return 2;
  }

  std::wcout << L"stop requested\n";
  return 0;
}

int self_test() {
  const bool passed =
      is_touch_promoted_mouse(kPointerSignature | kTouchFlag) &&
      !is_touch_promoted_mouse(kPointerSignature) &&
      !is_touch_promoted_mouse(0) &&
      is_promoted_pen_or_touch(kPointerSignature) &&
      kDeskflowReleaseKey == VK_F24 &&
      kDeskflowKeyMessage == WM_APP + 0x0012;
  std::wcout << L"self-test=" << (passed ? L"passed" : L"failed")
             << L" mode=independent-touch+compat-mouse-blocked+deskflow-keyboard-release\n";
  return passed ? 0 : 3;
}

// virtual_key/name：测试投递的虚拟键码和输出名称。
int probe_deskflow_key(WORD virtual_key, const wchar_t *name) {
  DesktopContext desktop;
  if (!attach_to_input_desktop(desktop)) {
    return 2;
  }
  DWORD error = ERROR_SUCCESS;
  const LONG sent = post_deskflow_key(virtual_key, error);
  detach_from_input_desktop(desktop);
  const bool passed = sent == 2;
  std::wcout << L"probe=" << (passed ? L"passed" : L"failed")
             << L" key=" << name
             << L" posted=" << sent
             << L" error=" << error
             << L" mode=independent-touch+compat-mouse-blocked+deskflow-keyboard-release\n";
  return passed ? 0 : 3;
}

// block_touch_mouse: true 保留旧版兼容鼠标拦截；false 时仅在新触摸手势开始时释放 Deskflow 键盘。
int run_isolator(bool block_touch_mouse, bool bridge = false) {
  g_bridge_mode = bridge && block_touch_mouse;
  DesktopContext desktop;
  if (!attach_to_input_desktop(desktop)) {
    return 2;
  }

  HANDLE singleton = CreateMutexW(nullptr, FALSE, kSingletonName);
  if (singleton == nullptr) {
    std::wcerr << L"failed to create singleton: " << GetLastError() << L'\n';
    detach_from_input_desktop(desktop);
    return 2;
  }
  if (GetLastError() == ERROR_ALREADY_EXISTS) {
    CloseHandle(singleton);
    detach_from_input_desktop(desktop);
    return 0;
  }

  HANDLE stop_event = CreateEventW(nullptr, TRUE, FALSE, kStopEventName);
  if (stop_event == nullptr) {
    std::wcerr << L"failed to create stop event: " << GetLastError() << L'\n';
    CloseHandle(singleton);
    detach_from_input_desktop(desktop);
    return 2;
  }

  HANDLE mapping = CreateFileMappingW(
      INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SharedState),
      kStateMappingName);
  if (mapping == nullptr) {
    std::wcerr << L"failed to create state: " << GetLastError() << L'\n';
    CloseHandle(stop_event);
    CloseHandle(singleton);
    detach_from_input_desktop(desktop);
    return 2;
  }

  g_state = static_cast<SharedState *>(
      MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
  if (g_state == nullptr) {
    std::wcerr << L"failed to map state: " << GetLastError() << L'\n';
    CloseHandle(mapping);
    CloseHandle(stop_event);
    CloseHandle(singleton);
    detach_from_input_desktop(desktop);
    return 2;
  }
  *g_state = {};
  g_state->process_id = GetCurrentProcessId();

  if (!open_touch_route()) {
    std::wcerr << L"failed to create touch route: " << GetLastError() << L'\n';
    UnmapViewOfFile(g_state);
    g_state = nullptr;
    CloseHandle(mapping);
    CloseHandle(stop_event);
    CloseHandle(singleton);
    detach_from_input_desktop(desktop);
    return 2;
  }

  MSG bootstrap_message {};
  PeekMessageW(&bootstrap_message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
  g_main_thread_id = GetCurrentThreadId();
  if (block_touch_mouse) {
    g_mouse_hook = SetWindowsHookExW(
        WH_MOUSE_LL, low_level_mouse_proc, GetModuleHandleW(nullptr), 0);
    if (g_mouse_hook == nullptr) {
      std::wcerr << L"failed to install mouse hook: " << GetLastError() << L'\n';
      close_touch_route();
      UnmapViewOfFile(g_state);
      g_state = nullptr;
      CloseHandle(mapping);
      CloseHandle(stop_event);
      CloseHandle(singleton);
      detach_from_input_desktop(desktop);
      return 2;
    }
  }
  g_state->mouse_hook_enabled = block_touch_mouse ? 1 : 0;
  g_state->bridge_mode = g_bridge_mode ? 1 : 0;
  g_state->version = kStateVersion;

  bool running = true;
  while (running) {
    const DWORD wait_result =
        MsgWaitForMultipleObjects(1, &stop_event, FALSE, 10, QS_ALLINPUT);
    if (wait_result == WAIT_OBJECT_0) {
      break;
    }
    if (wait_result == WAIT_TIMEOUT) {
      poll_touch_route();
      continue;
    }
    if (wait_result != WAIT_OBJECT_0 + 1) {
      std::wcerr << L"message wait failed: " << GetLastError() << L'\n';
      break;
    }

    MSG message {};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
      if (message.message == WM_QUIT) {
        running = false;
        break;
      }
      if (message.message == kReleaseDeskflowKeyboardMessage) {
        InterlockedExchange(&g_release_pending, 0);
        release_deskflow_keyboard();
        continue;
      }
      TranslateMessage(&message);
      DispatchMessageW(&message);
    }
    poll_touch_route();
  }

  if (g_mouse_hook != nullptr) {
    UnhookWindowsHookEx(g_mouse_hook);
    g_mouse_hook = nullptr;
  }
  close_touch_route();
  UnmapViewOfFile(g_state);
  g_state = nullptr;
  CloseHandle(mapping);
  CloseHandle(stop_event);
  CloseHandle(singleton);
  detach_from_input_desktop(desktop);
  return 0;
}

}  // namespace

int wmain(int argc, wchar_t **argv) {
  const std::wstring_view command = argc >= 2 ? argv[1] : L"run";
  if (command == L"run" || command == L"start") {
    return run_isolator(true);
  }
  if (command == L"keyboard-release") {
    return run_isolator(false);
  }
  if (command == L"bridge") {
    return run_isolator(true, true);
  }
  if (command == L"stop") {
    return stop_isolator();
  }
  if (command == L"status") {
    return print_status();
  }
  if (command == L"self-test" || command == L"test") {
    return self_test();
  }
  if (command == L"probe") {
    return probe_deskflow_key(VK_F24, L"F24");
  }
  std::wcerr << L"usage: touch_mouse_isolator.exe [run|bridge|keyboard-release|stop|status|self-test|probe]\n";
  return 64;
}
