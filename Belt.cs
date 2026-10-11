using System.Collections.Generic;
using UnityEngine;

namespace EngineSim
{
    // A drive belt drawn round two pulleys. Add it to an object that doesn't move with either pulley's spin
    // (it builds its mesh there) and give it:
    //   a, b                 the pulleys' centres (spinning visuals are fine: their origin stays on the axle)
    //   radiusA, radiusB     where the belt sits on each, m
    //   width, thickness     the belt's, m
    //   coupling             the WheelCoupling the belt is (its engagement and its driving wheel's speed)
    //   sag                  how far the lower run hangs when the belt is fully slack, m
    //   material             optional; without one it is black rubber with moulded cross ribs (a cogged belt)
    //                        every `pitch` m, so its running and slipping can be seen
    //   replaces             optional renderers it stands in for (hidden)
    // The belt lies square to this object's X axis (the axles' direction), halfway between the two pulleys'
    // centres along it (a belt has to run true on both).
    // It runs straight along the two outer tangents and wraps each pulley between them. The tensioner (the
    // coupling's engagement) takes up the slack: released, the lower run hangs by `sag` and the belt stands
    // still; as it is tightened the run straightens and the belt is dragged round by a's rim (its ribs run
    // round with it). The mesh is built again only when a pulley moves, a radius changes or the tension
    // changes by a step.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class Belt : MonoBehaviour
    {
        public Transform a, b;
        public float radiusA = 0.5f, radiusB = 0.1f;
        public float width = 0.05f, thickness = 0.008f;
        public WheelCoupling coupling;
        public float sag = 0.06f;
        public Material material;
        public float pitch = 0.02f;
        public Renderer[] replaces;

        private static Material _rubber;

        private const int ArcSegments = 64;     // per full turn
        private const int SpanSegments = 16;    // per straight run

        private Mesh _mesh;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Vector3 _lastA, _lastB;
        private float _lastRa = -1f, _lastRb = -1f, _length, _scroll;
        private float _lastSlack = -1f;

        // 0 tight .. 1 fully slack, in twentieths
        private float Slack { get { return coupling == null ? 0f : Mathf.Round((1f - Mathf.Clamp01(coupling.engagement)) * 20f) / 20f; } }

        private void Start()
        {
            _mesh = new Mesh { name = "belt" };
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
            if (replaces != null)
                foreach (var r in replaces)
                    if (r != null) r.enabled = false;
            _renderer.sharedMaterial = material != null ? material : Rubber();
        }

        // black rubber, a rib across the belt every tile (one tile is `pitch` long along the belt)
        private static Material Rubber()
        {
            if (_rubber != null) return _rubber;
            const int w = 32, h = 4;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "belt rubber", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var px = new Color[w * h];
            for (int x = 0; x < w; x++)
            {
                // a rounded rib over the middle third of the pitch, the land between it flat and darker
                float t = (x + 0.5f) / w;
                float rib = Mathf.Clamp01(1f - Mathf.Abs(t - 0.5f) / 0.18f);
                float v = Mathf.Lerp(0.035f, 0.11f, rib * rib * (3f - 2f * rib));
                for (int y = 0; y < h; y++) px[y * w + x] = new Color(v, v, v * 1.05f, 1f);
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            var shader = Shader.Find("Standard");
            _rubber = new Material(shader != null ? shader : Shader.Find("Diffuse")) { name = "belt rubber", mainTexture = tex };
            if (_rubber.HasProperty("_Glossiness")) _rubber.SetFloat("_Glossiness", 0.25f);
            if (_rubber.HasProperty("_Metallic")) _rubber.SetFloat("_Metallic", 0f);
            return _rubber;
        }

        private void LateUpdate()
        {
            if (a == null || b == null) return;
            var pa = transform.InverseTransformPoint(a.position);
            var pb = transform.InverseTransformPoint(b.position);
            float slack = Slack;
            if ((pa - _lastA).sqrMagnitude > 1e-6f || (pb - _lastB).sqrMagnitude > 1e-6f || radiusA != _lastRa || radiusB != _lastRb || slack != _lastSlack)
            {
                _lastA = pa; _lastB = pb; _lastRa = radiusA; _lastRb = radiusB; _lastSlack = slack;
                Build(pa, pb, slack * sag);
            }
            // the driving rim drags the belt round as soon as the tensioner bears on it
            float speed = 0f;
            if (slack < 1f && coupling != null && coupling.a != null && coupling.a.wheel != null)
                speed = coupling.a.wheel.angularVelocity * radiusA;
            float tile = material != null ? width : Mathf.Max(pitch, 1e-3f);
            if (_length > 0f && tile > 0f)
            {
                // whole tiles round the loop, so the pattern meets itself at the seam
                float tiles = Mathf.Max(1f, Mathf.Round(_length / tile));
                _scroll = (_scroll + speed * Time.deltaTime * tiles / _length) % 1f;
                _renderer.GetPropertyBlock(_block);
                _block.SetVector("_MainTex_ST", new Vector4(tiles, 1f, -_scroll, 0f));
                _renderer.SetPropertyBlock(_block);
            }
        }

        // in this object's space: x is the axle, the belt lies in the y-z plane at a's x
        private void Build(Vector3 pa, Vector3 pb, float hang)
        {
            var c1 = new Vector2(pa.z, pa.y);
            var c2 = new Vector2(pb.z, pb.y);
            float r1 = radiusA, r2 = radiusB, x = 0.5f * (pa.x + pb.x);
            float d = Vector2.Distance(c1, c2);
            _mesh.Clear();
            _length = 0f;
            if (d <= Mathf.Abs(r1 - r2) + 1e-4f) return;   // one inside the other: no belt
            var e = (c2 - c1) / d;
            var p = new Vector2(-e.y, e.x);
            float k = (r1 - r2) / d, s = Mathf.Sqrt(1f - k * k);
            // the outer tangents touch both pulleys where their radius points along nUp / nDown
            var nUp = k * e + s * p;
            var nDown = k * e - s * p;
            // the run that faces down hangs when slack
            bool upFacesDown = nUp.y < nDown.y;

            var path = new List<Vector2>();
            var normals = new List<Vector2>();
            void Span(Vector2 from, Vector2 to, Vector2 n, bool hangs)
            {
                for (int i = 0; i < SpanSegments; i++)
                {
                    float t = (float)i / SpanSegments;
                    var q = Vector2.Lerp(from, to, t);
                    if (hangs) q.y -= hang * 4f * t * (1f - t);
                    path.Add(q);
                    normals.Add(n);
                }
            }
            void Arc(Vector2 c, float r, Vector2 from, Vector2 to, Vector2 through)
            {
                float a0 = Mathf.Atan2(from.y, from.x), a1 = Mathf.Atan2(to.y, to.x), am = Mathf.Atan2(through.y, through.x);
                // go the way that passes `through` (the far side of the pulley)
                float sweep = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                float toMid = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, am * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                if (Mathf.Sign(toMid) != Mathf.Sign(sweep) || Mathf.Abs(toMid) > Mathf.Abs(sweep))
                    sweep = sweep > 0f ? sweep - 2f * Mathf.PI : sweep + 2f * Mathf.PI;
                int n = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(sweep) / (2f * Mathf.PI) * ArcSegments));
                for (int i = 0; i < n; i++)
                {
                    float ang = a0 + sweep * i / n;
                    var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    path.Add(c + dir * r);
                    normals.Add(dir);
                }
            }
            Span(c1 + nUp * r1, c2 + nUp * r2, nUp, upFacesDown);
            Arc(c2, r2, nUp, nDown, e);
            Span(c2 + nDown * r2, c1 + nDown * r1, nDown, !upFacesDown);
            Arc(c1, r1, nDown, nUp, -e);

            // a closed ribbon: inner face on the pulleys, outer face `thickness` out, and two edges
            int count = path.Count;
            var verts = new List<Vector3>(count * 8 + 8);
            var uvs = new List<Vector2>(count * 8 + 8);
            var nrm = new List<Vector3>(count * 8 + 8);
            var tris = new List<int>(count * 24);
            float h = width * 0.5f;
            var along = new float[count + 1];
            for (int i = 1; i <= count; i++) along[i] = along[i - 1] + Vector2.Distance(path[i - 1], path[i % count]);
            _length = along[count];
            // four faces, each a strip of count+1 rows (the seam row repeats with u = 1)
            void Face(System.Func<int, Vector3> left, System.Func<int, Vector3> right, System.Func<int, Vector3> normal)
            {
                int start = verts.Count;
                for (int i = 0; i <= count; i++)
                {
                    int j = i % count;
                    float u = _length > 0f ? along[i] / _length : 0f;
                    verts.Add(left(j)); uvs.Add(new Vector2(u, 0f)); nrm.Add(normal(j));
                    verts.Add(right(j)); uvs.Add(new Vector2(u, 1f)); nrm.Add(normal(j));
                }
                for (int i = 0; i < count; i++)
                {
                    int q = start + i * 2;
                    tris.Add(q); tris.Add(q + 2); tris.Add(q + 1);
                    tris.Add(q + 1); tris.Add(q + 2); tris.Add(q + 3);
                }
            }
            Vector3 P(int i, float out_, float side) { var q = path[i] + normals[i] * out_; return new Vector3(x + side, q.y, q.x); }
            Vector3 N(int i) { return new Vector3(0f, normals[i].y, normals[i].x); }
            Face(i => P(i, thickness, -h), i => P(i, thickness, h), N);                       // outer
            Face(i => P(i, 0f, h), i => P(i, 0f, -h), i => -N(i));                             // inner
            Face(i => P(i, 0f, -h), i => P(i, thickness, -h), i => Vector3.left);              // edge -x
            Face(i => P(i, thickness, h), i => P(i, 0f, h), i => Vector3.right);               // edge +x
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, uvs);
            _mesh.SetNormals(nrm);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
        }
    }
}
