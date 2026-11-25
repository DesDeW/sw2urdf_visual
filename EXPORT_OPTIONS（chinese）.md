# 导出选项说明

本文说明 **sw2urdf(visual) 1.0** 导出向导里的每一个选项。

内容刻意分成两部分：

1. 该选项在**原版**插件里是做什么的
   （Stephen Brawner 的 `solidworks_urdf_exporter` 1.6.1，本 fork 的上游）
2. 本 fork **改动或新增**了什么

只看第 2 部分会看不懂，因为向导的绝大部分仍是上游代码，术语也是上游的。

---

## 1. SolidWorks PropertyManager 面板（第 1 步）

这是固定在 SolidWorks 窗口左侧的面板，在任何对话框弹出之前出现。

| 选项 | 原版行为 | 本 fork |
|---|---|---|
| **Link Name** | 当前正在配置的 link 名称，根节点默认 `base_link`。写入 `<link name=...>` | 不变 |
| **Global Origin Coordinate System** | 所有 link 位姿的参考坐标系 | 不变 |
| **Link Components** | 组成该 link 的零部件 | 不变 |
| **Number of child links** | 该 link 有几个子节点，决定树的形状 | 不变 |
| **Load Configuration...** | 载入之前保存的 CSV 配置，免去重新选择 | 不变 |
| **Preview and Export...** | 进入关节页、link 页，然后导出 | 不变 |
| ☐ **Compute Mass and Inertia** | 读取 SolidWorks 质量属性，写入 `<mass>` 与 6 个 `<inertia>` 分量；不勾则写 `0` | 不变 |
| ☐ **Compute Visual and Collision** | 是否重新导出该 link 的网格 | 开关不变，但**产物变了**：视觉用 `.dae`，碰撞仍是 `.stl` |
| ☐ **Compute Joint Kinematics** | 导出关节的坐标原点与轴向 | 不变 |
| ☐ **Compute Joint Limits** | 读取装配配合的限位，写入 `<limit>` | 不变 |
| ☐ **Reduce image quality (Coarse, smaller files)** | — | **新增**。导出期间临时把*文档属性 → 图像品质*设为 Coarse，剖分完 DAE 后恢复原值。三角形更少、文件更小 |

> 其中 4 个 `Compute ...` 复选框在上游代码里**默认隐藏**（`options = 0`），只有点
> `Load Configuration...` 载入 CSV 之后才显示；这是上游行为，刻意没有改。
> `Reduce image quality` 是例外：它创建时就可见，因为不受 CSV 配置驱动。

---

## 2. Configure Joint Properties（第 2 步）

上游页面，未改动。每个字段都直接对应一个 URDF `<joint>` 元素。

| 字段 | URDF 元素 | 说明 |
|---|---|---|
| **Joint Name** | `<joint name=...>` | |
| **Joint Type** | `<joint type=...>` | `fixed`、`revolute`、`continuous`、`prismatic` |
| **Coordinates** | — | 该关节引用的 SolidWorks 坐标系 |
| **Axis** | `<axis xyz=...>` | 本地轴向；`fixed` 不用 |
| **Origin → Position / Orientation** | `<origin xyz rpy>` | 由坐标系自动算出，可手动修改 |
| **Limit → lower / upper / effort / velocity** | `<limit ...>` | `revolute` / `prismatic` 必填 |
| **Calibration → rising / falling** | `<calibration>` | 可选 |
| **Dynamics → friction / damping** | `<dynamics>` | 可选 |
| **Safety Controller** | `<safety_controller>` | 可选 |
| **Mimic Other Joint** | `<mimic>` | 联动关节（齿轮、四连杆） |

底部提示：**留空的字段不会写进 URDF**。

---

## 3. Configure Link Properties（第 3 步）

### 3.1 上游字段

| 字段 | 原版行为 | 在本 fork 里的状态 |
|---|---|---|
| **Inertial → Origin (m)** | 惯量坐标系偏移，`<inertial><origin>` | 有效 |
| **Inertial → Moment of Inertia** | 6 个惯量分量 | 有效 |
| **Mass (kg)** | `<mass value=...>` | 有效 |
| **Visual and Collision Meshes → Origin (m)** | 网格相对 link 坐标系的偏移 | 有效 |
| **Color → Red / Green / Blue / Alpha** | 写入 `<material><color rgba=...>` | **无效**。本 fork 不写 URDF 材质，颜色烘进 DAE |
| **Mesh Detail → Course / Fine** | STL 导出的 `swSTLQuality` | **只影响碰撞 STL**。视觉网格密度由图像品质决定（见 `Reduce image quality`） |
| **Material name** | `<material name=...>` | **无效**，原因同上 |
| **Texture (Replaces Color)** | 上游把 `<texture filename=...>` 写进 URDF 材质 | **无效**。RViz 忽略该元素；真正的贴图在 DAE 里 |
| **Browse...** | 给上面那个字段选文件 | 同上 |

### 3.2 本 fork 的固定行为

界面上**没有 DAE 相关的选项**，以下两件事固定执行：

| 行为 | 说明 |
|---|---|
| **贴图** | 取不到颜色（`N/A`）的零件挂材质贴图；有显式外观的零件保持纯色。图片写入 `meshes/textures/`，材质 ID 按 link 加前缀 |
| **曲面实体** | 除实体外，`swSheetBody` 曲面实体始终一并剖分。STEP 导入的零件因此不会丢掉外壳 |

第二项有代价：`RGM3 v3.1外` 有 4,652 个曲面体，剖分后约 94 万三角形，`base_link.dae`
从 0.9 MB 涨到约 104 MB。觉得太大就勾 `Reduce image quality`。

---

## 4. 界面上有但不起作用的选项

这一节特意写出来，因为这些都是继承来的，很容易浪费时间：

| 选项 | 为什么无效 |
|---|---|
| **Color (R/G/B/A)** | fork 调用了 `Visual.SuppressMaterial()`，完全不写 URDF 材质。颜色来自 DAE |
| **Material name** | 同上 |
| **Texture (Replaces Color)** | 同上；自 1.0 起连文件都不再拷进包里 |
| **Mesh Detail (Course/Fine)** | 视觉网格已不用 STL，所以它只改变碰撞精度 |

---

## 5. 最终写了哪些文件

```
<输出目录>/
├─ urdf/<机器人名>.urdf
├─ meshes/
│  ├─ visual/<link>.dae          ← 颜色 + 可选贴图，每 link 一个
│  ├─ collision/<link>.STL       ← 精度由 Mesh Detail 控制
│  └─ textures/<名字>.jpg        ← 仅对带材质贴图的零件产生
├─ launch/
└─ config/
```

DAE 内部按零部件的取色规则：

```
有显式外观？        →  把该颜色写成 <phong><diffuse>
无外观但有纹理？    →  写贴图
两者都没有？        →  退回面级颜色，最后退回纯灰
```

单零件导出（`PartExportForm`）走同一个写入器：几何来自 `IPartDoc.GetBodies2()`，颜色来自
文档自身的外观，因此单零件包同样会生成 `meshes/visual/<名字>.dae`。若 DAE 写入失败，
视觉网格回退为 STL。

---

## 6. 常见场景配置

| 目标 | 设置 |
|---|---|
| **最接近 SolidWorks 观感** | 不勾 `Reduce image quality`，并把文档图像品质调高 |
| **只要运动规划，包越小越好** | 勾上 `Reduce image quality`；贴图仍会导出，但三角形大幅减少 |
| **STEP 导入的零件在 DAE 里缺了一大块** | 曲面实体默认已导出；若仍显得空，才需要提高图像品质 |
| **浏览器里看不到纹理** | 用 `file://` 打开时属预期；起 HTTP 服务或用 Blender 打开 |

---

## 7. 已知限制

- UV 是**生成**的，不是从 SolidWorks 取的：`GetTessTriangles` 对这些实体不提供可用的逐面
  UV，所以用三平面投影（一张贴图覆盖 5 cm）。适合碳纤维这类平铺纹理，**不适合**贴花、logo。
- 材质 ID 带 link 前缀（`Hatch_Link_mat5`），避免把多个 link 文件导入同一场景时按名字合并材质。
- `Mesh Detail` 与 `Reduce image quality` 都会临时修改 **SolidWorks 全局用户首选项**。
  如果导出中途崩溃，被改的设置不会被恢复。
