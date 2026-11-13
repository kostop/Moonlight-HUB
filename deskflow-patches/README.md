# Moonlight Hub 随附键鼠组件

Deskflow 仅作为 Moonlight Hub 的随附组件维护、构建和分发。日常修复只提交本仓库，
不要求维护单独的 Deskflow fork，也不向 Deskflow 仓库推送更新。

上游固定为 Deskflow `v1.26.0`，提交 `760e3b99b00053647a96b405276bf614bd860075`。
`deskflow-1.26.0-moonlight-hub.patch` 是从这个版本起的完整定制补丁，包含逻辑显示器
路由、物理像素鼠标运动、带标记回位、修饰键状态处理及对应测试。
`deskflow-key-handoff.patch` 在完整补丁之后应用，修复本机按下按键、跨屏后松开时的
按键归属；它只释放实际收到过按下的电脑，不在每次切屏时强制松开所有按键。
`deskflow-modifier-release.patch` 继续修复修饰键松开被重复键、切屏 mark 或热键分支
过滤的问题；包含原窗口焦点保护及仅修饰键的诊断日志，不记录普通字符。
`deskflow-shared-input-state.patch` 保留接收端刷新前仍由接力拥有的合成按下与来源映射，
并加入按住鼠标禁止跨电脑切换的守卫。`deskflow-switch-recovery.patch` 独立保存本机
离开前的物理坐标，并在失败/断连时恢复到当前有效显示器位置；切换被拒绝时不提交
对端坐标。`deskflow-window-menu-mask.patch` 防止交接和内部临时修饰键释放被识别为
Alt/Win 单击而误激活菜单；普通同屏菜单用法保持原样。以上补丁按文件名顺序应用。

`deskflow-windows-motion-anchor.patch` 让停车定位和真实鼠标移动共用钩子事件顺序中的
坐标基准，避免切屏时拿新事件坐标减旧系统快照，产生上千像素的假反向移动。
正常快速移动、立即反向穿回及尚未消费的返回位移继续保留。
`deskflow-windows-pointer-clip.patch` 把钩子基准和回位落点按最近显示器裁剪，与 Windows
实际光标一致：穿屏时越过边缘的放行坐标不再让首批位移反向弹回本机；回位以确定落点
续算，不读回 SendInput 后仍可能停在停车中心的系统光标，避免被拉回屏幕中心。

上游下载仅是构建输入，位于被忽略的 `artifacts/`。触摸包装器、过滤器与键盘辅助程序
源码在本仓库的 [native/deskflow-touch](../native/deskflow-touch/README.md)。

## 构建与打包

需要 Git、MSVC 2022 x64、Windows SDK、CMake、Ninja、Qt 6.7+ MSVC x64 SDK，
以及 OpenSSL SDK 与匹配的运行 DLL。Qt、MSVC、OpenSSL 应与目标 x64 核心一致。

```powershell
.\deskflow-patches\build.ps1 `
    -QtRoot 'D:\SDK\Qt\6.10.1\msvc2022_64' `
    -OpenSslRoot 'D:\SDK\openssl' `
    -DependencySource 'D:\SDK\openssl\bin' `
    -Test

dotnet publish .\src\MoonlightHub.csproj -c Release -f net9.0-windows `
    -r win-x64 --self-contained false -o .\dist
.\tools\bundle-runtime.ps1 -Target .\dist
```

构建脚本下载固定上游版本，按文件名顺序应用本目录补丁，并生成
`artifacts/deskflow-runtime/`。该目录包含独立配置之外的核心、触摸适配、依赖 DLL、
来源清单与许可证；打包时只复制明确的运行文件，不复制用户配置、证书或旧绝对路径。
`runtime-manifest.json` 记录上游基准、补丁与核心的 SHA-256，供发布核验。

发布后的目录结构为：

```text
MoonlightHub.exe
deskflow/
  deskflow-core.exe                    # Hub 触摸包装器
  deskflow_touch_filter.dll
  moonlight-touch-keyboard-release.exe
  native/
    deskflow-core.exe                  # 定制核心及其 DLL
  runtime-manifest.json
  LICENSE
  LICENSES/
tools/
  moonlight-touch-bridge.exe
```

Windows Hub 优先从自身 `deskflow/` 加载完整组件。为升级已有安装，仍兼容过去由 Hub
管理的 `%LOCALAPPDATA%/MoonlightDeskflowBridge/vN`；不再搜索独立 Deskflow 的安装目录。
包装器按自身位置解析 `native/deskflow-core.exe`，发布包移动到含空格的目录仍可运行。

## 验证

`-Test` 运行不安装输入钩子、不注入真实键鼠的核心回归：鼠标积压与返回补偿、按键
归属、修饰键状态、逻辑路由、旧运动不重放，以及按钮与运动的事件顺序。
Hub 运行时路径选择还由 `tests/DisplaySafetyTests.csproj` 的纯文件快照测试覆盖。

补丁按上游项目的 SPDX 声明保留许可，发布目录随附上游 `LICENSE` 和 `LICENSES/`。
Qt/OpenSSL 运行库也需使用其允许再分发的版本并随附对应许可。

2026-11-13 的 Hub 2.0.22 构建已通过核心完整编译、712 项键盘相关断言、鼠标返回与
定位/路由回归、110 项 Hub 回归及随附组件包装检查；均未注入真实键鼠。
实际运行时已确认核心和触摸包装器来自 Hub 自身的 `deskflow/`，31 个清单文件的
SHA-256 一致，且 qingxian 已重连。修饰键手感与真实窗口焦点仍需现场操作验收。

后续卡键报告补充验证：586 项实际生产 `onKey` 检查，覆盖左右修饰键、同类 mask
未变和影子状态已经松开的情况；恢复旧 repeat/mark/hotkey 分支的突变均能触发失败。
另有 20 项生产桌面焦点检查、45 项包装器私有配置检查及 69 项包装流程检查。
包装器仅在私有配置中启用上游已有的 `win32KeepForeground=true`，共享布局文件保持
原值；没有保存前台窗口时，核心不再尝试附加线程或恢复前台。缓存准备支持对已经
校验过的补丁前缀追加后续补丁，不重新应用旧补丁或重置用户源码。

运行时日志中 `modifier source` 记录左右键、松开前后 mask 与切屏状态，
`modifier route` 记录实际转发目标。它们只能验证新版本收到和转发的事件，不能还原
旧版本未记录的卡键现场；当前本机系统修饰键与正常 Alt 按下/松开采样均已松开。

## Hub 2.0.23 输入交接

- 左、右、中、两个侧键及其全部组合保持按下时，边缘、普通快捷键和 F24 都不能
  跨到另一台电脑；本机多屏移动保留。全部松开不会自动执行旧切换，需新移动或
  新跳转请求。屏保和断连安全归位保留。
- 接收端 `updateKeyState()` 不再把自己注入的按下误归为实体键，明确的松开及离屏
  清理仍能找到原来源映射；已知实体事件接管和系统已报告松开的键不会恢复为接力拥有。
- 失败切换保留原电脑坐标；断连优先返回最后实际离开本机的位置，并按当前逐屏
  几何避开显示器空洞或已移除的屏幕。显式跳转书签单独保留；无可信历史时才使用安全回退。
- 菜单保护仅作用于跨屏释放、合成清理及 Windows KeyMap 的临时修饰键释放。使用
  带独立标签的 E8 掩码，不借用 Ctrl/Esc，不输入普通字符，不自动取消用户已经打开的菜单。
  正常 Alt/Win 单击、Alt-Tab、Alt-F 的原按键序列保留；当前旧菜单状态可由用户按 Esc 退出。

原理参考：[微软菜单键盘行为](https://learn.microsoft.com/en-us/windows/desktop/menurc/about-menus)、
[AutoHotkey 官方菜单掩码说明](https://raw.githubusercontent.com/AutoHotkey/AutoHotkeyDocs/v2/docs/lib/A_MenuMaskKey.htm)。
实现未复制 AutoHotkey 源码。

最终离线回归包括 632 项长按守卫、114 项位置恢复、254 项源端菜单交接、280 项接收端
菜单/真实 KeyMap、136 项刷新归属、588 项生产 onKey，以及原有 hook、路由和定位检查。
Hub Windows 路径迁移回归为 123 项（native calls: 0），更新器另有 7 项只读快照检查。
这些验证不向用户桌面注入测试按键。两台 Windows Hub 均已更新至 2.0.23，运行核心
来自各自 Hub 的 `deskflow/native`，31 个组件文件均通过清单校验；qingxian 客户端在
其交互会话内以 SYSTEM 运行，不再沿用独立旧核心。实际交接仍需结合用户操作与日志验收。

停车竞态补充回归：612 项真实生产鼠标回调及 120 项 Server 恢复/实际布局检查通过，
其余键盘、菜单、按钮长按、返回与定位回归也通过。同一时序输入下，冻结的旧代码
错误地产生 `(-1253,-36)`，新代码保留真实 `(3,-3)`。补丁序列准备成功，58 个涉及的
源码文件与实际编译输入逐项核对一致（仅忽略换行格式）。两端随附核心均更新至
SHA-256 `D64800C8A51854F8A7F364270E4E22142C747E79C7A2782F45530CAFC3F49116`，
各 31 个组件文件校验通过并重新连接；没有用自动输入代替用户实际穿屏验收。
