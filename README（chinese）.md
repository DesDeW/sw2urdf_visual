# sw2urdf(visual) 中的颜色与材质处理

本文说明导出器如何为每个零件决定写入 COLLADA `<material>` 的 RGB 颜色。这是本 fork 里
最容易出乎意料的部分，因为 SolidWorks 依据零件的建模方式，会把“颜色”存放在好几个不同的位置。

## 1. 取值顺序

对每个零部件，导出器按以下顺序尝试，取到第一个**有意义**的颜色即停止：

```
1. 零部件外观          IComponent2.GetRenderMaterials2(swThisDisplayState, null)
2. 零部件旧式颜色      IComponent2.MaterialPropertyValues   （需 HasMaterialPropertyValues()）
3. 零件文档外观        ModelDoc2.Extension.GetRenderMaterials2(...)
4. 零件文档颜色        ModelDoc2.MaterialPropertyValues
5. 面级颜色            IFace2.MaterialPropertyValues / GetMaterialPropertyValues2
6. 兜底                #C0C0C0（SolidWorks 默认灰）
```

第 1、3 步是常规路径。第 2、4 步只对用旧版“颜色”特征（而非外观）建模的模型有效。
第 5 步是安全网，用于外观只能通过面显示色体现的零件。

## 2. 哨兵值

SolidWorks 会给“其实没有颜色”的实体返回一个**看起来很像真的**颜色，所以导出器必须把某些值
视为“未设置”并继续往下找：

| 值 | 含义 | 处理 |
|---|---|---|
| `#FFFFFF` | **纹理外观**上没有颜色 | 视为未设置 |
| `#C0C0C0` | 文档默认灰 | 视为未设置 |
| `#000000` | 面上的黑色 | **保留** —— 黑色的面会写成黑色 |

重要例外：对**非纹理**外观，`#FFFFFF` 是真实颜色（例如显式设了白色外观的白色 ABS 件），
会原样保留。

## 3. 材质与外观

SolidWorks 里有两样互相独立、但都会影响观感的东西：

- **材质**（`碳板`、`ABS`、`AISI 1020`、`thormel mat vma` …）：物理材质。部分材质自带
  **纹理**外观；普通材质（ABS、铝、钢）只带一个基础色。
- **外观**：用户显式施加的。按 SolidWorks 规则，显式外观**优先于**材质自带外观。

因此导出器会对每个零部件的外观列表做两轮筛选：

```
第 1 轮 – 只看非纹理外观（显式外观、普通材质颜色）
         白色接受，只拒绝 #000000 / #C0C0C0
第 2 轮 – 纹理外观（碳纤维、thormel 等）
         白/灰/黑都拒绝，因为真实颜色在贴图里
```

## 4. 按零件类型的实际行为

| 零件建模方式 | 发生什么 | RViz2 里的结果 |
|---|---|---|
| 普通材质、无外观（如钢） | 用材质基础色 | 浅灰 |
| 普通材质 + 显式颜色外观 | 用外观颜色 | 准确颜色 |
| 普通材质 + 显式**白色**外观（ABS） | 第 1 轮接受白色 | 白色 |
| 纹理材质、无外观（碳板） | 白/黑被拒 → 使用贴图 | 碳板编织纹 |
| 纹理材质 + 显式颜色外观 | 第 1 轮选中外观，忽略纹理 | 准确颜色 |

## 5. 纹理材质

自 1.0（内部构建 3.0）起支持导出贴图，但**只对无法在零部件级取到颜色的零件**生效——也就是只挂了材质自带外观、
没有额外覆盖外观的零件。

```
能取到零部件颜色（#RRGGBB）？  →  写纯色，不挂贴图
零部件颜色为 N/A？             →  挂上该材质的贴图
```

理由：SolidWorks 里显式外观总是覆盖材质外观，所以一个被涂成蓝色的零件不应该同时带上碳板纹理。
在 `export.log` 里这两种情况可区分：`component=#234bbf` 与 `component=N/A`。

带贴图的材质会写入：

- `<library_images>` 与 `<init_from>../textures/<名字>.jpg</init_from>`
- `surface` + `sampler2D` 两个 newparam，以及 `<phong><diffuse><texture .../></diffuse>`
- 一个 UV 源和 `<input semantic="TEXCOORD" offset="2" set="0"/>`

图片会复制到 `meshes/textures/`，文件名中的空格替换为 `_`，因为浏览器类查看器无法解析含空格的 URI。

**UV 是生成出来的，不是从 SolidWorks 读取的。** `IFace2.GetTessTriangles()` 对这些实体
不提供可用的逐面 UV，因此使用三平面投影：按三角形法线的主轴选择投影平面，一张贴图覆盖 5 cm。
对平铺纹理（碳纤维、拉丝金属）足够；**不适合**贴花、logo。

**已经没有提亮步骤了**：2.x 系列里的 `#292929` 变通做法在重写颜色解析时（1.0）被移除。
颜色确实是黑色的零件就按黑色导出。实际影响不大，因为碳板件现在拿到的是贴图而不是纯色。

## 6. 调试颜色

每个导出的零部件都会往 `export.log` 写一行：

```
SaveDAE color: <零部件>  component=#RRGGBB|N/A  fromFace=<n>  gray=<n>  tris=<n>
```

| 字段 | 含义 |
|---|---|
| `component=#RRGGBB` | 在零部件级取到了颜色 |
| `component=N/A` | 零部件级取值失败，用了面级颜色 |
| `fromFace` | 有多少个面退回到面级颜色 |
| `gray` | 有多少个面落到 `#C0C0C0` 兜底（正常应为 0） |

如果某个零件颜色不对，这三个字段能告诉你链条在哪一步断掉。

## 7. 相对原版 sw2urdf 的改进

上游每个 link 导出一个 STL，并给整个 link 一个纯色（它只读一次 `ModelDoc2.MaterialPropertyValues`）。
本 fork 保留完整的 link/joint 流程，只替换了网格输出环节。

| 方面 | 上游 sw2urdf 1.6.1 | 本 fork |
|---|---|---|
| 视觉网格 | 每 link 一个 `.STL`，无颜色信息 | 每 link 一个 `.dae`，COLLADA **多材质** |
| 视觉颜色 | 每 link 一个 `<material><color>`（通常是文档默认灰） | 按零件从外观解析，一个 link 内可有多种材质 |
| 碰撞网格 | 与视觉相同的 STL | 不变：仍是 `.STL`（对 FCL 友好） |
| 输出结构 | `meshes/` | `meshes/visual/` + `meshes/collision/` |
| 网格精度 | 导出器硬编码 STL 精度 | 可选 `Reduce image quality (Coarse)` 复选框，导出后恢复；曲面实体始终导出 |
| URDF 材质 | 总会写入 | 视觉部分不再写，避免 RViz 覆盖 DAE 材质 |
| 纹理材质 | 不适用（STL 无颜色） | 对无显式外观的零件导出贴图，UV 为三平面投影（见第 5 节） |
| 依赖 | MathNet / log4net / CsvHelper / solidworkstools | 不变，未引入新的第三方库；COLLADA 用 `System.Xml` 手写 |
| 注册 | GUID `{65c9fc17-...}` | 新 GUID `{7b3f2c41-...}`，独立安装目录与 ProgID，可与上游共存 |

**没有**改动的部分：PropertyManager 页面、link 树、坐标系与轴的选择、自由度自动识别、
质量与惯量、关节限位、CSV 导入导出、包的脚手架（`package.xml` / `CMakeLists.txt` / launch 文件）。
## 8. 构建

依赖：

| 项 | 说明 |
|---|---|
| SolidWorks | 2020 或更新，提供 API 互操作程序集 |
| .NET Framework | 4.5.2（项目目标为 `net452`） |
| MSBuild | VS 2017+ 生成工具，或 .NET Framework 4.0 的 `MSBuild.exe` |
| git | 可选，只有 `scripts/UpdateVersionInfo.ps1` 会调用 |

`SolidWorks.Interop.*.dll` 是**达索的再分发组件**，本仓库刻意不包含。项目里用相对路径引用它们，
`build.cmd` 会按本机实际安装位置临时改写该路径，编译完成后自动还原项目文件：

```bat
build.cmd
build.cmd "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
```

脚本按以下顺序探测：

```
%ProgramFiles%\SOLIDWORKS Corp\SOLIDWORKS\api\redist
%ProgramFiles%\SOLIDWORKS Corp\SOLIDWORKS
D:\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS\api\redist
D:\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS
C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist
C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS
```

`packages/` 被 git 忽略，所以刚 clone 下来的仓库没有 NuGet 依赖。`build.cmd` 会在首次运行时
自动下载 `nuget.exe`、把 22 个包还原到 `src\packages\`，缓存放在 `.tools\`（同样被忽略）。
不需要安装 Visual Studio。

产物：`SW2URDF\bin\x64\Debug\SW2Visual.dll`，用发布包里的 `install.cmd` 安装（会自提权并注册 COM 加载项）。
