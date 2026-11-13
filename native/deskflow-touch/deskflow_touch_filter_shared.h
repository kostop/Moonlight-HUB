#pragma once

#include <windows.h>

#include <cstdint>

namespace deskflow_touch_filter {

constexpr wchar_t kStateMappingName[] = L"Local\\DeskflowTouchFilter.State";
constexpr wchar_t kTouchRouteMappingName[] = L"Local\\MoonlightTouchInjection.Route";
constexpr DWORD kStateVersion = 4;
constexpr DWORD kTouchRouteVersion = 1;
constexpr ULONG_PTR kPointerSignature = 0xFF515700ULL;
constexpr ULONG_PTR kPointerSignatureMask = 0xFFFFFF00ULL;
constexpr ULONG_PTR kTouchFlag = 0x00000080ULL;
constexpr UINT kDeskflowKeyMessage = WM_APP + 0x0012;
constexpr ULONGLONG kPointerSequenceGapMs = 250;

struct alignas(8) SharedState {
  DWORD version;
  DWORD process_id;
  volatile LONG filter_installed;
  volatile LONG mouse_filter_installed;
  volatile LONG keyboard_filter_installed;
  volatile LONG local_keyboard_mode;
  volatile LONG64 touch_mouse_events;
  volatile LONG64 bridged_mouse_events;
  volatile LONG64 bridge_send_failures;
  volatile LONG64 pen_mouse_events;
  volatile LONG64 local_release_requests;
  volatile LONG64 local_release_sent;
  volatile LONG64 local_release_failures;
  volatile LONG64 local_keyboard_events;
  volatile LONG64 physical_mouse_reclaims;
  volatile LONG64 touch_route_events;
  volatile LONG last_pointer_x;
  volatile LONG last_pointer_y;
  volatile LONG last_send_error;
};

struct alignas(8) TouchRouteState {
  DWORD version;
  volatile LONG sequence;
  volatile LONG screen_x;
  volatile LONG screen_y;
  volatile LONG64 update_tick;
};

// extra_info: Windows 低级鼠标事件携带的指针来源标记。
inline bool is_promoted_pen_or_touch(ULONG_PTR extra_info) {
  const auto value = extra_info & 0xFFFFFFFFULL;
  return (value & kPointerSignatureMask) == kPointerSignature;
}

// extra_info: Windows 低级鼠标事件携带的指针来源标记。
inline bool is_promoted_touch(ULONG_PTR extra_info) {
  const auto value = extra_info & 0xFFFFFFFFULL;
  return is_promoted_pen_or_touch(value) && (value & kTouchFlag) != 0;
}

}  // namespace deskflow_touch_filter
