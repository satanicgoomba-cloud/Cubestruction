using System.Collections;
using System.Collections.Generic;
using Cubestruction.Player;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>Scene-placed networked effects: explosions (glossy shock bubbles), tracers, impacts, shockwaves.
    /// Kept deliberately light: pooled-ish primitives + one burst particle system.</summary>
    public class FxHub : NetworkBehaviour
    {
        public static FxHub Instance { get; private set; }
        public Material glowMaterial;        // Cubestruction/UnlitTransparent
        public Material bubbleMaterial;      // Cubestruction/AeroGloss transparent-ish
        public ParticleSystem sparkBurst;
        public AudioClip boomClip, hitClip, zapClip;

        Bullets _bullets;
        float _impactSfxAt;

        void Awake()
        {
            Instance = this;
            _bullets = gameObject.AddComponent<Bullets>();
            _bullets.material = glowMaterial;
        }

        public bool TakeKunai(ulong owner, out Vector3 to) { to = default; return _bullets && _bullets.TakeKunai(owner, out to); }
        public int LiveBullets => _bullets ? _bullets.LiveCount : 0;

        /// <summary>Fires a volley on this peer. <paramref name="authority"/> = this copy damages voxels (server only).</summary>
        public void FireBullets(ToolDef t, Vector3 origin, Vector3 dir, int seed, ulong owner, bool authority, float scale, float size = 0f)
        {
            if (size > 0f) { t = t.Clone(); t.bulletSize = size; }
            _bullets.Fire(t, origin, dir, seed, owner, authority && IsServer, scale);
            Play(zapClip, origin, t.id == "RailGun" ? 0.7f : 0.25f, t.id == "RailGun" ? 0.7f : t.id == "PinballPopper" ? 1.8f : Random.Range(1.3f, 1.6f));
        }

        /// <summary>Server -> other clients: draw a volley someone else fired (the shooter drew theirs already).</summary>
        [Rpc(SendTo.NotServer)]
        public void BulletsRpc(int tool, Vector3 origin, Vector3 dir, int seed, ulong owner, float scale, bool includeOwner = false, float size = 0f)
        {
            if (NetworkManager.LocalClientId == owner && !includeOwner) return;   // the shooter drew its own
            var t = Balance.Tool(tool);
            if (t != null) FireBullets(t, origin, dir, seed, owner, false, scale, size);
        }

        public static int LightningBolts;   // -autotest counter

        /// <summary>Chain Zapper bolt through the voxels it hit (everyone, including the shooter).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void LightningRpc(Vector3[] points) => LightningLocal(points);

        /// <summary>Draw a lightning bolt on this peer only.</summary>
        public void LightningLocal(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;
            LightningBolts++;
            var go = new GameObject("Lightning");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = glowMaterial;
            var pts = new List<Vector3>();
            for (int i = 0; i < points.Length - 1; i++)
            {
                var a = points[i]; var b = points[i + 1];
                for (int k = 0; k < 4; k++) pts.Add(Vector3.Lerp(a, b, k / 4f) + (k == 0 ? Vector3.zero : Random.insideUnitSphere * 0.12f));
                Burst(b, 4, new Color(0.7f, 0.9f, 1f));
            }
            pts.Add(points[points.Length - 1]);
            lr.positionCount = pts.Count; lr.SetPositions(pts.ToArray());
            lr.widthMultiplier = 0.06f;
            lr.startColor = new Color(0.85f, 0.95f, 1f); lr.endColor = new Color(0.55f, 0.75f, 1f);
            StartCoroutine(Fade(lr, 0.18f));
            Play(zapClip, points[1], 0.7f, Random.Range(0.8f, 1.1f));
        }

        /// <summary>Aqua Surge beam segment (refreshed ~10x a second while it fires).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void SurgeBeamRpc(Vector3 origin, Vector3 dir, float radiusM)
        {
            const float len = 30f;
            var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(c.GetComponent<Collider>());
            c.GetComponent<MeshRenderer>().material = new Material(glowMaterial) { color = new Color(0.45f, 0.9f, 1f, 0.8f) };
            c.transform.position = origin + dir * len * 0.5f;
            c.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            c.transform.localScale = new Vector3(radiusM * 2f, len * 0.5f, radiusM * 2f);
            Destroy(c, 0.13f);
            Burst(origin + dir * 2f, 6, new Color(0.6f, 0.95f, 1f));
            if (Time.time >= _impactSfxAt) { _impactSfxAt = Time.time + 0.2f; Play(zapClip, origin, 0.9f, 0.5f); }
        }

        /// <summary>Chain Whip lash: a quick sagging line from the hand to the point.</summary>
        public void Whip(Vector3 from, Vector3 to, bool hit)
        {
            var go = new GameObject("Whip");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = glowMaterial;
            const int n = 12;
            lr.positionCount = n;
            var side = Vector3.Cross((to - from).normalized, Vector3.up);
            for (int i = 0; i < n; i++)
            {
                float k = i / (n - 1f);
                lr.SetPosition(i, Vector3.Lerp(from, to, k) + (side * 0.25f + Vector3.down * 0.35f) * Mathf.Sin(k * Mathf.PI));
            }
            lr.widthMultiplier = 0.035f;
            lr.startColor = new Color(0.85f, 0.7f, 0.4f); lr.endColor = new Color(0.55f, 0.4f, 0.25f);
            StartCoroutine(Fade(lr, 0.15f));
            if (hit) { Burst(to, 10, new Color(1f, 0.85f, 0.5f)); Play(hitClip, to, 0.8f, 0.7f); }
        }

        [Rpc(SendTo.ClientsAndHost)]
        public void WhipRpc(Vector3 from, Vector3 to, ulong shooter)
        {
            if (NetworkManager.LocalClientId == shooter) return;   // the shooter drew it already
            Whip(from, to, true);
        }

        /// <summary>Local-only blast (bomblets): every peer pops its own copy, so no RPC.</summary>
        public void LocalBlast(Vector3 pos, float radiusM, Color c)
        {
            StartCoroutine(Bubble(pos, Mathf.Max(radiusM * 1.4f, 0.4f), c, 0.3f));
            Burst(pos, 12, c);
            if (Time.time >= _impactSfxAt) { _impactSfxAt = Time.time + 0.04f; Play(boomClip, pos, 0.35f, Random.Range(1.3f, 1.7f)); }
        }

        /// <summary>Local-only impact puff (each peer simulates its own bullets, so no RPC).</summary>
        public void BulletImpact(Vector3 pos, ToolDef t, bool authority)
        {
            Burst(pos, t.id == "RailGun" ? 10 : 3, Bullets.BulletColor(t));
            if (Time.time >= _impactSfxAt) { _impactSfxAt = Time.time + 0.05f; Play(hitClip, pos, 0.25f, Random.Range(1.1f, 1.5f)); }
        }

        [Rpc(SendTo.ClientsAndHost)]
        public void ExplosionRpc(Vector3 pos, float radiusM, int tool, bool pulse)
        {
            var t = Balance.Tool(tool);
            Color c = t != null && t.id == "BlackHoleGrenade" ? new Color(0.6f, 0.4f, 1f) :
                      t != null && t.id == "NukeCrate" ? new Color(1f, 0.95f, 0.6f) : new Color(1f, 0.75f, 0.35f);
            StartCoroutine(Bubble(pos, Mathf.Max(radiusM * (pulse ? 0.8f : 1.6f), 0.6f), c, pulse ? 0.25f : 0.5f));
            Burst(pos, pulse ? 15 : 40, c);
            Play(boomClip, pos, pulse ? 0.4f : 1f, pulse ? 1.4f : Random.Range(0.85f, 1.05f));
            // camera shake for the local player (scaled by distance)
            var me = PlayerAvatar.Local;
            if (me)
            {
                float d = Vector3.Distance(me.transform.position, pos);
                UI.GameMenus.Instance?.Shake(Mathf.Clamp01(1f - d / (radiusM * 6f + 4f)) * (pulse ? 0.3f : 1f));
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        public void ImpactRpc(Vector3 pos, int tool)
        {
            Burst(pos, 6, Color.white);
            Play(hitClip, pos, 0.6f, Random.Range(0.9f, 1.2f));
        }

        [Rpc(SendTo.ClientsAndHost)]
        public void ShockwaveRpc(Vector3 pos, float radiusM)
        {
            StartCoroutine(Bubble(pos, radiusM * 2f, new Color(0.6f, 0.95f, 1f), 0.35f));
            Play(zapClip, pos, 0.8f, 1.6f);
        }

        /// <summary>Server -> everyone except the shooter (who already drew it locally).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void TracerRpc(Vector3 from, Vector3 to, int tool, ulong shooter)
        {
            if (NetworkManager.LocalClientId == shooter) return;
            LocalTracer(from, to, Balance.Tool(tool));
        }

        public void LocalTracer(Vector3 from, Vector3 to, ToolDef t)
        {
            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = glowMaterial;
            lr.positionCount = 2; lr.SetPosition(0, from); lr.SetPosition(1, to);
            lr.widthMultiplier = t != null && t.id == "RailGun" ? 0.12f : 0.025f;
            lr.startColor = lr.endColor = t != null && t.id == "RailGun" ? new Color(0.7f, 1f, 1f) : AeroPalette.Gold;
            StartCoroutine(Fade(lr, t != null && t.id == "RailGun" ? 0.5f : 0.08f));
            Play(zapClip, from, 0.3f, 1.2f);
        }

        IEnumerator Fade(LineRenderer lr, float time)
        {
            for (float t = 0; t < time; t += Time.deltaTime)
            {
                if (!lr) yield break;
                lr.widthMultiplier *= 0.93f;
                yield return null;
            }
            if (lr) Destroy(lr.gameObject);
        }

        public void BubbleLocal(Vector3 pos, float radius, Color c, float time) => StartCoroutine(Bubble(pos, radius, c, time));

        IEnumerator Bubble(Vector3 pos, float radius, Color c, float time)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(s.GetComponent<Collider>());
            s.transform.position = pos;
            var mr = s.GetComponent<MeshRenderer>();
            mr.sharedMaterial = glowMaterial;
            var block = new MaterialPropertyBlock();
            for (float t = 0; t < time; t += Time.deltaTime)
            {
                float k = t / time;
                s.transform.localScale = Vector3.one * radius * 2f * Mathf.Sqrt(k);
                c.a = 0.6f * (1 - k);
                block.SetColor("_BaseColor", c);
                mr.SetPropertyBlock(block);
                yield return null;
            }
            Destroy(s);
        }

        public void Burst(Vector3 pos, int count, Color c)
        {
            if (!sparkBurst) return;
            var ep = new ParticleSystem.EmitParams { position = pos, startColor = c, applyShapeToPosition = true };
            sparkBurst.Emit(ep, count);
        }

        public static void Play(AudioClip clip, Vector3 pos, float vol, float pitch)
        {
            if (!clip) return;
            var go = new GameObject("sfx");
            go.transform.position = pos;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip; a.volume = vol; a.pitch = pitch; a.spatialBlend = 1f; a.maxDistance = 60f;
            a.Play();
            Destroy(go, clip.length / Mathf.Max(pitch, 0.1f) + 0.1f);
        }
    }

    /// <summary>Loads tool models from Resources/ToolVisuals/{id} (generated by the project builder
    /// from Models/Tools/*.fbx). Falls back to a tinted primitive so the game runs without art.</summary>
    public static class ToolVisuals
    {
        public static GameObject Create(string id, Transform parent)
        {
            var prefab = Resources.Load<GameObject>($"ToolVisuals/{id}");
            GameObject go;
            if (prefab) go = Object.Instantiate(prefab, parent, false);
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetParent(parent, false);
                go.transform.localScale = new Vector3(0.06f, 0.06f, 0.35f);
                go.transform.localPosition = new Vector3(0, 0, 0.15f);
            }
            go.name = $"Tool_{id}";
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.Destroy(c);   // visuals never collide
            return go;
        }
    }
}
