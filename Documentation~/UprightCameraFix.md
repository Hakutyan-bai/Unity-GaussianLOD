# 天空盒与模型投影一致性修复

2026-10-27，在当前 `Assets/GaussianSplatLOD/Demo.unity` 应用并保存：

- 相机移除 180° roll，保留原 forward 方向，保持世界 Y 朝上。位置高度按已有范围夹到 60。
- 原倒置 X 范围 120..170 自动迁移到直立 10..60，高度 60..100；鼠标恢复正常 FPS 方向。
- 模型保持原位置 (0,0,0) 与原数据坐标转换 (0,180,180)，不跟随相机旋转或移动。
- Gaussian Shader 使用 Unity 的 `UNITY_MATRIX_P`。之前用 `camera.targetTexture != null` 判断 `GL.GetGPUProjectionMatrix` 的翻转并不覆盖 Game View 内部 RT、URP 中间目标等情况，会使 Gaussian 与 Unity 天空盒/普通几何的屏幕响应不一致。
- 手动投影仅保留为脱离 Camera 的 GPU 单元测试开关，运行时显式关闭该开关。

验证：相机直立/旧角度迁移、Shader 编译、光学渐变 3 项通过；新增相机水平及俯视 25° 时 Gaussian 与普通 Unity 白色 Cube 的渲染像素重心对齐测试 2 项通过，容差 1.5 像素，Builtin/DX12。结果位于工程 `Reports/GaussianSplatLOD/upright-tests.json` 和 `projection-alignment-tests.json`。Play Game View 已目视确认建筑直立、天空朝向一致；相机 up 与世界 up 点积为正，模型 transform 保持原值。Console 无错误。本次没有重打包旧 Windows Player。

天空盒仍是无限远背景：转动相机会正常改变看到的天空方向，平移时没有近景视差；修复的是它与模型的坐标和投影约定，不将天空锁定在屏幕像素上。
