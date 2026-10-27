在 Unity 执行 `Tools > Gaussian Splat LOD > Create Demo`，生成 65,536 点 SH3 binary PLY，调用官方 native AutoChunkLod 并创建可运行的 `Assets/GaussianSplatLOD/Demo.unity`。生成数据位于 StreamingAssets，可随 Windows 构建。

URP 工程先执行 Install URP Render Feature。可自行替换组件的 JSON Configuration 加载已有数据。Play 自动锁定鼠标，直接移动鼠标转向，WASD/QE 漫游，Shift 加速，ESC 显示鼠标，左键恢复。
