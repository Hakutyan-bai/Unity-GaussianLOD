# 2026-10-27：Buffer 上限、模型整体性和 FPS 漫游修复

## 修改

- `GaussianGPUResources` 使用 `SystemInfo.maxGraphicsBufferSize` 与 `int.MaxValue` 的较小值约束**每个** Gaussian Buffer，并按 256 字节记录向下取整。总显存预算不能用于放大单个缓冲。4096 MiB / 500 万点不再尝试创建 2,576,777,216 字节缓冲。
- `GaussianStreamingSession` 合并同文件连续的常驻/粗级区间，优先准备整个模型的粗级底图。所有分区粗级准备齐后一次发布，后续始终保留底图，视角转向和 IO 失败不会先失去对应区域。
- 默认 Coherent LOD：先提升可见区域的共同精度；加载中的实际细化前沿也受到约束，减少一块已到 L0 而邻近大片仍在 L3/L4 的情况。像素足够小的区域仍可保持粗级。
- 逐级细化和默认 0.4 秒光学厚度渐变，旧数据在过渡结束前保护驻留。按完整区域计算稳态点数及旧/新共存点数。取消按 GPU slot 顺序截断可见数组；快速转向或另一相机揭示更多区域超预算时回退到完整粗级集合。
- 每个分区两个 float 的 GPU 渐变参数缓冲，过渡时不重传全部可见索引。剔除包围盒加入 Gaussian 尾部裕量和移动预加载裕量。
- `GaussianLODDemoCamera`：Play 默认锁定隐藏鼠标，直接鼠标转向，WASD/QE、Shift 加速，ESC 释放、左键恢复。失焦/禁用释放鼠标。X rotation 限制 120°～170°，重新捕获修复 Unity 等价 Euler 表示导致的翻转。提供 Enable FPS Roaming 菜单并支持场景中唯一的未标记 Game 相机。

## 实际验证

设备：NVIDIA RTX 4050 Laptop，Direct3D12，Unity 2022.3.62f3c1。

- 27 项 Editor 测试全通过，另加 1 项 FPS pitch/重新捕获测试通过。记录分别为工程 `Reports/GaussianSplatLOD/continuity-tests-final.json`、`fps-pitch-test.json`。
- Buffer 规划测试重现 4096 MiB / 500 万点并检查 2 GiB 和 1 GiB 单缓冲设备上限，不为单元测试分配数 GB 数据。当前真实场景同一 4096 MiB / 500 万配置实际加载成功：59 个分区已覆盖，无 Buffer 超限错误。GPU 总分配约 2.114 GiB，是 arena 与排序等多个缓冲之和，单 arena 小于 2 GiB。
- 逐帧流式回归：受限增量上传时未完成首屏不发布零散分块；首次发布完整粗级；过渡中分区覆盖数量不下降，旧/新绘制总数不超排序容量；共同细化前沿不跨多级跳变。
- GPU 帧缓冲测试：同一 Gaussian 的 25% + 75% 光学厚度渐变，与单个完整 Gaussian 的 alpha / 红通道差值在 0.015 内，避免线性 alpha 交叉淡化的明显变暗。
- 真实已有数据 `G:/SUIT/edit/qiepian/JN_LOD1/lod-meta.json`，7,261,819 源点、126 PLY、59 分区。512 MiB GPU/CPU、100 万点排序预算，1280×720，近/远/非均匀负缩放三个视角验证通过。最新 Coherent LOD 记录：首次整体粗级约 **0.484 秒**，其后 **273 个采样帧最少覆盖 59 个分区**，36 帧处于渐变。报告 `preview-builtin-coherent-JN.json` 及同名阶段 PNG。该时间只表示本机本次缓存条件下的结果，不是保证值。
- 当前 Game View 真实鼠标操作：捕获后 `Cursor.lockState=Locked`、`visible=false`；按 Escape 后 `None`、`true`；pitch=170 且范围 [120,170]。记录 `fps-cursor-captured.json`、`fps-cursor-escaped.json`。
- 修改后 C# 编译与 Unity Console 检查无错误。

## 限制

本次未重新发布 Windows Player；之前 Builds 中的 exe 是修复前产物，应重新 Build 后使用这些修改。单 arena 容量仍受约 2 GiB 限制，超过时降低有效细节预算，尚未实现多个 Gaussian arena。Coherent LOD 和渐变不能修复原始训练数据或离线粗级的过大 Gaussian；低点预算下仍会有模糊和细节变化。这里的覆盖验证确认每个分区有可用数据，不等于对所有像素或视觉接缝的无误差保证。
