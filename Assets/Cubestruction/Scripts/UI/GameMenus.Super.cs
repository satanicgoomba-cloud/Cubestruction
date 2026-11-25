using System.Linq;
using Cubestruction.Net;
using Cubestruction.Player;
using Cubestruction.Super;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Cubestruction.UI
{
    /// <summary>SUPER! / Sandbox screens: the character select in the lobby, the fighter HUD, solo Sandbox and
    /// Continue from the main menu, and the time-stop tint.</summary>
    public partial class GameMenus
    {
        // lobby
        Button[] _fighterBtns; Text _fighterInfo; Button _modeSuper, _modeSandbox, _teamsBtn, _lobbyLoadBtn;
        RectTransform _fighterRow, _superOpts;
        // main menu
        Button _continueBtn;
        // pause
        RectTransform _pauseSave, _pauseLobby, _pauseHotbar, _pauseFighter; Text _pauseNote;
        // fight HUD
        RectTransform _fightHud;
        Image _fHp, _fPow, _timeTint;
        Text _fName, _fTier, _fCombo, _fScore, _fTimer, _fHint;
        readonly MoveCard[] _moveCards = new MoveCard[4];
        float _timeTintUntil;
        // solo start / continue
        GameMode? _pendingSolo; SaveData _pendingLoad; float _pendingAt;

        class MoveCard { public Image orb, cool; public Text name, key, cost; }

        static readonly string[] StyleRanks = { "", "", "", "D", "D", "C", "C", "C", "B", "B", "B", "B", "A" };

        // ================================================================== lobby: character select

        void BuildFighterLobbyRows(RectTransform col)
        {
            var row = AeroTheme.HRow(col, "Fighters", 8); row.Size(62);
            _fighterRow = (RectTransform)row.transform;
            _fighterBtns = new Button[Roster.All.Length];
            for (int i = 0; i < Roster.All.Length; i++)
            {
                int idx = i;
                var def = Roster.All[i];
                _fighterBtns[i] = AeroTheme.Button(row.transform, $"{def.name}\n<size=15>{def.title}</size>", () => PickFighter(idx), def.color, 20);
            }
            _fighterInfo = AeroTheme.Label(col, "", 19, TextAnchor.MiddleCenter, AeroPalette.Ink, false); _fighterInfo.Size(58);
            _fighterInfo.supportRichText = true;
            var opts = AeroTheme.HRow(col, "SuperOpts"); opts.Size(52);
            _superOpts = (RectTransform)opts.transform;
            _teamsBtn = AeroTheme.Cycle(opts.transform, "Rules", new[] { "Break the cube", "Brawl: free-for-all", "Brawl: teams" }, () => SuperDirector.Rules, v => SuperDirector.Rules = v);
            var bots = new[] { -1, 0, 1, 2, 3, 5 };
            AeroTheme.Cycle(opts.transform, "Brawl bots", new[] { "Fill to 4", "None", "1", "2", "3", "5" },
                () => Mathf.Max(0, System.Array.IndexOf(bots, SuperDirector.BotsOverride)), v => SuperDirector.BotsOverride = bots[v]);
            RefreshFighterLobby();
        }

        void PickFighter(int i)
        {
            GameSettings.Fighter = i;
            PlayerPrefs.Save();
            var fs = PlayerAvatar.Local ? PlayerAvatar.Local.GetComponent<FighterState>() : null;
            if (fs && fs.IsSpawned && fs.IsOwner) fs.character.Value = (byte)i;
            RefreshFighterLobby();
        }

        void RefreshFighterLobby()
        {
            if (_fighterBtns == null) return;
            int sel = Mathf.Clamp(GameSettings.Fighter, 0, Roster.All.Length - 1);
            for (int i = 0; i < _fighterBtns.Length; i++) _fighterBtns[i].transform.localScale = Vector3.one * (i == sel ? 1.08f : 0.9f);
            var d = Roster.All[sel];
            string moves = string.Join("  ·  ", d.moves.Select((m, k) => $"{(k == 3 ? "ULT" : ((char)('A' + k)).ToString())}: {m.name}"));
            _fighterInfo.text = $"<b>{d.name} \"{d.title}\"</b>, {d.style}.  {d.blurb}\n<size=16>{moves}</size>";
        }

        void UpdateFighterLobby(bool host)
        {
            bool fightMode = Roles.IsFighterMode(_lobbyMode);
            _modeSuper.interactable = host; _modeSandbox.interactable = host;
            _modeSuper.transform.localScale = Vector3.one * (_lobbyMode == GameMode.Super ? 1.06f : 0.94f);
            _modeSandbox.transform.localScale = Vector3.one * (_lobbyMode == GameMode.Sandbox ? 1.06f : 0.94f);
            _superOpts.gameObject.SetActive(host && fightMode);
            _lobbyLoadBtn.gameObject.SetActive(host && SaveGame.Exists);
            // the fighter pick always shows (clients don't know the host's mode until it starts)
            _fighterRow.gameObject.SetActive(true);
            _fighterInfo.gameObject.SetActive(true);
        }

        string FighterLobbyHint()
        {
            if (SuperDirector.WantBrawl)
                return _lobbyMode == GameMode.Super
                    ? "SUPER! Brawl: fight each other in a voxel arena. Fill Power by fighting, Power Up to ULTIMATE. Most KOs in 5 minutes wins."
                    : "Sandbox Brawl: every move unlocked, infinite Power, training dummies, and a whole arena to wreck.";
            return _lobbyMode == GameMode.Super
                ? "SUPER!: break the cube with anime moves. Start with punches; every voxel you break fills Power. Power Up to unlock bigger moves (the tough inner layers need them), all the way to your ULTIMATE."
                : "Sandbox: break the cube with every move unlocked and infinite Power.";
        }

        // ================================================================== main menu: solo Sandbox + Continue

        void BuildSoloRow(RectTransform col)
        {
            var row = AeroTheme.HRow(col, "Solo"); row.Size(64);
            AeroTheme.Button(row.transform, "Sandbox (solo)", () => StartSolo(GameMode.Sandbox, null), new Color(1f, 0.5f, 0.2f), 24);
            _continueBtn = AeroTheme.Button(row.transform, "Continue", () => { var d = SaveGame.Read(); if (d != null) StartSolo(d.Mode, d); else Toast("No save found.", AeroPalette.Danger); }, AeroPalette.Gold, 22);
        }

        void StartSolo(GameMode m, SaveData load)
        {
            _pendingSolo = m; _pendingLoad = load; _pendingAt = Time.unscaledTime;
            if (load != null) PickFighter(load.fighter);
            var nb = NetBootstrap.Instance;
            if (nb != null && !nb.IsConnected) nb.HostLan(0);
        }

        void LoadFromLobby()
        {
            var d = SaveGame.Read();
            if (d == null) { Toast("No save found.", AeroPalette.Danger); return; }
            PickFighter(d.fighter);
            MatchManager.Instance?.ServerStartMatch(d.Mode, d);
        }

        void UpdatePendingSolo(NetworkManager nm, MatchManager mm)
        {
            if (_continueBtn)
            {
                bool has = SaveGame.Exists;
                _continueBtn.interactable = has;
                if (_screen == Screen.Main && Time.frameCount % 30 == 0)
                {
                    var d = has ? SaveGame.Read() : null;
                    _continueBtn.SetLabel(d != null ? $"Continue\n<size=14>{d.Summary}</size>" : "Continue\n<size=14>(no save yet)</size>");
                }
            }
            if (_pendingSolo == null) return;
            if (Time.unscaledTime - _pendingAt > 20f) { _pendingSolo = null; _pendingLoad = null; return; }
            if (_screen != Screen.Lobby || nm == null || !nm.IsServer || mm == null || !mm.IsSpawned || PlayerAvatar.Local == null) return;
            var m = _pendingSolo.Value; var load = _pendingLoad;
            _pendingSolo = null; _pendingLoad = null;
            _lobbyMode = m;
            mm.ServerStartMatch(m, load);
            if (load != null) Toast("Loaded your save!", AeroPalette.Grass);
        }

        // ================================================================== fight HUD

        void BuildFightHud()
        {
            _fightHud = AeroTheme.Rect(_hudCanvas.transform, "FightHUD").Fill();
            _fightHud.gameObject.SetActive(false);
            var root = _fightHud;
            if (IsVR)
            {
                var back = AeroTheme.Panel(root, "Strip"); back.color = new Color(1, 1, 1, 0.6f); back.rectTransform.Fill();
                _fName = AeroTheme.Label(back.transform, "", 34, TextAnchor.UpperLeft, AeroPalette.Ink, false);
                _fName.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -14), new Vector2(-50, 46), new Vector2(0.5f, 1));
                _fHp = AeroTheme.Bar(back.transform, "HP", AeroPalette.Lime);
                ((RectTransform)_fHp.transform.parent).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -66), new Vector2(-50, 30), new Vector2(0.5f, 1));
                _fPow = AeroTheme.Bar(back.transform, "Power", AeroPalette.Gold);
                ((RectTransform)_fPow.transform.parent).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -104), new Vector2(-50, 24), new Vector2(0.5f, 1));
                _fTier = AeroTheme.Label(back.transform, "", 28, TextAnchor.UpperLeft, AeroPalette.SkyDeep, false);
                _fTier.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -136), new Vector2(-50, 40), new Vector2(0.5f, 1));
                _fHint = AeroTheme.Label(back.transform, "", 24, TextAnchor.UpperLeft, AeroPalette.Ink, false);
                _fHint.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -180), new Vector2(-50, 200), new Vector2(0.5f, 1));
                _fHint.supportRichText = true;
                _fScore = AeroTheme.Label(back.transform, "", 24, TextAnchor.UpperRight, AeroPalette.Ink, false);
                _fScore.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -14), new Vector2(-50, 46), new Vector2(0.5f, 1));
                _fCombo = AeroTheme.Label(back.transform, "", 40, TextAnchor.LowerRight, AeroPalette.Danger, false);
                _fCombo.rectTransform.Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 14), new Vector2(-50, 60), new Vector2(0.5f, 0));
                _fTimer = _fScore;
                return;
            }

            // top-left: who you are, health, Power, tier
            var panel = AeroTheme.Panel(root, "Status");
            panel.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -24), new Vector2(560, 190), new Vector2(0, 1));
            _fName = AeroTheme.Label(panel.transform, "", 30, TextAnchor.MiddleLeft, AeroPalette.Ink, false);
            _fName.fontStyle = FontStyle.Bold; _fName.supportRichText = true;
            _fName.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -12), new Vector2(-40, 40), new Vector2(0.5f, 1));
            _fHp = AeroTheme.Bar(panel.transform, "HP", AeroPalette.Lime);
            ((RectTransform)_fHp.transform.parent).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -58), new Vector2(-40, 34), new Vector2(0.5f, 1));
            _fPow = AeroTheme.Bar(panel.transform, "Power", AeroPalette.Gold);
            ((RectTransform)_fPow.transform.parent).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -100), new Vector2(-40, 26), new Vector2(0.5f, 1));
            _fTier = AeroTheme.Label(panel.transform, "", 24, TextAnchor.MiddleLeft, AeroPalette.SkyDeep, false);
            _fTier.supportRichText = true;
            _fTier.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -134), new Vector2(-40, 44), new Vector2(0.5f, 1));

            // top-right: clock + KO board
            _fTimer = AeroTheme.Label(root, "5:00", 40, TextAnchor.MiddleRight, Color.white);
            _fTimer.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -24), new Vector2(300, 56), new Vector2(1, 1));
            _fTimer.gameObject.AddComponent<Outline>().effectColor = new Color(0.05f, 0.3f, 0.6f, 0.8f);
            var board = AeroTheme.Panel(root, "Board"); board.color = new Color(1, 1, 1, 0.55f);
            board.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -86), new Vector2(380, 190), new Vector2(1, 1));
            _fScore = AeroTheme.Label(board.transform, "", 21, TextAnchor.UpperLeft, AeroPalette.Ink, false);
            _fScore.rectTransform.Fill(14); _fScore.supportRichText = true;

            // right: combo counter + style rank
            _fCombo = AeroTheme.Label(root, "", 54, TextAnchor.MiddleRight, AeroPalette.Gold);
            _fCombo.rectTransform.Place(new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-40, 40), new Vector2(420, 140), new Vector2(1, 0.5f));
            _fCombo.fontStyle = FontStyle.Bold; _fCombo.supportRichText = true;
            _fCombo.gameObject.AddComponent<Outline>().effectColor = new Color(0.5f, 0.1f, 0f, 0.9f);

            // crosshair
            var cross = AeroTheme.Image(root, "Crosshair", AeroTheme.Orb, new Color(1, 1, 1, 0.85f));
            cross.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(12, 12)); cross.raycastTarget = false;

            // bottom: the four specials
            bool mobile = Application.isMobilePlatform || _forceMobile;
            var bar = AeroTheme.Panel(root, "Moves"); bar.color = new Color(1, 1, 1, 0.5f); bar.raycastTarget = false;
            bar.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(mobile ? -260 : 0, 16), new Vector2(720, 140), new Vector2(0.5f, 0));
            var row = AeroTheme.HRow(bar.transform, "Row", 12); ((RectTransform)row.transform).Fill(10);
            for (int i = 0; i < 4; i++)
            {
                var c = new MoveCard();
                var cell = AeroTheme.Rect(row.transform, "Move" + i);
                c.orb = AeroTheme.Image(cell, "Orb", AeroTheme.Pill, Color.white); c.orb.rectTransform.Fill(); c.orb.raycastTarget = false;
                c.name = AeroTheme.Label(c.orb.transform, "", 19, TextAnchor.MiddleCenter, Color.white, false);
                c.name.rectTransform.Fill(8); c.name.fontStyle = FontStyle.Bold;
                c.name.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0.15f, 0.35f, 0.8f);
                c.cool = AeroTheme.Image(c.orb.transform, "Cool", AeroTheme.Pill, new Color(0.05f, 0.1f, 0.25f, 0.6f));
                c.cool.rectTransform.Fill(); c.cool.type = Image.Type.Filled; c.cool.fillMethod = Image.FillMethod.Horizontal; c.cool.fillAmount = 0; c.cool.raycastTarget = false;
                c.key = AeroTheme.Label(cell, "", 16, TextAnchor.UpperLeft, AeroPalette.Ink, false);
                c.key.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(10, 18), new Vector2(0, 22), new Vector2(0, 0));
                c.cost = AeroTheme.Label(cell, "", 15, TextAnchor.UpperRight, AeroPalette.Ink, false);
                c.cost.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(-10, 18), new Vector2(0, 22), new Vector2(1, 0));
                _moveCards[i] = c;
            }
            _fHint = AeroTheme.Label(root, "", 22, TextAnchor.MiddleCenter, Color.white);
            _fHint.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(mobile ? -260 : 0, 186), new Vector2(1100, 34), new Vector2(0.5f, 0));
            _fHint.gameObject.AddComponent<Outline>().effectColor = new Color(0.05f, 0.3f, 0.6f, 0.8f);
            _fHint.supportRichText = true;

            // time stop: the world goes grey
            _timeTint = AeroTheme.Image(_hudCanvas.transform, "TimeStop", null, new Color(0.45f, 0.45f, 0.55f, 0f));
            _timeTint.rectTransform.Fill(); _timeTint.raycastTarget = false;
            _timeTint.transform.SetAsFirstSibling();
        }

        string KeyFor(int slot, PlayerAvatar me)
        {
            if (me.IsVR) return new[] { "A", "X", "hold B", "fist up + trigger" }[slot];
            if (Application.isMobilePlatform || _forceMobile) return new[] { "A", "B", "C", "ULT" }[slot];
            return new[] { "Q", "E", "R (hold)", "G" }[slot];
        }

        int _lastFightTimer = -1; long _boardSig = -1;

        void UpdateFightHud(PlayerAvatar me)
        {
            var d = SuperDirector.Instance;
            var fs = me.GetComponent<FighterState>();
            var fc = FighterController.Local;
            if (d == null || fs == null) return;
            var def = fs.Def;
            bool sandbox = d.IsSandbox;
            int tier = fs.Tier;
            string col = ColorUtility.ToHtmlStringRGB(def.color * 0.8f);
            _fName.text = $"<color=#{col}>{def.name}</color> \"{def.title}\"   <size=20>{fs.hp.Value:0} / {fs.maxHp.Value:0}</size>";
            AeroTheme.SetBar(_fHp, fs.hp.Value / Mathf.Max(1f, fs.maxHp.Value));
            _fHp.color = fs.hp.Value < fs.maxHp.Value * 0.3f ? AeroPalette.Danger : AeroPalette.Lime;
            AeroTheme.SetBar(_fPow, fs.power.Value / Roster.PowerMax);
            bool full = fs.power.Value >= Roster.PowerMax - 0.01f && tier < Roster.MaxTier && !sandbox;
            _fPow.color = full ? Color.Lerp(AeroPalette.Gold, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.time * 10f)) : tier == 4 ? new Color(1f, 0.6f, 0.2f) : AeroPalette.Gold;
            string pips = "";
            for (int i = 1; i <= Roster.MaxTier; i++) pips += i <= tier ? "●" : "○";
            string powerKey = me.IsVR ? "grips + triggers" : Application.isMobilePlatform || _forceMobile ? "POWER" : "F";
            _fTier.text = sandbox ? $"{pips}  <b>SANDBOX</b> · everything unlocked"
                : full ? $"{pips}  <b>POWER FULL! Press {powerKey} to power up</b>"
                : $"{pips}  Tier {tier}: <b>{Roster.TierNames[tier]}</b>" + (tier == 4 && d.IsBrawl ? "  · draining" : tier == 5 ? "  · ULTIMATE READY" : "");

            bool brawl = d.IsBrawl;
            int secs = sandbox || !brawl ? (int)(MatchManager.Instance ? MatchManager.Instance.elapsed.Value : 0) : Mathf.CeilToInt(d.timeLeft.Value);
            if (secs != _lastFightTimer) { _lastFightTimer = secs; _fTimer.text = (sandbox ? "" : "") + MatchManager.FormatTime(secs); if (IsVR) _lastFightTimer = -1; }

            // KO board (VR shares the timer label)
            var cubeNow = Voxels.VoxelCube.Instance;
            if (!brawl && cubeNow != null && cubeNow.IsSpawned && (Time.frameCount % 10 == 0 || IsVR))
            {
                float frac = cubeNow.alive.Value / (float)Mathf.Max(1, cubeNow.Total);
                var sbc = new System.Text.StringBuilder();
                if (IsVR) sbc.Append(MatchManager.FormatTime(secs)).Append("   ");
                sbc.Append($"<b>{cubeNow.alive.Value:N0}</b> voxels left ({frac * 100f:0.0}%)" + (IsVR ? "   " : "\n"));
                foreach (var p in PlayerAvatar.All.OrderByDescending(p => p.voxels.Value).Take(5))
                {
                    var pf = p.GetComponent<FighterState>();
                    var c = pf ? pf.Def.color : Color.white;
                    sbc.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c * 0.8f)}>●</color> {p.displayName.Value}{(p == me ? " (you)" : "")}  {p.voxels.Value:N0}" + (IsVR ? "   " : "\n"));
                }
                _fScore.text = sbc.ToString();
            }
            long sig = 0;
            foreach (var f in FighterState.All) sig = sig * 31 + f.kos.Value * 7 + f.falls.Value + (f.down.Value ? 1 : 0);
            if (brawl && (sig != _boardSig || IsVR))
            {
                _boardSig = sig;
                var list = FighterState.All.Where(f => f.inMatch.Value).OrderByDescending(f => f.kos.Value).ThenBy(f => f.falls.Value).Take(6);
                var sb = new System.Text.StringBuilder();
                if (IsVR) sb.Append(MatchManager.FormatTime(secs)).Append("   ");
                foreach (var f in list)
                {
                    var c = d.teams.Value ? SuperDirector.TeamColors[f.team.Value % 2] : f.Def.color;
                    string you = f == fs ? " (you)" : "";
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c * 0.8f)}>●</color> {f.DisplayName}{you}  <b>{f.kos.Value}</b> KO{(f.down.Value ? "  (down)" : "")}" + (IsVR ? "   " : "\n"));
                }
                _fScore.text = sb.ToString();
            }

            // combo + style
            int combo = fs.combo.Value;
            if (combo >= 2)
            {
                string rank = combo >= 18 ? "S" : combo < StyleRanks.Length ? StyleRanks[combo] : "A";
                _fCombo.text = $"{combo} HITS!" + (rank.Length > 0 ? $"\n<size=40>Style {rank}</size>" : "");
                _fCombo.transform.localScale = Vector3.one * (1f + Mathf.Clamp01(combo / 30f) * 0.3f);
            }
            else _fCombo.text = "";

            // move cards
            string hint = null;
            if (!IsVR)
                for (int i = 0; i < 4; i++)
                {
                    var c = _moveCards[i]; var m = def.moves[i];
                    bool unlocked = fc != null ? fc.Unlocked(i) : sandbox || tier >= m.tier;
                    bool afford = fc == null || fc.Affordable(i);
                    float cd = fc != null ? fc.CooldownLeft(i) : 0f;
                    c.name.text = unlocked ? m.name : $"{m.name}\n<size=14>Tier {(i == 3 ? 5 : m.tier)}</size>";
                    c.name.supportRichText = true;
                    c.orb.color = !unlocked ? new Color(0.6f, 0.65f, 0.75f, 0.7f) : i == 3 ? Color.Lerp(def.aura, Color.white, 0.3f + 0.2f * Mathf.Sin(Time.time * 8f)) : afford ? def.color : new Color(0.5f, 0.5f, 0.55f);
                    c.cool.fillAmount = cd > 0 ? Mathf.Clamp01(cd / Mathf.Max(0.1f, m.cooldown)) : 0f;
                    c.key.text = KeyFor(i, me);
                    c.cost.text = sandbox || i == 3 ? "" : $"{m.cost:0} power";
                }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 4; i++)
                {
                    var m = def.moves[i];
                    bool unlocked = fc != null ? fc.Unlocked(i) : sandbox || tier >= m.tier;
                    float cd = fc != null ? fc.CooldownLeft(i) : 0;
                    sb.Append(unlocked ? $"<b>{KeyFor(i, me)}</b> {m.name}{(cd > 0 ? $" ({cd:0.0}s)" : "")}\n" : $"<color=#8899aa>{KeyFor(i, me)} {m.name} · Tier {(i == 3 ? 5 : m.tier)}</color>\n");
                }
                hint = sb.ToString();
            }
            if (fc != null && fc.InClash) hint = IsVR ? hint : "<b>BEAM CLASH! MASH PUNCH!</b>";
            else if (fc != null && fc.FlyLevel > 0 && !IsVR && fc.Flying) hint = "Flying · Space up · Ctrl down" + (fc.FlyLevel == 1 ? " · short bursts until next tier" : "");
            else if (!IsVR && hint == null)
            {
                bool mob = Application.isMobilePlatform || _forceMobile;
                string fly = fc != null && fc.FlyLevel == 0 ? $" (Tier {def.flyTier})" : "";
                hint = !d.IsBrawl
                    ? (mob ? "Punch and Kick the cube! Breaking voxels fills Power" : "LMB punch · RMB kick · break voxels to fill Power · Space in the air: fly" + fly)
                    : mob ? "Punch · Kick · hold Block (tap just before a hit to parry)"
                          : "LMB punch · RMB kick (look up: launcher) · C block / parry · Space in the air: fly" + fly;
            }
            _fHint.text = hint ?? "";

            // the time-stop tint
            if (_timeTint)
            {
                bool stopped = d.timeStopUntil.Value > d.Now || Time.unscaledTime < _timeTintUntil;
                float a = stopped ? 0.38f : 0f;
                _timeTint.color = new Color(0.45f, 0.45f, 0.55f, Mathf.MoveTowards(_timeTint.color.a, a, Time.unscaledDeltaTime * 2f));
            }
        }

        public void TimeStopTint(float secs) { _timeTintUntil = Time.unscaledTime + secs; }

        string SuperEndScoreboard(MatchManager mm)
        {
            var sd = SuperDirector.Instance;
            return Roles.IsFighterMode(mm.mode.Value) && sd != null && sd.scoreboard.Value.Length > 0 ? sd.scoreboard.Value.ToString() : null;
        }
    }
}
