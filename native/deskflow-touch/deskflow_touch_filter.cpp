#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellscalingapi.h>

#include <cstdint>
#include <cstdlib>

#include "deskflow_touch_filter_shared.h"

#pragma comment(lib, "shcore.lib")

namespace {

using SetWindowsHookExWFunction = HHOOK(WINAPI *)(int, HOOKPROC, HINSTANCE, DWORD);

SetWindowsHookExWFunction g_real_set_windows_hook_ex = nullptr;
HOOKPROC g_deskflow_mouse_hook_proc = nullptr;
HOOKPROC g_deskflow_keyboard_hook_proc = nullptr;
HHOOK g_mouse_hook = nullptr;
HHOOK g_keyboard_hook = nullptr;
volatile LONG g_deskflow_message_thread_id = 0;
HANDLE g_state_mapping = nullptr;
HANDLE g_touch_route_mapping = nullptr;
deskflow_touch_filter::SharedState *g_state = nullptr;
deskflow_touch_filter::TouchRouteState *g_touch_route_state = nullptr;
LONG g_last_touch_route_sequence = 0;

constexpr WORD kDeskflowReleaseKey = VK_F24;

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

touch_mouse_bridge_state_t g_touch_mouse_bridge;

// The previous bridged tap, the first half of a possible double tap.
struct touch_tap_t {
  bool valid {};
  WPARAM button {};  // its button-down message
  DWORD time {};
  POINT point {};
  HWND window {};
};

touch_tap_t g_last_tap;

// 触摸路由由 Sunshine 写入；创建映射可保证 Sunshine 在下一次触摸时能够连接到该路由。
bool open_touch_route() {
  g_touch_route_mapping = CreateFileMappingW(
      INVALID_HANDLE_VALUE,
      nullptr,
      PAGE_READWRITE,
      0,
      sizeof(deskflow_touch_filter::TouchRouteState),
      deskflow_touch_filter::kTouchRouteMappingName);
  if (g_touch_route_mapping == nullptr) {
    return false;
  }

  const bool created = GetLastError() != ERROR_ALREADY_EXISTS;
  g_touch_route_state = static_cast<deskflow_touch_filter::TouchRouteState *>(
      MapViewOfFile(
          g_touch_route_mapping,
          FILE_MAP_ALL_ACCESS,
          0,
          0,
          sizeof(deskflow_touch_filter::TouchRouteState)));
  if (g_touch_route_state == nullptr) {
    CloseHandle(g_touch_route_mapping);
    g_touch_route_mapping = nullptr;
    return false;
  }
  if (created) {
    *g_touch_route_state = {};
    g_touch_route_state->version = deskflow_touch_filter::kTouchRouteVersion;
  }
  if (g_touch_route_state->version != deskflow_touch_filter::kTouchRouteVersion) {
    return false;
  }
  g_last_touch_route_sequence = g_touch_route_state->sequence;
  return true;
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

// 返回是否观察到由 Sunshine 发布的新触摸序列。
bool observe_new_touch_route() {
  if (g_touch_route_state == nullptr ||
      g_touch_route_state->version != deskflow_touch_filter::kTouchRouteVersion) {
    return false;
  }

  const LONG sequence = g_touch_route_state->sequence;
  if (sequence == g_last_touch_route_sequence) {
    return false;
  }
  g_last_touch_route_sequence = sequence;

  if (g_state != nullptr) {
    InterlockedIncrement64(&g_state->touch_route_events);
  }

  return true;
}

bool current_touch_route_point(POINT &point) {
  if (g_touch_route_state == nullptr ||
      g_touch_route_state->version != deskflow_touch_filter::kTouchRouteVersion) {
    return false;
  }

  for (int attempt = 0; attempt < 2; ++attempt) {
    const LONG sequence_before = InterlockedCompareExchange(
        &g_touch_route_state->sequence, 0, 0);
    const LONG x = InterlockedCompareExchange(&g_touch_route_state->screen_x, 0, 0);
    const LONG y = InterlockedCompareExchange(&g_touch_route_state->screen_y, 0, 0);
    const auto update_tick = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &g_touch_route_state->update_tick, 0, 0));
    const LONG sequence_after = InterlockedCompareExchange(
        &g_touch_route_state->sequence, 0, 0);
    const auto now = GetTickCount64();
    if (sequence_before != 0 && sequence_before == sequence_after &&
        update_tick <= now && now - update_tick <= deskflow_touch_filter::kPointerSequenceGapMs) {
      point = {x, y};
      return true;
    }
  }
  return false;
}

void consume_touch_route() {
  if (g_touch_route_state != nullptr &&
      g_touch_route_state->version == deskflow_touch_filter::kTouchRouteVersion) {
    g_last_touch_route_sequence = g_touch_route_state->sequence;
  }
}

// window/parameter: Deskflow 的消息窗口，以及接收所属线程 ID 的输出指针。
BOOL CALLBACK find_deskflow_message_window(HWND window, LPARAM parameter) {
  wchar_t class_name[64] {};
  if (GetClassNameW(window, class_name, ARRAYSIZE(class_name)) == 0 ||
      wcscmp(class_name, L"DeskflowDesk") != 0) {
    return TRUE;
  }

  DWORD process_id = 0;
  const DWORD thread_id = GetWindowThreadProcessId(window, &process_id);
  if (thread_id == 0 || process_id != GetCurrentProcessId()) {
    return TRUE;
  }

  *reinterpret_cast<DWORD *>(parameter) = thread_id;
  return FALSE;
}

// 返回处理 Deskflow 内部按键消息的线程；结果仅在当前核心进程生命周期内缓存。
DWORD deskflow_message_thread() {
  const auto cached = static_cast<DWORD>(
      InterlockedCompareExchange(&g_deskflow_message_thread_id, 0, 0));
  if (cached != 0) {
    return cached;
  }

  DWORD found = 0;
  EnumWindows(find_deskflow_message_window, reinterpret_cast<LPARAM>(&found));
  if (found == 0) {
    return 0;
  }

  InterlockedCompareExchange(
      &g_deskflow_message_thread_id, static_cast<LONG>(found), 0);
  return static_cast<DWORD>(
      InterlockedCompareExchange(&g_deskflow_message_thread_id, 0, 0));
}

// virtual_key/error: 要发送给 Deskflow 的虚拟按键，以及失败时返回的 Windows 错误码。
LONG post_deskflow_key(WORD virtual_key, DWORD &error) {
  const DWORD thread_id = deskflow_message_thread();
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
  const LPARAM down = static_cast<LPARAM>(1u | (scan_code << 16));
  const LPARAM up = down | static_cast<LPARAM>(0x80000000u);
  LONG sent = 0;
  if (!PostThreadMessageW(
          thread_id, deskflow_touch_filter::kDeskflowKeyMessage, key, down)) {
    error = GetLastError();
    return sent;
  }
  ++sent;
  if (!PostThreadMessageW(
          thread_id, deskflow_touch_filter::kDeskflowKeyMessage, key, up)) {
    error = GetLastError();
    return sent;
  }
  ++sent;
  error = ERROR_SUCCESS;
  return sent;
}

// 将 Deskflow 的键盘控制权切回本机；只在一段新的触摸操作开始时调用。
void release_deskflow_keyboard() {
  DWORD error = ERROR_SUCCESS;
  const LONG sent = post_deskflow_key(kDeskflowReleaseKey, error);
  if (g_state == nullptr) {
    return;
  }

  InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(error));
  if (sent == 2) {
    InterlockedIncrement64(&g_state->local_release_sent);
  } else {
    InterlockedIncrement64(&g_state->local_release_failures);
  }
}

void enable_local_keyboard() {
  if (g_state == nullptr) {
    return;
  }
  if (InterlockedExchange(&g_state->local_keyboard_mode, 1) == 0) {
    InterlockedIncrement64(&g_state->local_release_requests);
    release_deskflow_keyboard();
  }
}

void disable_local_keyboard() {
  if (g_state != nullptr && InterlockedExchange(&g_state->local_keyboard_mode, 0) != 0) {
    InterlockedIncrement64(&g_state->physical_mouse_reclaims);
  }
}

// message/mouse: 低级鼠标消息和事件数据；仅识别真实鼠标的按下事件。
bool is_physical_mouse_button_down(WPARAM message, const MSLLHOOKSTRUCT &mouse) {
  switch (message) {
    case WM_LBUTTONDOWN:
    case WM_RBUTTONDOWN:
    case WM_MBUTTONDOWN:
    case WM_XBUTTONDOWN:
    case WM_NCLBUTTONDOWN:
    case WM_NCRBUTTONDOWN:
    case WM_NCMBUTTONDOWN:
    case WM_NCXBUTTONDOWN:
      break;
    default:
      return false;
  }
  return !deskflow_touch_filter::is_promoted_pen_or_touch(mouse.dwExtraInfo) &&
         (mouse.flags & LLMHF_INJECTED) == 0;
}

// code/message/data: Windows 低级鼠标钩子的状态、消息和事件数据。
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

void reset_touch_mouse_bridge(bool cancel) {
  if (cancel && g_touch_mouse_bridge.active &&
      g_touch_mouse_bridge.root_window != nullptr &&
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
  if (g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
  }
  return false;
}

bool physical_to_target_client(HWND window, POINT physical_point, POINT &client_point) {
  POINT target_screen_point {};
  if (!physical_to_target_screen(window, physical_point, target_screen_point)) {
    return false;
  }

  const DPI_AWARENESS_CONTEXT target_context = GetWindowDpiAwarenessContext(window);
  if (target_context == nullptr) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, ERROR_INVALID_WINDOW_HANDLE);
    }
    return false;
  }
  const DPI_AWARENESS_CONTEXT previous_context =
      SetThreadDpiAwarenessContext(target_context);
  if (previous_context == nullptr) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
    }
    return false;
  }

  client_point = target_screen_point;
  const bool converted = ScreenToClient(window, &client_point) != FALSE;
  const DWORD error = converted ? ERROR_SUCCESS : GetLastError();
  SetThreadDpiAwarenessContext(previous_context);
  if (!converted && g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(error));
  }
  return converted;
}

// deskflow-core has no DPI manifest, so this thread is DPI unaware: WindowFromPoint / ChildWindowFromPointEx would read
// the physical touch point as a DPI-virtualised one and pick a window away from the finger on a scaled screen (the
// tablet's virtual display runs at 175 %: taps landed on the wrong control, 2026-10-09). The lookup runs per-monitor
// aware; the coordinate conversion for the target window switches to that window's own context.
struct per_monitor_dpi_scope {
  DPI_AWARENESS_CONTEXT previous;
  per_monitor_dpi_scope()
      : previous(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {}
  ~per_monitor_dpi_scope() {
    if (previous != nullptr) {
      SetThreadDpiAwarenessContext(previous);
    }
  }
  per_monitor_dpi_scope(const per_monitor_dpi_scope &) = delete;
  per_monitor_dpi_scope &operator=(const per_monitor_dpi_scope &) = delete;
};

HWND deepest_window_at(POINT physical_point) {
  const per_monitor_dpi_scope dpi_scope;
  HWND current = WindowFromPoint(physical_point);
  for (int depth = 0; current != nullptr && depth < 16; ++depth) {
    POINT client_point {};
    if (!physical_to_target_client(current, physical_point, client_point)) {
      break;
    }
    const HWND child = ChildWindowFromPointEx(
        current,
        client_point,
        CWP_SKIPDISABLED | CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
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
  if (SendMessageTimeoutW(
          window,
          WM_NCHITTEST,
          0,
          MAKELPARAM(target_screen_point.x, target_screen_point.y),
          SMTO_ABORTIFHUNG | SMTO_BLOCK,
          20,
          &result) == 0) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
    }
    return false;
  }
  hit_test = static_cast<LRESULT>(result);
  return true;
}

bool is_non_client_hit_test(LRESULT hit_test) {
  return hit_test != HTCLIENT && hit_test != HTNOWHERE &&
         hit_test != HTERROR && hit_test != HTTRANSPARENT;
}

bool begin_touch_mouse_bridge(POINT point) {
  reset_touch_mouse_bridge(true);

  const HWND target_window = deepest_window_at(point);
  if (target_window == nullptr || !IsWindow(target_window)) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, ERROR_INVALID_WINDOW_HANDLE);
    }
    return false;
  }

  const HWND root_window = GetAncestor(target_window, GA_ROOT);
  if (root_window == nullptr || !IsWindow(root_window)) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, ERROR_INVALID_WINDOW_HANDLE);
    }
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

bool post_touch_mouse_bridge_message(
    UINT client_message,
    UINT non_client_message,
    WPARAM client_w_param,
    WPARAM non_client_w_param,
    POINT point) {
  const HWND target_window = g_touch_mouse_bridge.non_client
                                 ? g_touch_mouse_bridge.root_window
                                 : g_touch_mouse_bridge.target_window;
  if (target_window == nullptr || !IsWindow(target_window)) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, ERROR_INVALID_WINDOW_HANDLE);
    }
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
  if (g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
  }
  return false;
}

bool post_touch_context_menu(POINT point) {
  const HWND target_window = g_touch_mouse_bridge.non_client
                                 ? g_touch_mouse_bridge.root_window
                                 : g_touch_mouse_bridge.target_window;
  if (target_window == nullptr || !IsWindow(target_window)) {
    if (g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, ERROR_INVALID_WINDOW_HANDLE);
    }
    return false;
  }
  POINT target_screen_point {};
  if (!physical_to_target_screen(target_window, point, target_screen_point)) {
    return false;
  }
  if (PostMessageW(
          target_window,
          WM_CONTEXTMENU,
          reinterpret_cast<WPARAM>(target_window),
          MAKELPARAM(target_screen_point.x, target_screen_point.y))) {
    return true;
  }
  if (g_state != nullptr) {
    InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
  }
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
  return message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
         message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN;
}

bool is_mouse_button_up(WPARAM message) {
  return message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
         message == WM_MBUTTONUP || message == WM_XBUTTONUP;
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

// Double taps. Windows turns a second press into WM_xBUTTONDBLCLK only while it processes real input, and it lines a
// touch double tap up with the first tap before promoting it to the mouse; posted taps got neither, so desktop icons
// (SysListView32, CS_DBLCLKS) and Explorer's item view (DirectUI pairs clicks itself within SM_CXDOUBLECLK = 4 px)
// never saw a double click (2026-10-10). A second tap of the same button on the same window within the double-click
// time and a finger-sized distance is posted at the first tap's point, as a double click where the class asks for one.
constexpr int kDoubleTapToleranceDip = 24;  // about 6 mm: the mouse's 4 px are far too tight for a finger

LONG double_tap_tolerance(POINT physical_point) {
  const per_monitor_dpi_scope dpi_scope;
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
  return g_last_tap.valid && g_last_tap.button == message &&
         time - g_last_tap.time <= GetDoubleClickTime() &&
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
bool wants_double_clicks(HWND window) {
  return (GetClassLongPtrW(window, GCL_STYLE) & CS_DBLCLKS) != 0;
}

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

// UWP windows (Windows.UI.Core.CoreWindow: the touch keyboard of TextInputHost.exe, Settings, Calculator …) take the
// native touch Sunshine injects as pointer input. A posted mouse click on top of it only does harm: on the touch
// keyboard the posted press on its top bar swallowed the tap on the close button (2026-10-09). For them the promoted
// mouse is just dropped — the cursor still does not move — and the touch itself does the work.
bool is_native_pointer_window(POINT physical_point) {
  const HWND window = deepest_window_at(physical_point);
  if (window == nullptr) {
    return false;
  }
  wchar_t class_name[64] {};
  if (GetClassNameW(window, class_name, ARRAYSIZE(class_name)) == 0) {
    return false;
  }
  return lstrcmpW(class_name, L"Windows.UI.Core.CoreWindow") == 0;
}

bool bridge_promoted_touch_mouse(
    WPARAM message,
    const MSLLHOOKSTRUCT &mouse,
    POINT point) {
  if (is_mouse_button_down(message)) {
    if (is_native_pointer_window(point)) {
      reset_touch_mouse_bridge(false);  // moves and the release are not bridged either (bridge inactive)
      g_last_tap = {};
      return true;
    }
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
    g_last_tap = double_click
                     ? touch_tap_t {}
                     : touch_tap_t {true, message, mouse.time, point, g_touch_mouse_bridge.target_window};

    const bool moved = post_touch_mouse_bridge_message(
        WM_MOUSEMOVE,
        WM_NCMOUSEMOVE,
        0,
        static_cast<WPARAM>(g_touch_mouse_bridge.hit_test),
        press_point);
    g_touch_mouse_bridge.pressed_buttons |= mouse_button_mask(message, mouse);
    const bool pressed = post_touch_mouse_bridge_message(
        double_click && wants_double_clicks(g_touch_mouse_bridge.target_window)
            ? double_click_message(message)
            : static_cast<UINT>(message),
        double_click ? non_client_double_click_message(message) : non_client_button_message(message),
        client_button_w_param(message, mouse),
        non_client_button_w_param(message, mouse),
        press_point);
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
        static_cast<UINT>(message),
        non_client_button_message(message),
        client_button_w_param(message, mouse),
        non_client_button_w_param(message, mouse),
        release_point);
    const bool context_menu = message != WM_RBUTTONUP || post_touch_context_menu(release_point);
    if (g_touch_mouse_bridge.pressed_buttons == 0) {
      reset_touch_mouse_bridge(false);
    }
    const bool forwarded = released && context_menu;
    record_bridge_result(forwarded, !forwarded);
    return forwarded;
  }

  if (message == WM_MOUSEMOVE && g_touch_mouse_bridge.active) {
    const bool moved = post_touch_mouse_bridge_message(
        WM_MOUSEMOVE,
        WM_NCMOUSEMOVE,
        g_touch_mouse_bridge.pressed_buttons,
        static_cast<WPARAM>(g_touch_mouse_bridge.hit_test),
        bridged_point(point));
    record_bridge_result(moved, !moved);
    return moved;
  }

  if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL) {
    const HWND target_window = g_touch_mouse_bridge.active
                                   ? g_touch_mouse_bridge.target_window
                                   : deepest_window_at(point);
    if (target_window == nullptr || !IsWindow(target_window)) {
      record_bridge_result(false, true);
      return false;
    }
    const WPARAM wheel_param = MAKEWPARAM(
        g_touch_mouse_bridge.active ? g_touch_mouse_bridge.pressed_buttons : 0,
        HIWORD(mouse.mouseData));
    POINT target_screen_point {};
    if (!physical_to_target_screen(target_window, point, target_screen_point)) {
      record_bridge_result(false, true);
      return false;
    }
    const bool forwarded = PostMessageW(
        target_window,
        static_cast<UINT>(message),
        wheel_param,
        MAKELPARAM(target_screen_point.x, target_screen_point.y)) != FALSE;
    if (!forwarded && g_state != nullptr) {
      InterlockedExchange(&g_state->last_send_error, static_cast<LONG>(GetLastError()));
    }
    record_bridge_result(forwarded, !forwarded);
    return forwarded;
  }

  return false;
}

LRESULT CALLBACK filtered_mouse_hook(int code, WPARAM message, LPARAM data) {
  if (code >= 0 && data != 0) {
    const auto *mouse = reinterpret_cast<const MSLLHOOKSTRUCT *>(data);
    if (deskflow_touch_filter::is_promoted_touch(mouse->dwExtraInfo)) {
      observe_new_touch_route();
      enable_local_keyboard();
      POINT bridge_point = mouse->pt;
      current_touch_route_point(bridge_point);
      if (g_state != nullptr) {
        InterlockedIncrement64(&g_state->touch_mouse_events);
        InterlockedExchange(&g_state->last_pointer_x, bridge_point.x);
        InterlockedExchange(&g_state->last_pointer_y, bridge_point.y);
      }
      bridge_promoted_touch_mouse(message, *mouse, bridge_point);
      return 1;
    }

    if (is_physical_mouse_button_down(message, *mouse)) {
      consume_touch_route();
      disable_local_keyboard();
      g_last_tap = {};  // a real click in between: the next tap starts afresh
    }

    if (deskflow_touch_filter::is_promoted_pen_or_touch(mouse->dwExtraInfo) &&
        g_state != nullptr) {
      InterlockedIncrement64(&g_state->pen_mouse_events);
      InterlockedExchange(&g_state->last_pointer_x, mouse->pt.x);
      InterlockedExchange(&g_state->last_pointer_y, mouse->pt.y);
    }
  }

  return g_deskflow_mouse_hook_proc == nullptr
             ? CallNextHookEx(g_mouse_hook, code, message, data)
             : g_deskflow_mouse_hook_proc(code, message, data);
}

// code/message/data: Windows 低级键盘钩子的状态、消息和事件数据。
LRESULT CALLBACK filtered_keyboard_hook(int code, WPARAM message, LPARAM data) {
  if (code >= 0 && data != 0) {
    const auto *keyboard = reinterpret_cast<const KBDLLHOOKSTRUCT *>(data);
    const bool injected = (keyboard->flags & LLKHF_INJECTED) != 0;
    if (!injected && observe_new_touch_route()) {
      enable_local_keyboard();
    }
    if (!injected && g_state != nullptr &&
        InterlockedCompareExchange(&g_state->local_keyboard_mode, 0, 0) != 0) {
      InterlockedIncrement64(&g_state->local_keyboard_events);
      return CallNextHookEx(g_keyboard_hook, code, message, data);
    }
  }

  return g_deskflow_keyboard_hook_proc == nullptr
             ? CallNextHookEx(g_keyboard_hook, code, message, data)
             : g_deskflow_keyboard_hook_proc(code, message, data);
}

void update_filter_state() {
  if (g_state == nullptr) {
    return;
  }
  InterlockedExchange(&g_state->mouse_filter_installed, g_mouse_hook != nullptr ? 1 : 0);
  InterlockedExchange(&g_state->keyboard_filter_installed, g_keyboard_hook != nullptr ? 1 : 0);
  InterlockedExchange(
      &g_state->filter_installed,
      g_mouse_hook != nullptr && g_keyboard_hook != nullptr ? 1 : 0);
}

// hook_id/procedure/module/thread_id: Deskflow 请求安装钩子时传入的原始参数。
HHOOK WINAPI filtered_set_windows_hook_ex(
    int hook_id,
    HOOKPROC procedure,
    HINSTANCE module,
    DWORD thread_id) {
  if (procedure == nullptr) {
    return g_real_set_windows_hook_ex(hook_id, procedure, module, thread_id);
  }

  if (hook_id == WH_MOUSE_LL) {
    g_deskflow_mouse_hook_proc = procedure;
    g_mouse_hook = g_real_set_windows_hook_ex(
        hook_id, filtered_mouse_hook, module, thread_id);
    update_filter_state();
    return g_mouse_hook;
  }
  if (hook_id == WH_KEYBOARD_LL) {
    g_deskflow_keyboard_hook_proc = procedure;
    g_keyboard_hook = g_real_set_windows_hook_ex(
        hook_id, filtered_keyboard_hook, module, thread_id);
    update_filter_state();
    return g_keyboard_hook;
  }
  return g_real_set_windows_hook_ex(hook_id, procedure, module, thread_id);
}

bool process_is_running(DWORD process_id) {
  if (process_id == 0) {
    return false;
  }
  const HANDLE process = OpenProcess(
      SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
  if (process == nullptr) {
    return false;
  }
  const bool running = WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
  CloseHandle(process);
  return running;
}

bool initialize_state() {
  g_state_mapping = CreateFileMappingW(
      INVALID_HANDLE_VALUE,
      nullptr,
      PAGE_READWRITE,
      0,
      sizeof(deskflow_touch_filter::SharedState),
      deskflow_touch_filter::kStateMappingName);
  if (g_state_mapping == nullptr) {
    return false;
  }
  const bool created = GetLastError() != ERROR_ALREADY_EXISTS;
  g_state = static_cast<deskflow_touch_filter::SharedState *>(
      MapViewOfFile(
          g_state_mapping,
          FILE_MAP_ALL_ACCESS,
          0,
          0,
          sizeof(deskflow_touch_filter::SharedState)));
  if (g_state == nullptr) {
    CloseHandle(g_state_mapping);
    g_state_mapping = nullptr;
    return false;
  }
  const bool reset_state = created ||
      g_state->version != deskflow_touch_filter::kStateVersion ||
      !process_is_running(g_state->process_id);
  if (reset_state) {
    *g_state = {};
    g_state->version = deskflow_touch_filter::kStateVersion;
    g_state->process_id = GetCurrentProcessId();
  }
  return true;
}

void close_state() {
  if (g_state != nullptr) {
    UnmapViewOfFile(g_state);
    g_state = nullptr;
  }
  if (g_state_mapping != nullptr) {
    CloseHandle(g_state_mapping);
    g_state_mapping = nullptr;
  }
}

bool patch_set_windows_hook_import() {
  const auto user32 = GetModuleHandleW(L"user32.dll");
  const auto module = reinterpret_cast<std::uint8_t *>(GetModuleHandleW(nullptr));
  if (user32 == nullptr || module == nullptr) {
    return false;
  }

  g_real_set_windows_hook_ex = reinterpret_cast<SetWindowsHookExWFunction>(
      GetProcAddress(user32, "SetWindowsHookExW"));
  if (g_real_set_windows_hook_ex == nullptr) {
    return false;
  }

  const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(module);
  if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
    return false;
  }
  const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(module + dos->e_lfanew);
  if (nt->Signature != IMAGE_NT_SIGNATURE) {
    return false;
  }

  const auto import_rva =
      nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
  if (import_rva == 0) {
    return false;
  }

  auto descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR *>(module + import_rva);
  for (; descriptor->Name != 0; ++descriptor) {
    auto thunk = reinterpret_cast<IMAGE_THUNK_DATA *>(module + descriptor->FirstThunk);
    for (; thunk->u1.Function != 0; ++thunk) {
      auto slot = reinterpret_cast<void **>(&thunk->u1.Function);
      if (*slot != reinterpret_cast<void *>(g_real_set_windows_hook_ex)) {
        continue;
      }

      DWORD old_protection = 0;
      if (!VirtualProtect(slot, sizeof(*slot), PAGE_READWRITE, &old_protection)) {
        return false;
      }
      InterlockedExchangePointer(
          slot, reinterpret_cast<void *>(filtered_set_windows_hook_ex));
      DWORD ignored = 0;
      VirtualProtect(slot, sizeof(*slot), old_protection, &ignored);
      FlushInstructionCache(GetCurrentProcess(), slot, sizeof(*slot));
      return true;
    }
  }
  return false;
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(instance);
    return initialize_state() && open_touch_route() && patch_set_windows_hook_import();
  }
  if (reason == DLL_PROCESS_DETACH) {
    close_touch_route();
    close_state();
  }
  return TRUE;
}
