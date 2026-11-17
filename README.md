# Color and material handling in sw2urdf(visual)

This document explains how the exporter decides the RGB color written into the COLLADA
`<material>` for each part. It is the part of the fork most likely to surprise you, because
SolidWorks stores "color" in several different places depending on how the part was authored.

## 1. Resolution order

For every component the exporter tries the following sources, in order, and stops at the
first one that yields a *meaningful* color:

```
1. Component appearance      IComponent2.GetRenderMaterials2(swThisDisplayState, null)
2. Legacy component color    IComponent2.MaterialPropertyValues   (if HasMaterialPropertyValues())
3. Part document appearance  ModelDoc2.Extension.GetRenderMaterials2(...)
4. Part document color       ModelDoc2.MaterialPropertyValues
5. Per-face color            IFace2.MaterialPropertyValues / GetMaterialPropertyValues2
6. Fallback                  #C0C0C0 (SolidWorks default grey)
```

Steps 1 and 3 are the normal path. Steps 2/4 only exist for models authored with the old
"color" feature rather than an appearance. Step 5 is the safety net for parts whose
appearance is only visible through the face display color.

## 2. Sentinel values

SolidWorks returns *plausible looking* colors for entities that have no color at all, so the
exporter has to treat certain values as "not set" and keep looking:

| Value | Meaning | Treated as |
|---|---|---|
| `#FFFFFF` | no color on **textured** appearance | unset |
| `#C0C0C0` | document default grey | unset |
| `#000000` | black face colour | **kept** — a black face is written as black |

Important exception: for a **non-textured** appearance, `#FFFFFF` is a real color
(e.g. a white ABS part with an explicitly set white appearance) and is kept as-is.

## 3. Material vs appearance

SolidWorks has two independent things that both influence what you see:

- **Material** (`碳板`, `ABS`, `AISI 1020`, `thormel mat vma`, ...): a physical material. Some
  materials carry their own **textured** appearance; plain materials (ABS, aluminium, steel)
  only carry a base color.
- **Appearance**: what the user explicitly applies. Per SolidWorks rules an explicit
  appearance **wins** over the material's own appearance.

The exporter therefore does two passes over the appearance list of each component:

```
Pass 1 – non-textured appearances only (explicit appearance, plain material color)
         white is accepted, only #000000 / #C0C0C0 are rejected
Pass 2 – textured appearances (carbon fibre, thormel, ...)
         white / grey / black are rejected because the real color lives in the texture
```

## 4. Behaviour by part type

| Part authored as | What happens | Result in RViz2 |
|---|---|---|
| Plain material, no appearance (e.g. steel) | material base color used | light grey |
| Plain material + explicit color appearance | appearance color used | exact color |
| Plain material + explicit **white** appearance (ABS) | white accepted (pass 1) | white |
| Textured material, no appearance (carbon fibre) | white/black rejected → texture image is used | the carbon weave |
| Textured material + explicit color appearance | pass 1 picks the appearance, texture ignored | exact color |

## 5. Textured materials

Textures are exported since v3.0, but only for parts whose colour could **not** be resolved
at component level — i.e. parts that carry the material's own appearance and nothing else.

```
component colour resolved (#RRGGBB)?  ->  write flat colour, no texture
component colour N/A?                 ->  attach the material's texture image
```

Rationale: an explicit appearance always overrides the material appearance in SolidWorks, so
a part with a painted-on colour must not also get the carbon weave. In `export.log` the two
cases are distinguishable: `component=#234bbf` versus `component=N/A`.

What gets written for a textured material:

- `<library_images>` with `<init_from>../textures/<name>.jpg</init_from>`
- `surface` + `sampler2D` newparams, `<phong><diffuse><texture .../></diffuse>`
- a UV source and `<input semantic="TEXCOORD" offset="2" set="0"/>`

The image is copied into `meshes/textures/`, and spaces in the file name are replaced with
`_` because browser-based viewers cannot resolve a URI containing spaces.

**UVs are generated, not read from SolidWorks.** `IFace2.GetTessTriangles()` does not expose
usable per-face UVs for these bodies, so a triplanar projection is used: the dominant axis of
the triangle normal selects the projection plane, one tile covers 5 cm. Good enough for
tiling textures (carbon fibre, brushed metal); **not** suitable for decals or logos.

There is **no black-lifting step** any more: the `#292929` workaround of the 2.x series was
dropped when the colour resolution was rewritten for 3.x. A part whose colour really is
black is written as black. In practice this no longer matters for carbon parts, because they
get the texture image instead of a flat colour.

## 6. Debugging colors

Every exported component writes one line to `export.log`:

```
SaveDAE color: <component>  component=#RRGGBB|N/A  fromFace=<n>  gray=<n>  tris=<n>
```

| Field | Meaning |
|---|---|
| `component=#RRGGBB` | color resolved at component level |
| `component=N/A` | component-level lookup failed, per-face color was used |
| `fromFace` | number of faces that had to fall back to face-level color |
| `gray` | number of faces that ended on the `#C0C0C0` fallback (should normally be 0) |

If a part shows the wrong color, these three fields tell you which step of the chain failed.

## 7. Improvements over the original sw2urdf

Upstream exports one STL per link and gives every link a single flat colour
(it reads `ModelDoc2.MaterialPropertyValues` once per link). This fork keeps the whole
link/joint workflow but replaces the mesh output stage.

| Area | Upstream sw2urdf 1.6.1 | This fork |
|---|---|---|
| Visual mesh | one `.STL` per link, no colour data | one `.dae` per link, **multi-material** COLLADA |
| Visual colour | one `<material><color>` per link (usually the document default grey) | per-part colour resolved from appearances, several materials per link |
| Collision mesh | same STL as visual | unchanged: still `.STL` (FCL friendly) |
| Output layout | `meshes/` | `meshes/visual/` + `meshes/collision/` |
| Mesh quality | STL quality hardcoded by the exporter | optional "Reduce image quality (Coarse)" checkbox, restored after export; surface bodies always exported |
| URDF material | always written | suppressed for visuals so the DAE materials are not overridden by RViz |
| Texture materials | not applicable (STL has no colour) | texture images exported for parts without an explicit appearance, triplanar UV (see section 5) |
| Dependencies | MathNet / log4net / CsvHelper / solidworkstools | unchanged, no new third-party library; COLLADA is written with `System.Xml` |
| Registration | GUID `{65c9fc17-...}` | new GUID `{7b3f2c41-...}`, own install dir and ProgID, coexists with upstream |

What is **not** changed: the PropertyManager page, link tree, coordinate-system and axis
picking, automatic DOF detection, mass/inertia, joint limits, CSV import/export, package
scaffolding (`package.xml` / `CMakeLists.txt` / launch files).
## 8. Building

Requirements:

| Item | Notes |
|---|---|
| SolidWorks | 2020 or newer, needed for the API interop assemblies |
| .NET Framework | 4.5.2 (the project targets `net452`) |
| MSBuild | Visual Studio 2017+ Build Tools, or the .NET Framework 4.0 `MSBuild.exe` |
| git | optional, only used by `scripts/UpdateVersionInfo.ps1` |

`SolidWorks.Interop.*.dll` are **Dassault redistributables** and are deliberately not
committed. The project references them with a relative path, and `build.cmd` rewrites that
path to whatever is installed on your machine, builds, and restores the project file:

```bat
build.cmd
build.cmd "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
```

The script probes, in order:

```
%ProgramFiles%\SOLIDWORKS Corp\SOLIDWORKS\api\redist
%ProgramFiles%\SOLIDWORKS Corp\SOLIDWORKS
D:\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS\api\redist
D:\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS
C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist
C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS
```

`packages/` is git-ignored, so a fresh clone has no NuGet dependencies yet. `build.cmd`
downloads `nuget.exe` on first use, restores all 22 packages into `src\packages\`, and keeps
its cache under `.tools\` (also git-ignored). No Visual Studio required.

Output: `SW2URDF\bin\x64\Debug\SW2Visual.dll`. Install it with `install.cmd` from the release
package (it self-elevates and registers the COM add-in).
