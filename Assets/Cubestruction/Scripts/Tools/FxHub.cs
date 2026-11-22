using System.Collections;
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

        void Awake() { Instance = this; }

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

        void Burst(Vector3 pos, int count, Color c)
        {
            if (!sparkBurst) return;
            var ep = new ParticleSystem.EmitParams { position = pos, startColor = c, applyShapeToPosition = true };
            sparkBurst.Emit(ep, count);
        }

        static void Play(AudioClip clip, Vector3 pos, float vol, float pitch)
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
