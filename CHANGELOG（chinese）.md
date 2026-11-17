# 变更记录（中文）

英文版见 `CHANGELOG.md`，两份内容保持一致。

## v3.x 汇总（visual fork：贴图 + STEP 曲面）

| 版本 | 变更 |
|---|---|
| 3.0 | 导出贴图：`<library_images>`、`sampler2D`、TEXCOORD、三平面 UV；仅对无显式外观的零件挂贴图 |
| 3.1–3.5 | 界面开关实验（已移除）、材质 ID 冲突修复 |
| 3.6 | 补上 `builder.IdPrefix` 赋值，材质 ID 变为 `Hatch_Link_mat5` |
| 3.7 | 新增诊断日志 `SAEDBG ... sheets=...`（3.13 已移除） |
| 3.8–3.12 | DAE Settings 界面实验，全部回退 |
| **3.13** | **界面回到 3.0 基线；曲面实体（`swSheetBody`）始终导出** |
| 3.14 | 重新补上材质 ID 前缀（3.13 以 3.0 为基线重建时丢失） |
| 3.15 | 安装脚本增加环境自检（.NET、SolidWorks 位置、API 目录） |
| 3.16 | 脚本更名为 `install.cmd` / `delete.cmd`（原 `1-安装.cmd` / `2-卸载.cmd`） |
| 3.17 | 清理废弃代码；单零件导出改为输出 DAE |

### 3.13 说明

- STEP 导入的零件保留外壳：`RGM3 v3.1外` 从 10,524 三角形增加到 953,249
  （`base_link.dae` 从 0.9 MB 增至 104 MB）。
- 曲面剖分精度跟随文档图像品质，界面里唯一可调的杠杆是 `Reduce image quality`。

3.17 说明（代码审查 P0 + P1）：

- 删除临时诊断 `ProbeTextures()`：它对每个零部件重复调用材质与剖分查询。
- 修复孤立 `{ }` 代码块导致 158 个零件全部打印 `no component color` 的问题。
- 删除废弃成员：`ExportTextures`、`_lastTexture`、`IsWhite()`、`LiftBlack()`，以及
  `ExportFiles()` 里已无引用的 URDF 贴图复制逻辑。
- `AddComponent()` 与新增的 `AddDocument()` 共用 `AddBodies()`；重复的文件名净化逻辑
  统一为 `DaeWriterSafeName()`。
- 单零件导出（`ExportLink()`）原先 visual 与 collision 都写 STL，颜色全部丢失；现改为
  生成 `meshes/visual/<名字>.dae`，仅在失败时回退 STL。
- `Reduce image quality` 复选框原先用 `options = 0` 创建且从未启用，界面里看不到；
  现改为创建即可见。
- 安装包不再内置 `SolidWorks.Interop.*.dll`，改由安装脚本从本机 SolidWorks 目录复制。
- 新增 `build.cmd` / `build.ps1`：自动探测 SolidWorks API 与 MSBuild，编译后还原项目文件。

## 开源相关

- 新增 `LICENSE`（MIT，含上游版权行）与 `THIRD-PARTY-NOTICES`。
- 源码目录由 `fork/` 更名为 `src/`。
- 项目引用改为相对路径，不再依赖硬编码的绝对路径。