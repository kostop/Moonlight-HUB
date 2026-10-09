# 显示器安全回归

使用 .NET 9 x64 SDK，从 `MoonlightHub` 仓库根目录运行：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run -c Release --project tests/DisplaySafetyTests.csproj
```

测试直接引用生产项目，通过模拟显示器快照校验布局目标。覆盖副屏断开、GDI 编号被内屏复用、虚拟屏尚未就绪、虚实复制模式、设备编号变化、正常独立虚拟屏以及用户明确调整物理屏。另覆盖客户端模式请求的提前退出，以及历史 Sunshine 显示目标和 2000×1200 配置的安全恢复。

测试不会启动 Hub。生产程序集的全部 P/Invoke 都被阻止；测试不读取真实显示器，不应用显示设置，不读取或改写真实用户配置。测试工程输出保存在 `%LOCALAPPDATA%\MoonlightHub\build-tests`，生产工程沿用原构建目录，不需要额外 NuGet 测试框架。通过标准：进程退出码为 0，全部用例通过，`native calls: 0`。
