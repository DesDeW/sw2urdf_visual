# Export Options

This document describes every option in the export wizard of **sw2urdf(visual) 1.0**.

It is split into two parts on purpose:

1. what the option does in the **original** plugin
   (Stephen Brawner's `solidworks_urdf_exporter` 1.6.1, the upstream this fork is based on)
2. what this fork **changed or added**

Reading only part 2 will not make sense, because most of the wizard is still upstream
code and upstream terminology.

---

## 1. SolidWorks PropertyManager panel (step 1)

This is the panel pinned on the left side of the SolidWorks window, before any dialog opens.

| Option | Upstream behaviour | In this fork |
|---|---|---|
| **Link Name** | Name of the link currently being configured. The root defaults to `base_link`. Written to `<link name=...>`. | unchanged |
| **Global Origin Coordinate System** | Coordinate system every link pose is measured against. | unchanged |
| **Link Components** | The selected components that make up this link. | unchanged |
| **Number of child links** | How many children this link has; drives the shape of the tree. | unchanged |
| **Load Configuration...** | Reload a previously saved CSV configuration instead of re-picking everything. | unchanged |
| **Preview and Export...** | Opens the joint page, then the link page, then exports. | unchanged |
| ☐ **Compute Mass and Inertia** | Reads SolidWorks mass properties and writes `<mass>` plus the six `<inertia>` components. Unchecked leaves them at `0`. | unchanged |
| ☐ **Compute Visual and Collision** | Re-exports the meshes for this link. | unchanged as a switch, but the **output changed**: visual is now `.dae`, collision stays `.stl` |
| ☐ **Compute Joint Kinematics** | Exports joint origin and axis. | unchanged |
| ☐ **Compute Joint Limits** | Reads the assembly mate limits and writes `<limit>`. | unchanged |
| ☐ **Reduce image quality (Coarse, smaller files)** | — | **added.** Temporarily sets *Document Properties → Image Quality* to Coarse while the DAE is tessellated, then restores the previous value. Fewer triangles, smaller DAE |

> The four `Compute ...` checkboxes are **hidden by default** in the upstream code
> (`options = 0`) and only become visible after `Load Configuration...` loads a CSV.
> That is upstream behaviour and was deliberately not changed. `Reduce image quality` is the
> exception: it is created visible, because it is not driven by a CSV configuration.

---

## 2. Configure Joint Properties (step 2)

Upstream page, unchanged. Every field maps directly to a URDF `<joint>` element.

| Field | URDF element | Notes |
|---|---|---|
| **Joint Name** | `<joint name=...>` | |
| **Joint Type** | `<joint type=...>` | `fixed`, `revolute`, `continuous`, `prismatic` |
| **Coordinates** | — | the SolidWorks coordinate system this joint references |
| **Axis** | `<axis xyz=...>` | local axis; not used by `fixed` |
| **Origin → Position / Orientation** | `<origin xyz rpy>` | computed from the coordinate system, editable |
| **Limit → lower / upper / effort / velocity** | `<limit ...>` | required for `revolute` / `prismatic` |
| **Calibration → rising / falling** | `<calibration>` | optional |
| **Dynamics → friction / damping** | `<dynamics>` | optional |
| **Safety Controller** | `<safety_controller>` | optional |
| **Mimic Other Joint** | `<mimic>` | for coupled joints (gears, four-bars) |

Bottom note: **blank fields are not written to the URDF.**

---

## 3. Configure Link Properties (step 3)

### 3.1 Upstream fields

| Field | Upstream behaviour | Status in this fork |
|---|---|---|
| **Inertial → Origin (m)** | Inertial frame offset, `<inertial><origin>` | works |
| **Inertial → Moment of Inertia** | the six inertia terms | works |
| **Mass (kg)** | `<mass value=...>` | works |
| **Visual and Collision Meshes → Origin (m)** | mesh offset relative to the link frame | works |
| **Color → Red / Green / Blue / Alpha** | written as `<material><color rgba=...>` | **no effect.** This fork suppresses the URDF material; the colour is baked into the DAE instead |
| **Mesh Detail → Course / Fine** | `swSTLQuality` for the STL export | **only affects collision STL.** Visual density is governed by image quality (see `Reduce image quality`) |
| **Material name** | `<material name=...>` | **no effect**, same reason as Color |
| **Texture (Replaces Color)** | upstream writes `<texture filename=...>` into the URDF material | **no effect.** RViz ignores that element; real textures are embedded in the DAE |
| **Browse...** | picks the file for the field above | same |

### 3.2 Behaviour that is fixed in this fork

There are **no DAE-specific options in the UI**. Two things always happen:

| Behaviour | Detail |
|---|---|
| **Textures** | A part whose colour resolves to `N/A` inherits its material texture; a part with an explicit appearance keeps the flat colour. Images go to `meshes/textures/`, IDs are prefixed per link |
| **Surface bodies** | `swSheetBody` bodies are always tessellated in addition to solid bodies. STEP imported parts keep their shell, which would otherwise be silently dropped |

The second one is not free: `RGM3 v3.1外` carries 4,652 surface bodies which tessellate to
about 943 k triangles, so `base_link.dae` grows from 0.9 MB to roughly 104 MB. Use
`Reduce image quality` if that is too heavy.

---

## 4. Options that exist but do nothing

Worth calling out, because they are inherited and can waste time:

| Option | Why it does nothing |
|---|---|
| **Color (R/G/B/A)** | The fork calls `Visual.SuppressMaterial()`: URDF materials are not written at all. Colour comes from the DAE |
| **Material name** | same |
| **Texture (Replaces Color)** | same; since 1.0 the file is not even copied into the package any more |
| **Mesh Detail (Course/Fine)** | visual meshes no longer use STL, so this only changes collision fidelity |

---

## 5. What actually lands on disk

```
<output folder>/
├─ urdf/<roboton>.urdf
├─ meshes/
│  ├─ visual/<link>.dae          ← colour + optional texture, one file per link
│  ├─ collision/<link>.STL       ← Mesh Detail controls the density
│  └─ textures/<name>.jpg        ← only for parts that carry a material texture
├─ launch/
└─ config/
```

Colour decision inside the DAE, per component:

```
explicit appearance found?  ->  write that colour as <phong><diffuse>
no appearance, has texture? ->  write the texture image
otherwise                   ->  fall back to the per-face colour, then to a flat grey
```

Single-part export (`PartExportForm`) goes through the same writer: the bodies come from
`IPartDoc.GetBodies2()` and the colours from the document's own appearances, so a single-part
package also contains `meshes/visual/<name>.dae`. If the DAE cannot be written, the visual
mesh falls back to the STL.

---

## 6. Recipes

| Goal | Settings |
|---|---|
| **Closest to what SolidWorks shows** | leave `Reduce image quality` unchecked, raise the document image quality |
| **Smallest package, for motion planning only** | tick `Reduce image quality`; textures are still exported but far fewer triangles |
| **STEP-imported part looks gutted in the DAE** | surface bodies are exported by default; if it still looks empty, raise the image quality |
| **Textures do not show up in a browser viewer** | expected over `file://`; serve the folder over HTTP, or open it in Blender |

---

## 7. Known limitations

- UVs are **generated**, not taken from SolidWorks: `GetTessTriangles` does not expose
  usable per-face UVs for these bodies, so a triplanar projection is used (one tile per
  5 cm). Fine for tiling textures such as carbon fibre; **not** fine for decals or logos.
- Material IDs are prefixed with the link name (`Hatch_Link_mat5`) so that importing
  several link files into one scene does not merge materials by name.
- `Mesh Detail` and `Reduce image quality` touch **global SolidWorks user preferences**
  during the export. If SolidWorks crashes mid-export, the modified setting is not
  restored.
