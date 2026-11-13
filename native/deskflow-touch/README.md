# Hub 内置 Deskflow 触摸组件

这些源码从原工作区的 `deskflow/touch/` 收入 Moonlight-HUB，由 Hub 统一构建和分发。
它们不是独立 Deskflow 产品或上游 Deskflow 源码仓库；源文件快照及 SHA-256 见 `SOURCE.json`。
原文件未附单独的许可证声明，此处保留其原有版权/许可状态，不擅自另行授权。
上游 Deskflow 核心及许可证由 Hub 的核心源码构建流程单独保留。

在仓库根目录运行：

```powershell
native/deskflow-touch/build.ps1 -OutputDir "$env:TEMP\moonlight-hub-deskflow-touch"
```

需要 Visual Studio C++ x64 工具和 Windows SDK，不需要 Qt 或单独安装 Deskflow。
脚本使用静态 CRT 构建包装器、触摸过滤 DLL 和键盘释放/触摸桥接程序，并运行只读自检；
不会安装钩子、启动 Deskflow、注入输入或更改用户配置。默认输出为
`artifacts/deskflow-touch`，中间文件集中在该目录的 `obj/`，不应纳入版本控制。
构建还会复制组件到临时的含空格目录，验证默认定位、相对路径覆盖与缺失核心检测；
这些检查仅调用 `--touch-filter-paths`，使用不可执行的占位核心，不会启动子进程。

发布目录布局：

```text
deskflow/
  deskflow-core.exe                    # 包装器
  deskflow_touch_filter.dll
  moonlight-touch-keyboard-release.exe
  native/
    deskflow-core.exe                  # Hub 定制核心
    Qt6*.dll, libcrypto-3-x64.dll, libssl-3-x64.dll
tools/
  moonlight-touch-bridge.exe
```

包装器默认相对自身目录启动 `native/deskflow-core.exe`，以该 native 目录作为工作目录，
从而保留核心的 Qt/OpenSSL 依赖。可选 `deskflow-core.real-path.txt` 也支持相对包装器目录
的路径；发布包无需保存开发机器的绝对路径。`--touch-filter-paths` 只打印并校验这些路径，
不启动核心，可用于移动发布目录后的打包检查。

原有 v25 运行时的触摸过滤 DLL 与迁入前 `deskflow_touch_filter.dll` SHA-256 相同，
键盘释放程序与迁入前 `touch_mouse_isolator.exe` SHA-256 相同。包装器旧产物哈希不同，
仅凭现有记录不能保证可重建为相同字节；发布应使用本目录源码新构建的包装器。
