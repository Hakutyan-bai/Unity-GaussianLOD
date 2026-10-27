# 原生转换器

Editor 直接启动 `Editor/Converter/Native~/splat-transform.exe`。`~` 目录不进入 Unity 资产导入；Editor 代码和原生工具不进入 Player。Windows 需要支持该原生发行版的系统；CPU PLY/AutoChunkLod 不初始化 Vulkan。

随包二进制来自官方 `@manycore/aholo-splat-transform-win32-x64-msvc@2.1.0` 的 registry tarball，校验 SHA512 后提取。无需安装 npm 或 Node。许可证与依赖清单见 ThirdPartyNotices。

Assets `.unitypackage` 发行包将原生程序放在 `Editor/Converter/Native/splat-transform.exe.bytes`，首次调用时复制到 `Library/GaussianSplatLOD/Native/2.1.0/` 后启动。该资源只用于 Editor；UPM 安装继续直接使用 Native~ 中的原始程序。

如需自行构建，请在独立的 Aholo checkout 按 `packages/splat-transform-native/source/README.md` 准备 CMake 3.31+、C++20、完整 vcpkg 及 DXC/匹配 shader headers，再执行官方 `cmake --workflow --preset x64-windows-static`。这不是 Unity 的构建步骤。窗口允许指定另一个本机 native executable；版本必须支持 v1 Pipeline 和 PLY bundle。

生成的 Pipeline 使用绝对 UTF-8 文件路径；C# 参数传递仅包含带引号的 Pipeline 路径，数据路径由 JSON 转义，不拼接到 shell。UseShellExecute=false，CreateNoWindow=true。无自创 CLI 参数。

转换使用一个 Read、一个 AutoChunkLod 和一个 Write；AutoChunkLod 后 release source，Write 后 release lod。后台进程的 stdout/stderr 进入有界显示日志；实际输出可能在原生块处理期间才刷新，进度仅表示阶段。窗口重编译或关闭时终止独立进程。取消后的专属临时目录由用户决定是否删除。

官方 bundle Write 会递归替换指定目录，因此插件只将随机唯一的 staging 路径交给原生程序。既有非空输出目录拒绝处理；转换完成校验后才将文件提交至目标。向空目录提交时先移 PLY、最后移 JSON，防止读取半成品。
