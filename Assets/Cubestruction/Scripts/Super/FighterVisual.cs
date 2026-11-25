using Cubestruction.Player;
using Cubestruction.Tools;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>
    /// How a fighter looks in SUPER! (every peer, players and bots): the character's colours and headband,
    /// an aura that grows with the tier, glowing spiky hair once Ascended, a charge orb while charging,
    /// kicks and jabs on the body, move shouts, and a health bar over everyone else's head.
    /// Runs after AvatarVisual so it can override the legs and arms for a moment.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class FighterVisual : MonoBehaviour
    {
        FighterState _fs;
        PlayerAvatar _pa;
        Transform _head, _legR, _legL, _armR, _armL, _bodyRoot;
        Renderer[] _bodyRenderers;
        GameObject _aura, _hair, _band, _orb, _barRoot;
        Transform _barFill;
        TextMesh _label;
        Material _auraMat, _hairMat, _orbMat;
        byte _lastAnim; float _animT = -9f; FighterState.Anim _anim;
        int _lastChar = -1; bool _wasFighter;
        string _labelText;

        void Awake()
        {
            _fs = GetComponent<FighterState>();
            _pa = GetComponent<PlayerAvatar>();
        }

        void Start()
        {
            var av = GetComponentInChildren<AvatarVisual>(true);
            if (av) { _head = av.head; _legR = av.legR; _legL = av.legL; _armR = av.armR; _armL = av.armL; _bodyRoot = av.transform; }
            else
            {
                _bodyRoot = transform.Find("Visual");
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    switch (t.name) { case "Head": _head = t; break; case "LegR": _legR = t; break; case "LegL": _legL = t; break; case "ArmR": _armR = t; break; case "ArmL": _armL = t; break; }
            }
            if (_bodyRoot) _bodyRenderers = _bodyRoot.GetComponentsInChildren<Renderer>(true);
        }

        bool IsFighter => _fs && _fs.IsSpawned && _fs.inMatch.Value;
        bool IsLocal => _pa && _pa.IsOwner;

        void LateUpdate()
        {
            bool on = IsFighter;
            if (!on)
            {
                if (_wasFighter) { Show(false); if (_pa) GetComponentInChildren<AvatarVisual>()?.Tint(); _wasFighter = false; _lastChar = -1; }
                return;
            }
            _wasFighter = true;
            var def = _fs.Def;
            int tier = _fs.Tier;
            EnsureParts();
            if (_lastChar != _fs.character.Value) { _lastChar = _fs.character.Value; Recolour(def); }
            float s = transform.localScale.x;
            bool down = _fs.down.Value;
            if (_fs.Bot && _bodyRoot) _bodyRoot.gameObject.SetActive(!down);

            // ---- aura: none at Brawler, a shimmer from Energy, a blaze when Ascended
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * (6f + tier * 2f));
            float alpha = down ? 0f : tier >= 5 ? 0.32f + 0.1f * pulse : tier == 4 ? 0.22f + 0.08f * pulse : tier == 3 ? 0.08f + 0.04f * pulse : 0f;
            bool frozen = SuperDirector.Instance && _fs.Frozen(SuperDirector.Instance.Now);
            var ac = frozen ? new Color(0.75f, 0.95f, 1f) : def.aura;
            if (frozen) alpha = Mathf.Max(alpha, 0.35f);
            _aura.SetActive(alpha > 0.01f && !IsLocal);   // never around your own camera (it whites out the view)
            _auraMat.color = new Color(ac.r, ac.g, ac.b, alpha);
            _aura.transform.position = transform.position + Vector3.up * 0.55f * s;
            _aura.transform.localScale = new Vector3(1.1f, 1.6f + 0.2f * pulse * (tier >= 4 ? 1 : 0), 1.1f) * s * (tier >= 5 ? 1.25f : 1f);
            if (tier >= 4 && !down && Time.frameCount % (tier >= 5 ? 2 : 5) == 0) FxHub.Instance?.Burst(transform.position + Random.insideUnitSphere * 0.4f * s + Vector3.up * 0.3f * s, 1, def.aura);

            // ---- hair + headband
            if (_head)
            {
                _band.transform.SetPositionAndRotation(_head.position + _head.up * 0.06f * s, _head.rotation);
                _hair.transform.SetPositionAndRotation(_head.position + _head.up * 0.13f * s, _head.rotation);
                _hair.transform.localScale = Vector3.one * s * (tier >= 5 ? 1.25f : 1f);
            }
            bool showHead = !down && !IsLocal && _head;   // never in front of your own camera
            _band.SetActive(showHead);
            _hair.SetActive(showHead && tier >= 4);
            _hairMat.color = Color.Lerp(def.hair, Color.white, 0.25f * pulse);

            // ---- animation cues (kicks, jabs, charge orb)
            if (_fs.anim.Value != _lastAnim) { _lastAnim = _fs.anim.Value; _anim = (FighterState.Anim)(_lastAnim & 15); _animT = Time.time; }
            float t = Time.time - _animT;
            bool charging = _anim == FighterState.Anim.Charge && t < 3f && !IsLocal;
            _orb.SetActive(charging);
            if (charging)
            {
                bool sun = def.Ult.kind == MoveKind.Sphere && _fs.Tier >= 5;
                _orb.transform.position = sun ? transform.position + Vector3.up * 5.5f * s : _fs.Chest + _fs.Facing * 0.5f * s;
                _orb.transform.localScale = Vector3.one * s * (sun ? 0.6f + 6.2f * Mathf.Clamp01(t / 1.2f) : 0.15f + 0.3f * Mathf.Clamp01(t / 1.2f));
                _orbMat.color = new Color(def.aura.r, def.aura.g, def.aura.b, 0.85f);
            }
            PoseBody(t, s);
            UpdateBar(def, s, down);
        }

        void PoseBody(float t, float s)
        {
            if (t > 0.3f) return;
            float k = Mathf.Sin(Mathf.Clamp01(t / 0.3f) * Mathf.PI);
            var fwd = _fs.Facing;
            var rot = Quaternion.LookRotation(fwd, Vector3.up);
            switch (_anim)
            {
                case FighterState.Anim.Kick:
                case FighterState.Anim.Axe:
                    if (_legR) _legR.rotation = rot * Quaternion.Euler(_anim == FighterState.Anim.Axe ? -150f * k : -95f * k, 0, 0);
                    break;
                case FighterState.Anim.Launcher:
                    if (_legR) _legR.rotation = rot * Quaternion.Euler(-160f * k, 0, 0);
                    if (_armR && _fs.Bot) _armR.rotation = rot * Quaternion.Euler(180f * k, 0, 0);
                    break;
                case FighterState.Anim.Jab:
                case FighterState.Anim.Cross:
                case FighterState.Anim.Hook:
                case FighterState.Anim.Special:
                    // players' arms follow their real hands; bots get a punch
                    if (_fs.Bot)
                    {
                        var arm = _anim == FighterState.Anim.Cross ? _armL : _armR;
                        if (arm) arm.rotation = rot * Quaternion.Euler(-90f * k, _anim == FighterState.Anim.Hook ? 40f * k : 0, 0);
                    }
                    break;
                case FighterState.Anim.Beam:
                case FighterState.Anim.Ultimate:
                    if (_fs.Bot) { if (_armR) _armR.rotation = rot * Quaternion.Euler(-90f, 0, 0); if (_armL) _armL.rotation = rot * Quaternion.Euler(-90f, 0, 0); }
                    break;
            }
        }

        void UpdateBar(FighterDef def, float s, bool down)
        {
            bool show = !IsLocal && !down;
            _barRoot.SetActive(show);
            if (!show) return;
            _barRoot.transform.position = transform.position + Vector3.up * (1.35f * s + (_fs.Tier >= 4 ? 0.25f : 0f));
            var cam = Camera.main;
            if (cam) _barRoot.transform.rotation = Quaternion.LookRotation(_barRoot.transform.position - cam.transform.position);
            float f = Mathf.Clamp01(_fs.hp.Value / Mathf.Max(1f, _fs.maxHp.Value));
            _barFill.localScale = new Vector3(f, 1f, 1f);
            _barFill.localPosition = new Vector3((f - 1f) * 0.5f, 0, -0.001f);
            var d = SuperDirector.Instance;
            string txt = $"{_fs.DisplayName}  T{_fs.Tier}";
            if (txt != _labelText)
            {
                _labelText = txt; _label.text = txt;
                _label.color = d && d.teams.Value ? SuperDirector.TeamColors[_fs.team.Value % 2] : Color.white;
            }
        }

        void EnsureParts()
        {
            if (_aura) return;
            var glow = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            _aura = Prim(PrimitiveType.Sphere, "Aura", null);
            _auraMat = new Material(glow); _aura.GetComponent<MeshRenderer>().material = _auraMat;

            _hair = new GameObject("Hair");
            _hairMat = new Material(glow);
            for (int i = 0; i < 7; i++)
            {
                var spike = Prim(PrimitiveType.Cube, "Spike", _hair.transform);
                float a = i / 7f * 360f;
                spike.transform.localRotation = Quaternion.Euler(-25f - (i % 2) * 20f, a, 0) * Quaternion.Euler(-60f, 0, 0);
                spike.transform.localPosition = Quaternion.Euler(0, a, 0) * new Vector3(0, 0.02f, -0.05f);
                spike.transform.localScale = new Vector3(0.05f, 0.05f, 0.2f + 0.06f * (i % 3));
                spike.GetComponent<MeshRenderer>().sharedMaterial = _hairMat;
            }
            var top = Prim(PrimitiveType.Cube, "Spike", _hair.transform);
            top.transform.localPosition = new Vector3(0, 0.08f, 0); top.transform.localRotation = Quaternion.Euler(-80f, 0, 0);
            top.transform.localScale = new Vector3(0.06f, 0.06f, 0.26f);
            top.GetComponent<MeshRenderer>().sharedMaterial = _hairMat;

            _band = Prim(PrimitiveType.Cylinder, "Headband", null);
            _band.transform.localScale = new Vector3(0.36f, 0.02f, 0.36f);

            _orb = Prim(PrimitiveType.Sphere, "ChargeOrb", null);
            _orbMat = new Material(glow); _orb.GetComponent<MeshRenderer>().material = _orbMat;

            _barRoot = new GameObject("FighterBar");
            var bg = Prim(PrimitiveType.Quad, "Back", _barRoot.transform);
            bg.transform.localScale = new Vector3(1f, 0.09f, 1f);
            bg.GetComponent<MeshRenderer>().material = new Material(glow) { color = new Color(0f, 0.1f, 0.25f, 0.6f) };
            var fill = Prim(PrimitiveType.Quad, "Fill", _barRoot.transform);
            _barFill = fill.transform;
            _barFill.localScale = Vector3.one;
            var fillRoot = new GameObject("FillRoot").transform; fillRoot.SetParent(_barRoot.transform, false);
            fill.transform.SetParent(fillRoot, false);
            fill.transform.localScale = new Vector3(1f, 0.07f, 1f);
            _barFill = fillRoot;
            fill.GetComponent<MeshRenderer>().material = new Material(glow) { color = new Color(0.5f, 1f, 0.35f, 0.95f) };
            var lbl = new GameObject("Name").AddComponent<TextMesh>();
            lbl.transform.SetParent(_barRoot.transform, false);
            lbl.transform.localPosition = new Vector3(0, 0.16f, 0); lbl.transform.localScale = Vector3.one * 0.02f;
            lbl.font = UI.AeroTheme.Font; lbl.GetComponent<MeshRenderer>().sharedMaterial = lbl.font.material;
            lbl.fontSize = 60; lbl.anchor = TextAnchor.MiddleCenter; lbl.alignment = TextAlignment.Center;
            _label = lbl;
        }

        static GameObject Prim(PrimitiveType t, string name, Transform parent)
        {
            var g = GameObject.CreatePrimitive(t);
            g.name = name;
            Destroy(g.GetComponent<Collider>());
            if (parent) g.transform.SetParent(parent, false);
            g.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g;
        }

        void Recolour(FighterDef def)
        {
            if (_bodyRenderers != null)
            {
                var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", def.color);
                foreach (var r in _bodyRenderers) if (r) r.SetPropertyBlock(block);
            }
            var bandBlock = new MaterialPropertyBlock(); bandBlock.SetColor("_BaseColor", Color.Lerp(def.aura, Color.white, 0.2f));
            _band.GetComponent<Renderer>().SetPropertyBlock(bandBlock);
            if (_band.GetComponent<Renderer>().sharedMaterial == null && FxHub.Instance) _band.GetComponent<Renderer>().sharedMaterial = FxHub.Instance.glowMaterial;
        }

        void Show(bool on)
        {
            foreach (var g in new[] { _aura, _hair, _band, _orb, _barRoot }) if (g) g.SetActive(on);
            if (_fs && _fs.Bot && _bodyRoot) _bodyRoot.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------------ one-shots

        public void Shout(string text)
        {
            if (!_fs) return;
            HitSparks.Text(_fs.Chest + Vector3.up * 0.9f * transform.localScale.x, text.ToUpper() + "!", _fs.Def.aura, 1.3f);
            var fx = FxHub.Instance;
            if (fx) FxHub.Play(fx.zapClip, _fs.Chest, 0.5f, 1.8f);
        }

        public void PowerUpBurst(byte tier)
        {
            var fx = FxHub.Instance; if (!fx || !_fs) return;
            float s = transform.localScale.x;
            fx.BubbleLocal(_fs.Chest, (2f + tier * 0.6f) * s, _fs.Def.aura, 0.6f);
            fx.BubbleLocal(_fs.Chest, (1f + tier * 0.3f) * s, Color.white, 0.3f);
            fx.Burst(_fs.Chest, 60, _fs.Def.aura);
            FxHub.Play(fx.boomClip, _fs.Chest, 1f, 0.6f + tier * 0.1f);
            HitSparks.Text(_fs.Chest + Vector3.up * 1.2f * s, $"TIER {tier}: {Roster.TierNames[Mathf.Clamp(tier, 0, 5)].ToUpper()}!", _fs.Def.aura, 2f);
            var me = PlayerAvatar.Local;
            if (me) UI.GameMenus.Instance?.Shake(Mathf.Clamp01(1f - Vector3.Distance(me.transform.position, transform.position) / 25f) * 0.7f);
        }

        void OnDestroy()
        {
            foreach (var g in new[] { _aura, _hair, _band, _orb, _barRoot }) if (g) Destroy(g);
        }
    }

    /// <summary>Floating damage numbers and callouts ("PARRY!", "K.O.!", move names).</summary>
    public class HitSparks : MonoBehaviour
    {
        TextMesh _t; float _age, _life; Vector3 _vel; Color _c;

        public static void Spawn(Vector3 pos, float damage, bool blocked, Color c)
        {
            if (damage < 1f) return;
            Text(pos + Random.insideUnitSphere * 0.2f, blocked ? $"{damage:0}" : $"{damage:0}", blocked ? new Color(0.8f, 0.85f, 0.9f) : Color.Lerp(Color.white, c, 0.5f), 0.7f, blocked ? 0.6f : Mathf.Clamp(0.7f + damage / 200f, 0.7f, 1.8f));
        }

        public static void Text(Vector3 pos, string text, Color c, float life = 1f, float size = 1.2f)
        {
            var go = new GameObject("HitText");
            go.transform.position = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = UI.AeroTheme.Font; go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
            tm.text = text; tm.fontSize = 64; tm.fontStyle = FontStyle.Bold; tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
            tm.color = c;
            go.transform.localScale = Vector3.one * 0.012f * size;
            var hs = go.AddComponent<HitSparks>();
            hs._t = tm; hs._life = life; hs._c = c; hs._vel = Vector3.up * 1.2f + Random.insideUnitSphere * 0.3f;
        }

        void Update()
        {
            _age += Time.deltaTime;
            transform.position += _vel * Time.deltaTime;
            _vel *= 1f - Mathf.Clamp01(Time.deltaTime * 2f);
            var cam = Camera.main;
            if (cam) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            float k = _age / _life;
            if (_t) _t.color = new Color(_c.r, _c.g, _c.b, 1f - k * k);
            if (_age < 0.1f) transform.localScale *= 1.04f;
            if (_age >= _life) Destroy(gameObject);
        }
    }
}
