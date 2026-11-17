// DaeWriter.cs -- NEW: export a link's components into a multi-material COLLADA (.dae)
//
// Design rules:
//   1. Vertices are baked into the link joint coordinate system (<visual><origin> stays 0)
//   2. One color per part, but a link may hold parts of different colors -> several materials per .dae
//   3. Collision keeps using STL and is unaffected
//   4. Output goes to the visual mesh folder
//   5. Image quality is user controlled (optionally downgraded during export)

using MathNet.Numerics.LinearAlgebra;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using SW2URDF.URDF;
using SW2URDF.Utilities;

namespace SW2URDF.URDFExport
{
    internal struct DaeVec
    {
        public double X, Y, Z;
        public DaeVec(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    internal sealed class DaeMaterial
    {
        public int Index;
        public float R, G, B, A;
        public string Prefix = "";
        public string TextureFile;     // absolute path on disk, may be null
        public string TextureRel;      // ../textures/xxx.jpg as written into the DAE

        public bool Matches(double[] c, string tex)
        {
            return Math.Abs(R - c[0]) < 0.004 && Math.Abs(G - c[1]) < 0.004 &&
                   Math.Abs(B - c[2]) < 0.004 && Math.Abs(A - c[3]) < 0.004 &&
                   string.Equals(TextureFile, tex, StringComparison.OrdinalIgnoreCase);
        }
        public string Id { get { return Prefix + "mat" + Index.ToString(CultureInfo.InvariantCulture); } }
        public string FxId { get { return Prefix + "fx" + Index.ToString(CultureInfo.InvariantCulture); } }
        public string Name
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "rgb_{0:x2}{1:x2}{2:x2}",
                    (int)(R * 255), (int)(G * 255), (int)(B * 255));
            }
        }
    }

    // Quantized vertex key: avoids allocating millions of strings
    internal struct DaeVKey : IEquatable<DaeVKey>
    {
        public long X, Y, Z;
        public DaeVKey(double x, double y, double z)
        {
            X = (long)Math.Round(x * 1e6);
            Y = (long)Math.Round(y * 1e6);
            Z = (long)Math.Round(z * 1e6);
        }
        public bool Equals(DaeVKey o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is DaeVKey && Equals((DaeVKey)obj); }
        public override int GetHashCode()
        {
            unchecked { return (int)(X * 73856093) ^ (int)(Y * 19349663) ^ (int)(Z * 83492791); }
        }
    }

    internal sealed class DaePart
    {
        public string Name;
        public readonly List<DaeVec> Points = new List<DaeVec>();
        public readonly List<DaeVec> Normals = new List<DaeVec>();
        public readonly List<int> Tris = new List<int>();
        public readonly List<int> TriMat = new List<int>();
        public readonly List<float> UV = new List<float>();   // 2 floats per vertex
        public string TextureFile;
        public int FromFace;
        public int Gray;
        public double[] ComponentColor;
    }

    internal sealed class DaeBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string Ns = "http://www.collada.org/2005/11/COLLADASchema";

        public bool IncludeSheetBodies = true;
        public string IdPrefix = "";
        private readonly List<DaeMaterial> materials = new List<DaeMaterial>();
        private readonly List<DaePart> parts = new List<DaePart>();

        public int TriangleCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < parts.Count; i++) n += parts[i].Tris.Count / 3;
                return n;
            }
        }

        public int PartCount { get { return parts.Count; } }
        public int MaterialCount { get { return materials.Count; } }

        // ---------------- Collection ----------------
        public void AddComponent(string name, IComponent2 comp, Matrix<double> toLinkSpace)
        {
            if (comp == null) return;
            try { if (comp.IsSuppressed()) return; }
            catch (Exception) { }

            double[] color = GetComponentColor(comp);
            string compTexture = null;
            if (color == null)
            {
                // An explicit appearance wins over the material's own texture, so only a
                // part whose colour could not be resolved inherits the texture image.
                compTexture = GetComponentTexture(comp);
                Logger.GetLogger().Warn("SaveDAE: no component colour for " + name +
                    ", falling back to per-face colours");
            }

            object[] solids = null;
            try { solids = comp.GetBodies2((int)swBodyType_e.swSolidBody) as object[]; }
            catch (Exception) { }

            object[] sheets = null;
            if (IncludeSheetBodies)
            {
                // STEP imported parts carry their shell as surface bodies; without them
                // the DAE looks gutted.
                try { sheets = comp.GetBodies2((int)swBodyType_e.swSheetBody) as object[]; }
                catch (Exception) { }
            }

            AddBodies(name, solids, sheets, color, compTexture, toLinkSpace);

            object[] children = null;
            try { children = comp.GetChildren() as object[]; }
            catch (Exception) { }
            if (children == null) return;
            for (int i = 0; i < children.Length; i++)
            {
                IComponent2 child = children[i] as IComponent2;
                if (child == null) continue;
                AddComponent(name + "/" + Safe(child.Name2), child, toLinkSpace);
            }
        }

        // Shared by both entry points: the assembly path passes the bodies of one
        // component, the single-part path passes the bodies of the whole document.
        public void AddBodies(string name, object[] solids, object[] sheets, double[] color,
                              string texture, Matrix<double> toLinkSpace)
        {
            bool hasSolids = solids != null && solids.Length > 0;
            bool hasSheets = sheets != null && sheets.Length > 0;
            if (!hasSolids && !hasSheets) return;

            double[] m = ToArray(toLinkSpace);

            DaePart part = new DaePart();
            part.Name = name;
            part.ComponentColor = color;
            part.TextureFile = texture;

            if (hasSolids)
            {
                for (int b = 0; b < solids.Length; b++)
                {
                    IBody2 body = solids[b] as IBody2;
                    if (body != null) AddBody(part, body, m, color);
                }
            }
            if (hasSheets)
            {
                for (int s = 0; s < sheets.Length; s++)
                {
                    IBody2 body = sheets[s] as IBody2;
                    if (body != null) AddBody(part, body, m, color);
                }
            }

            if (part.Tris.Count == 0) return;

            FixWinding(part);
            parts.Add(part);
            Logger.GetLogger().Info(string.Format(
                "SaveDAE color: {0}  component={1}  fromFace={2}  gray={3}  tris={4}",
                name,
                part.ComponentColor == null ? "N/A" : Hex(part.ComponentColor),
                part.FromFace, part.Gray, part.Tris.Count / 3));
        }

        // Single-part export: there is no IComponent2, so bodies and colours come from the
        // document itself.
        public bool AddDocument(string name, ModelDoc2 doc, Matrix<double> toLinkSpace)
        {
            if (doc == null) return false;

            object[] mats = SafeDocMaterials(doc);
            double[] color = ColorFromMaterials(mats);
            if (color == null)
            {
                try
                {
                    double[] c = ToColor(doc.MaterialPropertyValues);
                    if (c != null && !IsUnset(c)) color = c;
                }
                catch (Exception) { }
            }
            string texture = (color == null) ? FirstTexture(mats) : null;

            object[] solids = null;
            object[] sheets = null;
            IPartDoc partDoc = doc as IPartDoc;
            if (partDoc != null)
            {
                try { solids = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
                catch (Exception) { }
                if (IncludeSheetBodies)
                {
                    try { sheets = partDoc.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; }
                    catch (Exception) { }
                }
            }

            int before = parts.Count;
            AddBodies(name, solids, sheets, color, texture, toLinkSpace);
            return parts.Count > before;
        }
        private void AddBody(DaePart part, IBody2 body, double[] m, double[] color)
        {
            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch (Exception) { }
            if (faces == null) return;

            for (int f = 0; f < faces.Length; f++)
            {
                IFace2 face = faces[f] as IFace2;
                if (face == null) continue;
                AddFace(part, face, m, color);
            }
        }

        private void AddFace(DaePart part, IFace2 face, double[] m, double[] color)
        {
            float[] tri = null;
            try { tri = face.GetTessTriangles(true) as float[]; }
            catch (Exception) { }
            if (tri == null || tri.Length < 9) return;

            // Component color first (it is what SolidWorks shows), face color only as fallback.
            double[] col = color;
            if (col == null)
            {
                col = FaceColor(face, null);
                if (col != null) part.FromFace++;
            }
            if (col == null) { col = new double[] { 0.75, 0.75, 0.75, 1.0 }; part.Gray++; }
            int mat = MaterialIndex(col, part.TextureFile);
            int n = tri.Length / 9;
            float[] uvArr = null;
            try { uvArr = face.GetTessTextures() as float[]; } catch (Exception) { }
            if (uvArr != null && uvArr.Length != n * 6) uvArr = null;   // expect 2 floats per vertex

            Dictionary<DaeVKey, int> map = new Dictionary<DaeVKey, int>(n * 2);
            List<DaeVec> pts = new List<DaeVec>(n * 2);
            List<DaeVec> acc = new List<DaeVec>(n * 2);
            List<float> uvs = new List<float>(n * 2);
            int baseIdx = part.Points.Count;

            for (int t = 0; t < n; t++)
            {
                int i = t * 9;
                DaeVec a = Apply(m, tri[i], tri[i + 1], tri[i + 2]);
                DaeVec b = Apply(m, tri[i + 3], tri[i + 4], tri[i + 5]);
                DaeVec c = Apply(m, tri[i + 6], tri[i + 7], tri[i + 8]);

                DaeVec nrm = Cross(Sub(b, a), Sub(c, a));
                if (nrm.X * nrm.X + nrm.Y * nrm.Y + nrm.Z * nrm.Z < 1e-20) continue;

                float ua = 0, va = 0, ub = 0, vb = 0, uc = 0, vc = 0;
                if (uvArr != null)
                {
                    ua = uvArr[t * 6 + 0]; va = uvArr[t * 6 + 1];
                    ub = uvArr[t * 6 + 2]; vb = uvArr[t * 6 + 3];
                    uc = uvArr[t * 6 + 4]; vc = uvArr[t * 6 + 5];
                }
                else
                {
                    // SolidWorks does not expose UVs for these faces: generate a planar
                    // (triplanar) projection so tiling textures such as carbon fibre work.
                    Triplanar(nrm, a, out ua, out va);
                    Triplanar(nrm, b, out ub, out vb);
                    Triplanar(nrm, c, out uc, out vc);
                }
                int ia = GetOrAdd(map, pts, acc, uvs, a, ua, va);
                int ib = GetOrAdd(map, pts, acc, uvs, b, ub, vb);
                int ic = GetOrAdd(map, pts, acc, uvs, c, uc, vc);
                acc[ia] = Add(acc[ia], nrm);
                acc[ib] = Add(acc[ib], nrm);
                acc[ic] = Add(acc[ic], nrm);

                part.Tris.Add(baseIdx + ia);
                part.Tris.Add(baseIdx + ib);
                part.Tris.Add(baseIdx + ic);
                part.TriMat.Add(mat);
            }

            for (int k = 0; k < pts.Count; k++)
            {
                part.Points.Add(pts[k]);
                part.Normals.Add(Normalized(acc[k]));
                part.UV.Add(uvs[k * 2]);
                part.UV.Add(uvs[k * 2 + 1]);
            }
        }

        // Project a point on the plane whose normal dominates; 1 tile = 5 cm
        private static void Triplanar(DaeVec nrm, DaeVec p, out float u, out float v)
        {
            const double scale = 0.05;
            double ax = Math.Abs(nrm.X), ay = Math.Abs(nrm.Y), az = Math.Abs(nrm.Z);
            if (ax >= ay && ax >= az) { u = (float)(p.Y / scale); v = (float)(p.Z / scale); }
            else if (ay >= az) { u = (float)(p.X / scale); v = (float)(p.Z / scale); }
            else { u = (float)(p.X / scale); v = (float)(p.Y / scale); }
        }

        private static int GetOrAdd(Dictionary<DaeVKey, int> map, List<DaeVec> pts, List<DaeVec> acc,
            List<float> uvs, DaeVec p, float u, float v)
        {
            // quantize at 1e-6 m = 1 micron
            DaeVKey key = new DaeVKey(p.X, p.Y, p.Z);
            int idx;
            if (map.TryGetValue(key, out idx)) return idx;
            idx = pts.Count;
            map[key] = idx;
            pts.Add(p);
            acc.Add(new DaeVec(0, 0, 0));
            uvs.Add(u); uvs.Add(v);
            return idx;
        }

        // Closed solid with inward winding: flip so RViz back-face culling does not hide it
        private static void FixWinding(DaePart part)
        {
            double v = 0;
            for (int t = 0; t < part.Tris.Count; t += 3)
            {
                DaeVec a = part.Points[part.Tris[t]];
                DaeVec b = part.Points[part.Tris[t + 1]];
                DaeVec c = part.Points[part.Tris[t + 2]];
                v += Dot(a, Cross(b, c)) / 6.0;
            }
            if (v >= 0) return;

            for (int t = 0; t < part.Tris.Count; t += 3)
            {
                int tmp = part.Tris[t + 1];
                part.Tris[t + 1] = part.Tris[t + 2];
                part.Tris[t + 2] = tmp;
            }
            for (int i = 0; i < part.Normals.Count; i++)
                part.Normals[i] = new DaeVec(-part.Normals[i].X, -part.Normals[i].Y, -part.Normals[i].Z);
        }

        // ---------------- Colors ----------------

        // Per-face color: the display color is closest to what the user sees (covers RealView materials)
        // Material-driven appearances (carbon fiber etc.) report pure black because the
        // real colour comes from their texture. Pure black renders as a silhouette in
        // RViz, so lift it to a dark charcoal that still reads as "black".
        // Spaces and non-ASCII characters break URI resolution in browser-based viewers
        private static string SanitizeFileName(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                          (ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '_';
                sb.Append(ok ? ch : '_');
            }
            return sb.ToString();
        }

        private static string Hex(double[] c)
        {
            return string.Format("#{0:x2}{1:x2}{2:x2}",
                (int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255));
        }

        // SolidWorks returns these for "no color set"; they must not stop the lookup chain
        private static bool IsUnset(double[] c)
        {
            if (c == null) return true;
            bool white = c[0] > 0.98 && c[1] > 0.98 && c[2] > 0.98;
            bool defGray = Math.Abs(c[0] - 0.7529) < 0.02 &&
                           Math.Abs(c[1] - 0.7529) < 0.02 &&
                           Math.Abs(c[2] - 0.7529) < 0.02;
            return white || defGray;
        }

        private static bool IsNearBlack(double[] c)
        {
            return c[0] < 0.02 && c[1] < 0.02 && c[2] < 0.02;
        }

        private static bool IsDefaultGray(double[] c)
        {
            return Math.Abs(c[0] - 0.7529) < 0.02 &&
                   Math.Abs(c[1] - 0.7529) < 0.02 &&
                   Math.Abs(c[2] - 0.7529) < 0.02;
        }

        private static double[] FaceColor(IFace2 face, double[] fallback)
        {
            try
            {
                if (face.HasMaterialPropertyValues())
                {
                    double[] c = ToColor(face.MaterialPropertyValues);
                    if (c != null && !IsUnset(c)) return c;
                }
            }
            catch (Exception) { }

            try
            {
                object o = face.GetMaterialPropertyValues2(
                    (int)swInConfigurationOpts_e.swThisConfiguration, null);
                double[] c = ToColor(o);
                if (c != null && !IsUnset(c)) return c;
            }
            catch (Exception) { }

            return fallback;
        }

        private static double[] GetComponentColor(IComponent2 comp)
        {
            // 1) Component-level appearance (applied to this component in the assembly)
            double[] c = ColorFromMaterials(SafeCompMaterials(comp));
            if (c != null) return c;

            // 2) Legacy "component color"
            try
            {
                if (comp.HasMaterialPropertyValues())
                {
                    c = ToColor(comp.MaterialPropertyValues);
                    if (c != null && !IsUnset(c)) return c;
                }
            }
            catch (Exception) { }

            // 3) Part document appearance (most common: appearance lives on the part, not the component)
            ModelDoc2 mdoc = null;
            try { mdoc = comp.GetModelDoc2() as ModelDoc2; }
            catch (Exception) { }
            if (mdoc != null)
            {
                c = ColorFromMaterials(SafeDocMaterials(mdoc));
                if (c != null) return c;

                try
                {
                    c = ToColor(mdoc.MaterialPropertyValues);
                    if (c != null && !IsUnset(c)) return c;
                }
                catch (Exception) { }
            }

            return null;   // let the caller apply a fallback and log it
        }

        // First render material of this component that actually carries a texture
        private static string GetComponentTexture(IComponent2 comp)
        {
            try { return FirstTexture(SafeCompMaterials(comp)); }
            catch (Exception) { }
            return null;
        }

        private static string FirstTexture(object[] mats)
        {
            if (mats == null) return null;
            for (int i = 0; i < mats.Length; i++)
            {
                IRenderMaterial rm = mats[i] as IRenderMaterial;
                if (rm == null) continue;
                string fn = null;
                try { fn = rm.TextureFilename; } catch (Exception) { }
                if (!string.IsNullOrEmpty(fn)) return fn;
            }
            return null;
        }

        private static object[] SafeCompMaterials(IComponent2 comp)
        {
            try
            {
                return comp.GetRenderMaterials2(
                    (int)swDisplayStateOpts_e.swThisDisplayState, null) as object[];
            }
            catch (Exception) { return null; }
        }

        private static object[] SafeDocMaterials(ModelDoc2 doc)
        {
            try
            {
                return doc.Extension.GetRenderMaterials2(
                    (int)swDisplayStateOpts_e.swThisDisplayState, null) as object[];
            }
            catch (Exception) { return null; }
        }

        // Pick one material from the array: prefer the one covering the most entities
        private static double[] ColorFromMaterials(object[] mats)
        {
            if (mats == null || mats.Length == 0) return null;

            // Pass 1: plain-color material (no texture) with a meaningful color.
            // Pass 2: any material with a meaningful color.
            // White / default gray means "no appearance set" (e.g. carbon-fiber style
            // materials report #ffffff because the color comes from their texture).
            for (int pass = 0; pass < 2; pass++)
            {
                IRenderMaterial best = null;
                double[] bestColor = null;
                int bestCount = -1;

                for (int i = 0; i < mats.Length; i++)
                {
                    IRenderMaterial rm = mats[i] as IRenderMaterial;
                    if (rm == null) continue;

                    bool textured = false;
                    try { textured = !string.IsNullOrEmpty(rm.TextureFilename); }
                    catch (Exception) { }
                    if (pass == 0 && textured) continue;

                    double[] c = ReadPrimaryColor(rm);
                    if (c == null) continue;
                    if (pass == 0)
                    {
                        // Plain (non-textured) appearance: white IS a real colour here
                        // (e.g. a white ABS part with an explicitly set white appearance).
                        // Only zero-black and the document default grey are sentinels.
                        if (IsNearBlack(c) || IsDefaultGray(c)) continue;
                    }
                    else
                    {
                        // Textured appearance: its PrimaryColor is meaningless when it is
                        // white/grey/black, because the real colour lives in the texture.
                        if (IsUnset(c)) continue;
                    }

                    int n = 0;
                    try { n = rm.GetEntitiesCount(); }
                    catch (Exception) { }
                    if (n > bestCount) { bestCount = n; best = rm; bestColor = c; }
                }

                if (best != null)
                {
                    return bestColor;
                }
            }
            return null;
        }

        private static double[] ReadPrimaryColor(IRenderMaterial rm)
        {
            try
            {
                int col = rm.PrimaryColor;   // COLORREF 0x00BBGGRR
                return new double[]
                {
                    (col & 0xFF) / 255.0,
                    ((col >> 8) & 0xFF) / 255.0,
                    ((col >> 16) & 0xFF) / 255.0,
                    1.0
                };
            }
            catch (Exception) { return null; }
        }        private static double[] ToColor(object o)
        {
            if (o == null) return null;
            double r, g, b, t = 0;
            double[] d = o as double[];
            float[] f = o as float[];
            if (d != null && d.Length >= 3) { r = d[0]; g = d[1]; b = d[2]; if (d.Length > 7) t = d[7]; }
            else if (f != null && f.Length >= 3) { r = f[0]; g = f[1]; b = f[2]; if (f.Length > 7) t = f[7]; }
            else return null;

            double a = 1.0 - t;
            if (a < 0.02) a = 0.02;
            return new double[] { Clamp(r), Clamp(g), Clamp(b), a };
        }

        private static double Clamp(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        private int MaterialIndex(double[] c, string tex)
        {
            for (int i = 0; i < materials.Count; i++)
                if (materials[i].Matches(c, tex)) return i;
            DaeMaterial mm = new DaeMaterial();
            mm.Index = materials.Count;
            mm.R = (float)c[0]; mm.G = (float)c[1]; mm.B = (float)c[2]; mm.A = (float)c[3];
            mm.TextureFile = tex;
            mm.Prefix = IdPrefix;
            if (!string.IsNullOrEmpty(tex))
                mm.TextureRel = "../textures/" + SanitizeFileName(Path.GetFileName(tex));
            materials.Add(mm);
            return mm.Index;
        }

        // ---------------- Math ----------------

        private static double[] ToArray(Matrix<double> m)
        {
            if (m == null)
                return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            return new double[]
            {
                m[0,0], m[1,0], m[2,0],
                m[0,1], m[1,1], m[2,1],
                m[0,2], m[1,2], m[2,2],
                m[0,3], m[1,3], m[2,3]
            };
        }

        private static DaeVec Apply(double[] m, double x, double y, double z)
        {
            return new DaeVec(
                m[0] * x + m[3] * y + m[6] * z + m[9],
                m[1] * x + m[4] * y + m[7] * z + m[10],
                m[2] * x + m[5] * y + m[8] * z + m[11]);
        }

        private static DaeVec Add(DaeVec a, DaeVec b) { return new DaeVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        private static DaeVec Sub(DaeVec a, DaeVec b) { return new DaeVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        private static double Dot(DaeVec a, DaeVec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static DaeVec Cross(DaeVec a, DaeVec b)
        {
            return new DaeVec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        private static DaeVec Normalized(DaeVec a)
        {
            double l = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
            if (l < 1e-12) return new DaeVec(0, 0, 1);
            return new DaeVec(a.X / l, a.Y / l, a.Z / l);
        }

        // ---------------- File writer ----------------

        public void Write(string path)
        {
            XmlWriterSettings st = new XmlWriterSettings();
            st.Indent = true;
            st.IndentChars = "  ";
            st.Encoding = new UTF8Encoding(false);
            st.CloseOutput = true;

            using (XmlWriter w = XmlWriter.Create(path, st))
            {
                w.WriteStartDocument();
                w.WriteStartElement("COLLADA", Ns);
                w.WriteAttributeString("version", "1.4.1");

                WriteAsset(w);
                WriteImages(w, path);
                WriteEffects(w);
                WriteMaterials(w);
                WriteGeometries(w);
                WriteScene(w);

                w.WriteEndElement();
                w.WriteEndDocument();
            }
        }

        private static void WriteAsset(XmlWriter w)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);
            w.WriteStartElement("asset");
            w.WriteStartElement("contributor");
            w.WriteElementString("authoring_tool", "Sw2Visual (SolidWorks URDF Exporter fork)");
            w.WriteEndElement();
            w.WriteElementString("created", now);
            w.WriteElementString("modified", now);
            w.WriteStartElement("unit");
            w.WriteAttributeString("meter", "1");
            w.WriteAttributeString("name", "meter");
            w.WriteEndElement();
            w.WriteElementString("up_axis", "Z_UP");
            w.WriteEndElement();
        }

        private void WriteImages(XmlWriter w, string daePath)
        {
            string texDir = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(daePath)), "..", "textures");
            try { if (!Directory.Exists(texDir)) Directory.CreateDirectory(texDir); }
            catch (Exception) { }

            w.WriteStartElement("library_images");
            for (int i = 0; i < materials.Count; i++)
            {
                DaeMaterial m = materials[i];
                if (string.IsNullOrEmpty(m.TextureRel)) continue;
                try
                {
                    string dst = Path.Combine(texDir, Path.GetFileName(m.TextureRel));
                    if (File.Exists(m.TextureFile)) File.Copy(m.TextureFile, dst, true);
                }
                catch (Exception) { }
                w.WriteStartElement("image");
                w.WriteAttributeString("id", m.Prefix + "img" + m.Index.ToString(Inv));
                w.WriteElementString("init_from", m.TextureRel);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private void WriteEffects(XmlWriter w)
        {
            w.WriteStartElement("library_effects");
            for (int i = 0; i < materials.Count; i++)
            {
                DaeMaterial m = materials[i];
                bool textured = !string.IsNullOrEmpty(m.TextureRel);

                w.WriteStartElement("effect");
                w.WriteAttributeString("id", m.FxId);
                w.WriteStartElement("profile_COMMON");

                if (textured)
                {
                    w.WriteStartElement("newparam");
                    w.WriteAttributeString("sid", m.Prefix + "surf" + m.Index);
                    w.WriteStartElement("surface");
                    w.WriteAttributeString("type", "2D");
                    w.WriteElementString("init_from", m.Prefix + "img" + m.Index);
                    w.WriteEndElement();
                    w.WriteEndElement();

                    w.WriteStartElement("newparam");
                    w.WriteAttributeString("sid", m.Prefix + "samp" + m.Index);
                    w.WriteStartElement("sampler2D");
                    w.WriteElementString("source", m.Prefix + "surf" + m.Index);
                    w.WriteEndElement();
                    w.WriteEndElement();
                }

                w.WriteStartElement("technique");
                w.WriteAttributeString("sid", "common");
                w.WriteStartElement("phong");
                w.WriteStartElement("emission");
                w.WriteStartElement("color");
                w.WriteString("0 0 0 1");
                w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("ambient");
                w.WriteStartElement("color");
                w.WriteString(Fmt4(m.R * 0.2f, m.G * 0.2f, m.B * 0.2f, 1f));
                w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("diffuse");
                if (textured)
                {
                    w.WriteStartElement("texture");
                    w.WriteAttributeString("texture", m.Prefix + "samp" + m.Index);
                    w.WriteEndElement();
                }
                else
                {
                    w.WriteStartElement("color");
                    w.WriteString(Fmt4(m.R, m.G, m.B, m.A));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();   // phong
                w.WriteEndElement();   // technique
                w.WriteEndElement();   // profile_COMMON
                w.WriteEndElement();   // effect
            }
            w.WriteEndElement();
        }

        private void WriteMaterials(XmlWriter w)
        {
            w.WriteStartElement("library_materials");
            for (int i = 0; i < materials.Count; i++)
            {
                DaeMaterial m = materials[i];
                w.WriteStartElement("material");
                w.WriteAttributeString("id", m.Id);
                w.WriteAttributeString("name", m.Name);
                w.WriteStartElement("instance_effect");
                w.WriteAttributeString("url", "#" + m.FxId);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private void WriteGeometries(XmlWriter w)
        {
            w.WriteStartElement("library_geometries");
            for (int gi = 0; gi < parts.Count; gi++)
            {
                DaePart pm = parts[gi];
                string gid = "geom" + gi.ToString(Inv);
                string pid = gid + "-pos";
                string nid = gid + "-nrm";
                string vid = gid + "-vtx";

                w.WriteStartElement("geometry");
                w.WriteAttributeString("id", gid);
                w.WriteAttributeString("name", pm.Name);
                w.WriteStartElement("mesh");

                w.WriteStartElement("source");
                w.WriteAttributeString("id", pid);
                WriteFloatArray(w, pid + "-array", pm.Points);
                w.WriteStartElement("technique_common");
                WriteAccessor(w, pid + "-array", pm.Points.Count);
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("source");
                w.WriteAttributeString("id", nid);
                WriteFloatArray(w, nid + "-array", pm.Normals);
                w.WriteStartElement("technique_common");
                WriteAccessor(w, nid + "-array", pm.Normals.Count);
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("source");
                w.WriteAttributeString("id", gid + "-uv");
                w.WriteStartElement("float_array");
                w.WriteAttributeString("id", gid + "-uv-array");
                w.WriteAttributeString("count", pm.UV.Count.ToString(Inv));
                {
                    StringBuilder usb = new StringBuilder(pm.UV.Count * 8);
                    for (int ui = 0; ui < pm.UV.Count; ui++) usb.Append(Fmt(pm.UV[ui])).Append(' ');
                    w.WriteString(usb.ToString());
                }
                w.WriteEndElement();
                w.WriteStartElement("technique_common");
                w.WriteStartElement("accessor");
                w.WriteAttributeString("source", "#" + gid + "-uv-array");
                w.WriteAttributeString("count", (pm.UV.Count / 2).ToString(Inv));
                w.WriteAttributeString("stride", "2");
                w.WriteStartElement("param");
                w.WriteAttributeString("name", "S");
                w.WriteAttributeString("type", "float");
                w.WriteEndElement();
                w.WriteStartElement("param");
                w.WriteAttributeString("name", "T");
                w.WriteAttributeString("type", "float");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("vertices");
                w.WriteAttributeString("id", vid);
                w.WriteStartElement("input");
                w.WriteAttributeString("semantic", "POSITION");
                w.WriteAttributeString("source", "#" + pid);
                w.WriteEndElement();
                w.WriteEndElement();

                int triCount = pm.Tris.Count / 3;
                for (int mi = 0; mi < materials.Count; mi++)
                {
                    List<int> group = new List<int>();
                    for (int t = 0; t < triCount; t++)
                        if (pm.TriMat[t] == mi) group.Add(t);
                    if (group.Count == 0) continue;

                    w.WriteStartElement("triangles");
                    w.WriteAttributeString("material", materials[mi].Id);
                    w.WriteAttributeString("count", group.Count.ToString(Inv));
                    WriteInput(w, "VERTEX", "#" + vid, "0");
                    WriteInput(w, "NORMAL", "#" + nid, "1");
                    w.WriteStartElement("input");
                    w.WriteAttributeString("semantic", "TEXCOORD");
                    w.WriteAttributeString("source", "#" + gid + "-uv");
                    w.WriteAttributeString("offset", "2");
                    w.WriteAttributeString("set", "0");
                    w.WriteEndElement();

                    StringBuilder sb = new StringBuilder(group.Count * 12);
                    for (int k = 0; k < group.Count; k++)
                    {
                        int t = group[k] * 3;
                        for (int c = 0; c < 3; c++)
                        {
                            int idx = pm.Tris[t + c];
                            sb.Append(idx.ToString(Inv)).Append(' ').Append(idx.ToString(Inv)).Append(' ')
                              .Append(idx.ToString(Inv)).Append(' ');
                        }
                    }
                    w.WriteStartElement("p");
                    w.WriteString(sb.ToString());
                    w.WriteEndElement();
                    w.WriteEndElement();
                }

                w.WriteEndElement(); // mesh
                w.WriteEndElement(); // geometry
            }
            w.WriteEndElement();
        }

        private void WriteScene(XmlWriter w)
        {
            w.WriteStartElement("library_visual_scenes");
            w.WriteStartElement("visual_scene");
            w.WriteAttributeString("id", "Scene");
            w.WriteAttributeString("name", "Scene");

            for (int gi = 0; gi < parts.Count; gi++)
            {
                DaePart pm = parts[gi];
                w.WriteStartElement("node");
                w.WriteAttributeString("id", "node" + gi.ToString(Inv));
                w.WriteAttributeString("name", pm.Name);
                w.WriteAttributeString("type", "NODE");

                w.WriteStartElement("instance_geometry");
                w.WriteAttributeString("url", "#geom" + gi.ToString(Inv));
                w.WriteStartElement("bind_material");
                w.WriteStartElement("technique_common");

                bool[] used = new bool[materials.Count];
                for (int t = 0; t < pm.TriMat.Count; t++) used[pm.TriMat[t]] = true;
                for (int mi = 0; mi < materials.Count; mi++)
                {
                    if (!used[mi]) continue;
                    w.WriteStartElement("instance_material");
                    w.WriteAttributeString("symbol", materials[mi].Id);
                    w.WriteAttributeString("target", "#" + materials[mi].Id);
                    w.WriteEndElement();
                }

                w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("scene");
            w.WriteStartElement("instance_visual_scene");
            w.WriteAttributeString("url", "#Scene");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteInput(XmlWriter w, string semantic, string source, string offset)
        {
            w.WriteStartElement("input");
            w.WriteAttributeString("semantic", semantic);
            w.WriteAttributeString("source", source);
            w.WriteAttributeString("offset", offset);
            w.WriteEndElement();
        }

        private static void WriteFloatArray(XmlWriter w, string id, List<DaeVec> pts)
        {
            StringBuilder sb = new StringBuilder(pts.Count * 32);
            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append(Fmt(pts[i].X)).Append(' ')
                  .Append(Fmt(pts[i].Y)).Append(' ')
                  .Append(Fmt(pts[i].Z)).Append(' ');
            }
            w.WriteStartElement("float_array");
            w.WriteAttributeString("id", id);
            w.WriteAttributeString("count", (pts.Count * 3).ToString(Inv));
            w.WriteString(sb.ToString());
            w.WriteEndElement();
        }

        private static void WriteAccessor(XmlWriter w, string source, int count)
        {
            w.WriteStartElement("accessor");
            w.WriteAttributeString("source", "#" + source);
            w.WriteAttributeString("count", count.ToString(Inv));
            w.WriteAttributeString("stride", "3");
            string[] names = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                w.WriteStartElement("param");
                w.WriteAttributeString("name", names[i]);
                w.WriteAttributeString("type", "float");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private static string Fmt(double v)
        {
            if (Math.Abs(v) < 1e-12) return "0";
            return v.ToString("0.#####", Inv);
        }

        private static string Fmt4(float r, float g, float b, float a)
        {
            return Fmt(r) + " " + Fmt(g) + " " + Fmt(b) + " " + Fmt(a);
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "part";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = true;
                for (int k = 0; k < bad.Length; k++) if (bad[k] == c) { ok = false; break; }
                if (c == '/' || c == '\\' || c == ' ') ok = false;
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }
    }

    public partial class ExportHelper
    {
        // Export all components of one link into a single .dae (multi-material)
        // Single-part export: the mesh comes from the whole document rather than from a
        // component, so it needs its own entry point.
        private bool SaveDAEDocument(Link link, string windowsFileName, Matrix<double> toLinkSpace)
        {
            try
            {
                DaeBuilder builder = new DaeBuilder();
                builder.IdPrefix = DaeWriterSafeName(link.Name) + "_";
                if (!builder.AddDocument(DaeWriterSafeName(link.Name), ActiveSWModel, toLinkSpace))
                {
                    logger.Warn("SaveDAE: no geometry collected for " + link.Name);
                    return false;
                }
                builder.Write(windowsFileName);
                logger.Info(string.Format("SaveDAE: {0} -> {1} parts, {2} materials, {3} triangles",
                    link.Name, builder.PartCount, builder.MaterialCount, builder.TriangleCount));
                return true;
            }
            catch (Exception e)
            {
                logger.Warn("SaveDAE: failed for " + link.Name + ": " + e.Message);
                return false;
            }
        }
        private bool SaveDAE(Link link, string windowsFileName)
        {
            try
            {
                MathTransform csTransform = GetCoordinateSystemTransform(link.Joint.CoordinateSystemName);
                if (csTransform == null)
                {
                    logger.Warn("SaveDAE: coordinate system not found for link " + link.Name + ": " +
                        link.Joint.CoordinateSystemName);
                    return false;
                }

                Matrix<double> csInverse = MathOps.GetTransformation(csTransform).Inverse();

                DaeBuilder builder = new DaeBuilder();
                builder.IdPrefix = DaeWriterSafeName(link.Name) + "_";
                foreach (Component2 comp in link.SWComponents)
                {
                    MathTransform total = comp.GetTotalTransform(true);
                    Matrix<double> world = MathOps.GetTransformation(total);
                    Matrix<double> local = csInverse * world;
                    builder.AddComponent(DaeWriterSafeName(comp.Name2), comp, local);
                }

                if (builder.PartCount == 0)
                {
                    logger.Warn("SaveDAE: no geometry collected for link " + link.Name);
                    return false;
                }

                builder.Write(windowsFileName);
                logger.Info(string.Format("SaveDAE: {0} -> {1} parts, {2} materials, {3} triangles",
                    link.Name, builder.PartCount, builder.MaterialCount, builder.TriangleCount));
                return true;
            }
            catch (Exception e)
            {
                logger.Error("SaveDAE failed: " + link.Name, e);
                return false;
            }
        }

        private static string DaeWriterSafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "part";
            return s.Replace('/', '_').Replace('\\', '_');
        }
    }
}