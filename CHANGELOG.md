# sw2urdf(visual) — a fork of sw2urdf 1.6.1

## What this is

A complete fork of [ros/solidworks_urdf_exporter](https://github.com/ros/solidworks_urdf_exporter) 1.6.1.
**Only the mesh output stage is changed**: `<visual>` uses multi-material COLLADA (.dae),
`<collision>` keeps using STL.

Link / joint / coordinate systems / axes / mass properties / limits / CSV / package layout
are all inherited from upstream.

## Functional changes

| File | Change |
|---|---|
| `URDFExport/URDFPackage.cs` | Added `VisualDirectory` / `WindowsVisualDirectory`; output restructured to `meshes/visual/` and `meshes/collision/` |
| `URDFExport/ExportHelper.cs` | `ExportFiles()`: visual -> `.dae`, collision -> `.STL`; added `SaveDAE()` call, `SetReduceImageQuality()` and save/restore of image quality around the export |
| `URDFExport/DaeWriter.cs` | **New file.** Walks a link's components, tessellates faces, resolves per-face/per-part color, writes multi-material COLLADA |
| `URDF/Visual.cs` | Added `SuppressMaterial()` so a URDF `<material><color>` cannot override the DAE materials |
| `URDFExport/ExportHelperExtension.cs` | `ComputeVisualCollisionProperties` no longer writes material color |
| `URDFExport/ExportPropertyManager.cs` | Added checkbox "Reduce image quality (Coarse, smaller files)" |
| `SW/SwAddin.cs` | Plugin title `sw2urdf(visual)`, menu `Export as URDF(visual)`, new GUID, icons resolved relative to the DLL |
| `Properties/AssemblyInfo.cs` | `[assembly: ComVisible(false)]` so only the add-in class is registered (avoids ProgID clashes with the original) |

## Engineering changes

Upstream targets C# 7, but this machine only ships the .NET Framework C# 5 compiler,
so the source was **downgraded syntactically (semantics unchanged)**:

- `get => expr;` / `set => expr;` -> `get { return ...; }` / `set { ... }`
- Getter-only auto-properties `{ get; }` -> `{ get; private set; }`
- Inline `out int x` -> declare first, then `out x`
- Pattern matching `is T x` -> `as` + null check
- `= default;` -> `= null;` / `default(T)`
- Dictionary index initializers `["k"] = v` -> explicit assignment
- Removed `Test/` (unit tests) and the related NuGet references
- Removed the NuGet `EnsureNuGetPackageBuildImports` verification target
- `PreBuildEvent` path `$(SolutionDir)` -> `$(ProjectDir)..\`

## Build

```bat
cd fork\SW2URDF
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" SW2URDF.csproj ^
   /p:Configuration=Debug /p:Platform=x64 /p:RegisterForComInterop=false "/p:PostBuildEvent="
```

Output: `fork\SW2URDF\bin\x64\Debug\SW2Visual.dll`

> The NuGet DLLs under `..\packages\` were copied back from the installed URDFExporter
> directory (MathNet / CsvHelper / log4net / solidworkstools).

## Coexistence with the installed SW2URDF

| Item | Original | This plugin |
|---|---|---|
| GUID | `{65c9fc17-...}` | `{7b3f2c41-9d6a-4e2b-8c15-2f9a6d31c4e7}` |
| Install dir | `...\URDFExporter\` | `...\URDFExporter_Sw2Visual\` |
| Registered ProgIDs | many | only `SW2URDF.SW.SwAddin` |
| Menu | Export as URDF | Export as URDF(visual) |

## Known issues / TODO

1. **Textures are not supported yet.** Only RGB base color is exported.
2. If a link owns a single component, the DAE may capture less geometry than the STL (to be confirmed).
3. DAE files are 3-20x larger than STL (text format + per-face vertices). Consider enabling
   "Reduce image quality (Coarse)" when exporting.
4. Materials are deduplicated by color (tolerance 0.004), so same-colored parts share one material.

## Version history (colour handling)

| Version | Change |
|---|---|
| 1.3 | first fork build: component appearance + legacy colour |
| 1.4 | added part-document appearance fallback; output split into `meshes/visual` + `meshes/collision` |
| 1.5 | per-face colour, but white was taken as a real colour -> whole model turned white |
| 2.2 | component colour restored as primary; face colour demoted to fallback |
| 2.4 | struct vertex keys (fixed hang + 8 GB memory blow-up from string keys) |
| 2.5 | `IsUnset`: white / `#C0C0C0` no longer stop the lookup chain |
| 2.6 | appearance picker: two passes (plain vs textured), textured white rejected |
| 2.7 | `LiftBlack`: near-black lifted to `#292929` so carbon parts are not silhouettes |
| 2.8 | white accepted for **non-textured** appearances (white ABS parts were falling through to black) |

See `README.md` for the full resolution order and material/appearance rules.
## v3.x summary (visual fork, texture + STEP surfaces)

| Version | Change |
|---|---|
| 3.0 | texture export: `<library_images>`, `sampler2D`, TEXCOORD, triplanar UV; texture only for parts without an explicit appearance; per-link material ID prefix |
| 3.1–3.5 | experimental UI switches (removed), material ID collision fix |
| 3.6 | `builder.IdPrefix` actually assigned — IDs become `Hatch_Link_mat5` |
| 3.7 | diagnostic log `SAEDBG ... sheets=...` (removed in 3.13) |
| 3.8–3.12 | DAE Settings UI experiments, reverted |
| **3.13** | **UI back to the 3.0 baseline; surface bodies (`swSheetBody`) always exported** |
| 3.14 | material ID prefix re-applied — it had been lost when the tree was rebased on 3.0 |
| 3.15 | installer prints an environment self-check (.NET, SolidWorks location, API folder) |
| 3.16 | scripts renamed `install.cmd` / `delete.cmd` (were `1-安装.cmd` / `2-卸载.cmd`) |
| 3.17 | dead-code cleanup; single-part export now writes a DAE |

Notes on 3.13:

- STEP imported parts keep their shell: `RGM3 v3.1外` goes from 10,524 to 953,249 triangles
  (`base_link.dae` 0.9 MB → 104 MB).
- Surface tessellation follows the document image quality; `Reduce image quality` is the
  only lever exposed in the UI.

Notes on 3.17 (code review follow-up, P0 + P1):

- Removed the temporary `ProbeTextures()` diagnostic that ran for every component and
  duplicated the material/tessellation queries.
- Fixed an orphan `{ }` block that logged `no component color` for all 158 parts regardless
  of the outcome.
- Removed dead members: `ExportTextures`, `_lastTexture`, `IsWhite()`, `LiftBlack()`, and the
  URDF texture copy in `ExportFiles()` (URDF materials are suppressed).
- `AddComponent()` and the new `AddDocument()` now share `AddBodies()`; the duplicate
  filename-sanitising code was replaced by `DaeWriterSafeName()`.
- Single-part export (`ExportLink()`) previously wrote STL for both visual and collision, so
  it lost every colour. It now writes `meshes/visual/<name>.dae` and falls back to STL only
  on failure.
- The `Reduce image quality` checkbox was created with `options = 0` and never enabled, so it
  was invisible in the UI. It is now created visible.