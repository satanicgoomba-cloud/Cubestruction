using System.Collections.Generic;
using System.Linq;
using Cubestruction.Net;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Cubestruction.UI
{
    /// <summary>
    /// Every screen, built from code in the Frutiger Aero style:
    /// main menu → lobby → miner HUD + Aero hotbar + shop → end screen, plus settings/pause and toasts.
    /// Flat clients use a Screen Space Overlay canvas; VR uses a lazily-following world-space panel
    /// (laser pointer) and a small hotbar strip at the bottom of the view.
    /// Before a player is spawned it also owns a menu camera (and a tracked menu rig in VR).
    /// </summary>
    public partial class GameMenus : MonoBehaviour
    {
        public static GameMenus Instance { get; private set; }

        public bool IsVR { get; private set; }
        public bool ShopOpen => _shopOpen;
        /// <summary>True while a menu needs the mouse/touch on a flat client (FlatRig stops moving/looking).</summary>
        public bool BlocksGameplayInput => !IsVR && (_screen != Screen.Hud || _shopOpen || _pauseOpen || _settingsOpen || _hotbarEditOpen);

        enum Screen { Main, Connecting, Lobby, Hud, End }
        Screen _screen = (Screen)(-1);
        bool _shopOpen, _pauseOpen, _settingsOpen, _hotbarEditOpen;
        RectTransform _hotbarEdit, _hotbarBack; RectTransform _pickGrid; Text _pickInfo; readonly List<Button> _pickButtons = new List<Button>(); long _pickSig = -1;
        GameMode _lobbyMode = GameMode.Mining;

        // canvases
        Canvas _menuCanvas;          // overlay (flat) or world-space panel (VR)
        Canvas _hudCanvas;           // overlay (flat) or camera-attached strip (VR)
        Transform _menuAnchor;       // VR: the world-space panel transform
        VRPointer _pointer;

        // screens
        RectTransform _main, _connecting, _lobby, _hud, _shop, _end, _settings, _pause, _toasts, _sky;
        Text _status, _connectingText, _lobbyCode, _lobbyPlayers, _lobbyHint, _endTitle, _endStats;
        Button _startBtn, _modeMining, _modeSiege, _modeTitan, _endLobbyBtn, _voteRaider, _voteCaster;
        RectTransform _voteRow;
        InputField _nameField, _codeField, _ipField;
        Image[] _colorSwatches;

        // HUD
        Text _moneyText, _timerText, _voxelText, _toolInfo;
        Image _voxelBar;
        RectTransform _hotbarRow;
        readonly List<HotbarSlot> _slots = new List<HotbarSlot>();
        MobileControls _mobile;
        int _lastMoney = int.MinValue, _lastTimer = -1, _lastAlive = -1;
        long _hotbarSig = -1;

        // shop
        Text _shopMoney, _shopPage;
        readonly List<ShopCard> _cards = new List<ShopCard>();
        int _shopPageIdx;
        const int CardsPerPage = 8;
        List<ToolDef> _shopTools;
        int _pendingBuy = -1;
        long _shopSig = -1;

        // cameras
        Camera _menuCam;
        GameObject _menuRig;
        Transform _menuLeft, _menuRight;
        Transform _ragdollFollow;
        Transform _flatCamParent; Vector3 _flatCamLocalPos; Quaternion _flatCamLocalRot;
        float _shake;
        Camera _shakeCam;
        Vector3 _shakeOffset;

        float _lobbyRefresh;
        bool _forceMobile;

        class HotbarSlot
        {
            public RectTransform rt; public Image orb, ringR, ringL, cool; public Text name, count, key, coolText; public Button btn; public int tool = -1;
        }

        class ShopCard
        {
            public RectTransform rt; public Text title, stats, desc, state; public Button buy; public ToolDef tool;
        }

        // ================================================================== lifecycle

        void Awake()
        {
            Instance = this;
            IsVR = XRSettings.isDeviceActive && System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-novr") < 0;
            _forceMobile = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-mobileui") >= 0;
            EnsureEventSystem();
            BuildCanvases();
            BuildMain(); BuildConnecting(); BuildLobby(); BuildHud(); BuildShop(); BuildEnd(); BuildSettings(); BuildPause(); BuildHotbarEdit();
            _toasts = AeroTheme.Rect(_menuCanvas.transform, "Toasts").Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(900, 400), new Vector2(0.5f, 1));
            var tv = _toasts.gameObject.AddComponent<VerticalLayoutGroup>(); tv.spacing = 8; tv.childAlignment = TextAnchor.UpperCenter;
            tv.childControlHeight = true; tv.childControlWidth = true; tv.childForceExpandHeight = false;
            EnsureMenuCamera();
        }

        void Start()
        {
            var nb = NetBootstrap.Instance;
            if (nb != null) nb.OnError += msg => Toast(msg, AeroPalette.Danger);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(go);
        }

        void BuildCanvases()
        {
            _menuCanvas = MakeCanvas("MenuCanvas", IsVR);
            _hudCanvas = MakeCanvas("HudCanvas", IsVR);
            if (IsVR)
            {
                _menuAnchor = _menuCanvas.transform;
                ((RectTransform)_menuCanvas.transform).sizeDelta = new Vector2(1600, 1000);
                _menuCanvas.transform.localScale = Vector3.one * 0.0008f;
                ((RectTransform)_hudCanvas.transform).sizeDelta = new Vector2(1100, 500);
                _hudCanvas.transform.localScale = Vector3.one * 0.00045f;
                _pointer = gameObject.AddComponent<VRPointer>();
                _pointer.canvas = _menuCanvas;
            }
            // HUD lives on the HUD canvas; for VR the toasts/menus live on the follow panel.
            _sky = AeroTheme.Image(_menuCanvas.transform, "Sky", AeroTheme.Sky, Color.white).rectTransform.Fill();
            _sky.gameObject.SetActive(!IsVR);
        }

        Canvas MakeCanvas(string name, bool world)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.layer = 5;
            DontDestroyOnLoad(go);
            var c = go.GetComponent<Canvas>();
            if (world)
            {
                c.renderMode = RenderMode.WorldSpace;
                go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2;
            }
            else
            {
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                var sc = go.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920, 1080);
                sc.matchWidthOrHeight = 0.5f;
            }
            c.sortingOrder = name == "MenuCanvas" ? 10 : 0;
            return c;
        }

        // ================================================================== screens

        RectTransform Screen_(string name, Vector2 size)
        {
            var p = AeroTheme.Panel(_menuCanvas.transform, name);
            p.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
            p.gameObject.SetActive(false);
            return p.rectTransform;
        }

        static RectTransform Column(RectTransform parent, float inset = 40, float spacing = 14)
        {
            var col = AeroTheme.VCol(parent, "Col", spacing);
            ((RectTransform)col.transform).Fill(inset);
            col.childAlignment = TextAnchor.UpperCenter;
            return (RectTransform)col.transform;
        }

        static Text Title(Transform parent, string text, int size = 64)
        {
            var t = AeroTheme.Label(parent, text, size, TextAnchor.MiddleCenter, Color.white, false);
            t.fontStyle = FontStyle.Bold;
            var o = t.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.05f, 0.35f, 0.7f, 0.9f); o.effectDistance = new Vector2(3, -3);
            var s = t.gameObject.AddComponent<Shadow>(); s.effectColor = new Color(0.3f, 0.9f, 1f, 0.6f); s.effectDistance = new Vector2(0, 5);
            t.Size(size * 1.4f);
            return t;
        }

        void BuildMain()
        {
            _main = Screen_("Main", new Vector2(980, 980));
            var col = Column(_main);
            Title(col, "CUBESTRUCTION", 76);
            AeroTheme.Label(col, "Climb it. Mine it. Blow it up. Clear every last pixel.", 24).Size(36);

            var nameRow = AeroTheme.HRow(col, "NameRow"); nameRow.Size(64);
            AeroTheme.Label(nameRow.transform, "Your Buddy", 26).Size(-1, 200, 0);
            _nameField = AeroTheme.InputField(nameRow.transform, GameSettings.PlayerName, "Name");
            _nameField.characterLimit = 16;
            _nameField.onEndEdit.AddListener(v => { if (!string.IsNullOrWhiteSpace(v)) GameSettings.PlayerName = v.Trim(); });

            var colorRow = AeroTheme.HRow(col, "Colors", 12); colorRow.Size(64);
            var colors = AeroPalette.BuddyColors;
            _colorSwatches = new Image[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                int idx = i;
                var img = AeroTheme.Image(colorRow.transform, "Swatch" + i, AeroTheme.Orb, colors[i]);
                var b = img.gameObject.AddComponent<Button>();
                AeroTheme.PressFeedback(b);
                b.onClick.AddListener(() => { GameSettings.ColorIndex = idx; RefreshSwatches(); });
                _colorSwatches[i] = img;
            }
            RefreshSwatches();

            AeroTheme.Button(col, "Host Online Game", () => NetBootstrap.Instance?.HostOnline(), AeroPalette.Lime, 30).Size(78);
            var joinRow = AeroTheme.HRow(col, "JoinRow"); joinRow.Size(70);
            _codeField = AeroTheme.InputField(joinRow.transform, "", "Join code");
            _codeField.characterLimit = 10;
            AeroTheme.Button(joinRow.transform, "Join", () => NetBootstrap.Instance?.JoinOnline(_codeField.text), AeroPalette.Aqua).Size(-1, 200, 0);

            var lanRow = AeroTheme.HRow(col, "LanRow"); lanRow.Size(60);
            AeroTheme.Button(lanRow.transform, "Host LAN", () => NetBootstrap.Instance?.HostLan(0), AeroPalette.SkyDeep, 22);
            _ipField = AeroTheme.InputField(lanRow.transform, PlayerPrefs.GetString("lanip", ""), "LAN IP");
            AeroTheme.Button(lanRow.transform, "Join LAN", () =>
            {
                PlayerPrefs.SetString("lanip", _ipField.text);
                NetBootstrap.Instance?.JoinLan(_ipField.text, 0);
            }, AeroPalette.SkyDeep, 22);

            BuildSoloRow(col);
            var bottom = AeroTheme.HRow(col, "Bottom"); bottom.Size(60);
            AeroTheme.Button(bottom.transform, "Settings", () => _settingsOpen = true, AeroPalette.SkyDeep);
            AeroTheme.Button(bottom.transform, "Quit", Application.Quit, AeroPalette.Danger);
            _status = AeroTheme.Label(col, "", 22, TextAnchor.MiddleCenter, AeroPalette.SkyDeep); _status.Size(60);
        }

        void RefreshSwatches()
        {
            int sel = GameSettings.ColorIndex;
            for (int i = 0; i < _colorSwatches.Length; i++)
                _colorSwatches[i].rectTransform.localScale = Vector3.one * (i == sel ? 1.2f : 0.85f);
            var me = PlayerAvatar.Local;
            if (me != null && me.IsSpawned) me.colorIndex.Value = (byte)sel;
        }

        void BuildConnecting()
        {
            _connecting = Screen_("Connecting", new Vector2(700, 260));
            var col = Column(_connecting);
            _connectingText = AeroTheme.Label(col, "Connecting…", 32); _connectingText.Size(80);
            AeroTheme.Button(col, "Cancel", () => NetBootstrap.Instance?.Leave(), AeroPalette.Danger).Size(64);
        }

        void BuildLobby()
        {
            _lobby = Screen_("Lobby", new Vector2(1160, 1050));
            var col = Column(_lobby);
            Title(col, "Lobby", 56);
            _lobbyCode = AeroTheme.Label(col, "", 44, TextAnchor.MiddleCenter, AeroPalette.SkyDeep); _lobbyCode.fontStyle = FontStyle.Bold; _lobbyCode.Size(64);
            AeroTheme.Label(col, "Share the code — friends on Quest, PC, Mac and mobile can all join.", 20).Size(30);
            var playersBg = AeroTheme.Image(col, "Players", AeroTheme.BarBg, new Color(1, 1, 1, 0.6f)); playersBg.Size(160);
            _lobbyPlayers = AeroTheme.Label(playersBg.transform, "", 26, TextAnchor.UpperLeft, AeroPalette.Ink, false);
            _lobbyPlayers.rectTransform.Fill(18); _lobbyPlayers.supportRichText = true;

            var modeRow = AeroTheme.HRow(col, "Modes"); modeRow.Size(70);
            _modeMining = AeroTheme.Button(modeRow.transform, "Mining", () => _lobbyMode = GameMode.Mining, AeroPalette.Lime);
            _modeSiege = AeroTheme.Button(modeRow.transform, "God Siege", () => _lobbyMode = GameMode.GodSiege, AeroPalette.SkyDeep);
            _modeTitan = AeroTheme.Button(modeRow.transform, "Titan Siege", () => _lobbyMode = GameMode.TitanSiege, AeroPalette.Danger);
            _modeSuper = AeroTheme.Button(modeRow.transform, "SUPER!", () => _lobbyMode = GameMode.Super, new Color(1f, 0.5f, 0.15f), 26);
            _modeSandbox = AeroTheme.Button(modeRow.transform, "Sandbox", () => _lobbyMode = GameMode.Sandbox, new Color(0.65f, 0.5f, 1f));
            var vote = AeroTheme.HRow(col, "Vote"); vote.Size(60);
            _voteRow = (RectTransform)vote.transform;
            AeroTheme.Label(vote.transform, "God Siege, I'll play as:", 22);
            _voteRaider = AeroTheme.Button(vote.transform, "Raider", () => { if (PlayerAvatar.Local) PlayerAvatar.Local.siegeVote.Value = 0; }, AeroPalette.Lime, 22);
            _voteCaster = AeroTheme.Button(vote.transform, "Minion Caster", () => { if (PlayerAvatar.Local) PlayerAvatar.Local.siegeVote.Value = 1; }, AeroPalette.Gold, 22);
            BuildFighterLobbyRows(col);
            _lobbyHint = AeroTheme.Label(col, "", 19); _lobbyHint.Size(58);

            var row = AeroTheme.HRow(col, "Actions"); row.Size(76);
            _startBtn = AeroTheme.Button(row.transform, "Start!", () => MatchManager.Instance?.RequestStartRpc(_lobbyMode), AeroPalette.Lime, 32);
            _lobbyLoadBtn = AeroTheme.Button(row.transform, "Load save", LoadFromLobby, AeroPalette.Gold);
            AeroTheme.Button(row.transform, "Settings", () => _settingsOpen = true, AeroPalette.SkyDeep);
            AeroTheme.Button(row.transform, "Leave", () => NetBootstrap.Instance?.Leave(), AeroPalette.Danger);
        }

        void BuildEnd()
        {
            _end = Screen_("End", new Vector2(1000, 760));
            var col = Column(_end);
            Title(col, "Match Over!", 60);
            _endTitle = AeroTheme.Label(col, "", 28, TextAnchor.MiddleCenter, AeroPalette.SkyDeep); _endTitle.Size(90);
            var bg = AeroTheme.Image(col, "Stats", AeroTheme.BarBg, new Color(1, 1, 1, 0.6f)); bg.Size(330);
            _endStats = AeroTheme.Label(bg.transform, "", 26, TextAnchor.UpperLeft, AeroPalette.Ink, false);
            _endStats.rectTransform.Fill(18); _endStats.supportRichText = true;
            var row = AeroTheme.HRow(col, "Actions"); row.Size(72);
            _endLobbyBtn = AeroTheme.Button(row.transform, "Back to lobby", () => MatchManager.Instance?.RequestLobbyRpc(), AeroPalette.Lime);
            AeroTheme.Button(row.transform, "Leave", () => NetBootstrap.Instance?.Leave(), AeroPalette.Danger);
        }

        void BuildSettings()
        {
            _settings = Screen_("Settings", new Vector2(900, IsVR ? 980 : 900));
            var col = Column(_settings, 40, 10);
            Title(col, "Settings", 52);
            AeroTheme.Cycle(col, "VR movement", new[] { "Smooth", "Teleport", "Arm swing" }, () => (int)GameSettings.Locomotion, v => GameSettings.Locomotion = (VRLocomotion)v).Size(58);
            AeroTheme.Cycle(col, "VR turning", new[] { "Snap", "Smooth" }, () => (int)GameSettings.Turn, v => GameSettings.Turn = (VRTurn)v).Size(58);
            var snaps = new[] { 30f, 45f, 60f, 90f };
            AeroTheme.Cycle(col, "Snap angle", snaps.Select(s => s + "°").ToArray(), () => Mathf.Max(0, System.Array.IndexOf(snaps, GameSettings.SnapAngle)), v => GameSettings.SnapAngle = snaps[v]).Size(58);
            AeroTheme.Cycle(col, "Comfort vignette", new[] { "Off", "On" }, () => GameSettings.ComfortVignette ? 1 : 0, v => GameSettings.ComfortVignette = v == 1).Size(58);
            AeroTheme.Cycle(col, "Handedness", new[] { "Right", "Left" }, () => GameSettings.LeftHanded ? 1 : 0, v => GameSettings.LeftHanded = v == 1).Size(58);
            AeroTheme.Cycle(col, "Explosion ragdoll", new[] { "Off", "On" }, () => GameSettings.AllowRagdoll ? 1 : 0, v => GameSettings.AllowRagdoll = v == 1).Size(58);
            var sens = new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
            AeroTheme.Cycle(col, "Look sensitivity", sens.Select(s => s + "x").ToArray(), () => Nearest(sens, GameSettings.LookSensitivity), v => GameSettings.LookSensitivity = sens[v]).Size(58);
            var heights = new[] { -0.3f, -0.2f, -0.1f, 0f, 0.1f, 0.2f, 0.3f };
            AeroTheme.Cycle(col, "VR height offset", heights.Select(h => (h >= 0 ? "+" : "") + h.ToString("0.0") + " m").ToArray(), () => Nearest(heights, GameSettings.HeightOffset), v => GameSettings.HeightOffset = heights[v]).Size(58);
            if (IsVR) AeroTheme.Button(col, "Recenter view", XRTracking.RequestRecenter, AeroPalette.SkyDeep).Size(58);
            AeroTheme.Label(col, "Movement/handedness changes apply next time you spawn." + (IsVR ? " Holding the Meta button also recenters." : ""), 18).Size(28);
            AeroTheme.Button(col, "Done", () => { PlayerPrefs.Save(); _settingsOpen = false; }, AeroPalette.Lime).Size(64);
        }

        static int Nearest(float[] arr, float v)
        {
            int best = 0;
            for (int i = 1; i < arr.Length; i++) if (Mathf.Abs(arr[i] - v) < Mathf.Abs(arr[best] - v)) best = i;
            return best;
        }

        void BuildPause()
        {
            _pause = Screen_("Pause", new Vector2(640, 900));
            var col = Column(_pause, 36, 12);
            Title(col, "Paused", 52);
            AeroTheme.Button(col, "Resume", () => _pauseOpen = false, AeroPalette.Lime).Size(66);
            _pauseSave = (RectTransform)AeroTheme.Button(col, "Save game", () =>
            {
                var err = MatchManager.Instance != null ? MatchManager.Instance.ServerSave() : "Not in a game.";
                if (err == null) Toast("Game saved! Continue it any time from the main menu.", AeroPalette.Grass);
                else Toast(err, AeroPalette.Danger);
            }, AeroPalette.Gold).Size(66).transform;
            _pauseFighter = (RectTransform)AeroTheme.Cycle(col, "Fighter", Super.Roster.All.Select(f => $"{f.name} \"{f.title}\"").ToArray(),
                () => Mathf.Clamp(GameSettings.Fighter, 0, Super.Roster.All.Length - 1), PickFighter).Size(66).transform;
            _pauseHotbar = (RectTransform)AeroTheme.Button(col, "Edit hotbar", () => { _hotbarEditOpen = true; _pickSig = -1; }, AeroPalette.Aqua).Size(66).transform;
            AeroTheme.Button(col, "Settings", () => _settingsOpen = true, AeroPalette.SkyDeep).Size(66);
            _pauseLobby = (RectTransform)AeroTheme.Button(col, "Back to lobby", () => { _pauseOpen = false; MatchManager.Instance?.RequestLobbyRpc(); }, AeroPalette.SkyDeep).Size(66).transform;
            AeroTheme.Button(col, "Main menu", () => { _pauseOpen = false; NetBootstrap.Instance?.Leave(); }, AeroPalette.Danger).Size(66);
            _pauseNote = AeroTheme.Label(col, "", 18, TextAnchor.MiddleCenter, AeroPalette.Ink, false); _pauseNote.Size(48);
        }

        void UpdatePauseMenu(NetworkManager nm, MatchManager mm, PlayerAvatar me, bool miner)
        {
            bool host = nm != null && nm.IsServer;
            bool savable = mm != null && SaveGame.CanSave(mm.mode.Value);
            _pauseSave.gameObject.SetActive(host && savable);
            _pauseLobby.gameObject.SetActive(host);
            _pauseHotbar.gameObject.SetActive(miner);
            _pauseFighter.gameObject.SetActive(mm != null && mm.mode.Value == GameMode.Sandbox);
            bool solo = nm != null && nm.IsServer && nm.ConnectedClientsIds.Count <= 1;
            _pauseNote.text = !host ? "Only the host can save or end the match." : !savable ? "God Siege and Titan Siege can't be saved." :
                              solo ? "Game paused." + (SaveGame.Exists ? "  Saving replaces your last save." : "") : "Others keep playing while you're in this menu.";
        }

        // ------------------------------------------------------------------ HUD + hotbar

        void BuildHud()
        {
            _hud = AeroTheme.Rect(_hudCanvas.transform, "HUD").Fill();
            _hud.gameObject.SetActive(false);

            if (!IsVR)
            {
                // money pill top-left
                var money = AeroTheme.Image(_hud, "Money", AeroTheme.Pill, AeroPalette.Gold);
                money.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -30), new Vector2(300, 72), new Vector2(0, 1));
                _moneyText = AeroTheme.Label(money.transform, "$0", 36, TextAnchor.MiddleCenter, Color.white, false);
                _moneyText.rectTransform.Fill(); _moneyText.fontStyle = FontStyle.Bold;
                _moneyText.gameObject.AddComponent<Outline>().effectColor = new Color(0.55f, 0.35f, 0f, 0.7f);

                // voxels bar + timer top-centre
                var top = AeroTheme.Panel(_hud, "Top");
                top.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(620, 92), new Vector2(0.5f, 1));
                _voxelBar = AeroTheme.Bar(top.transform, "Voxels", AeroPalette.Lime);
                ((RectTransform)_voxelBar.transform.parent).Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 16), new Vector2(-40, 28), new Vector2(0.5f, 0));
                _voxelText = AeroTheme.Label(top.transform, "", 22);
                _voxelText.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -6), new Vector2(-40, 36), new Vector2(0.5f, 1));
                _timerText = AeroTheme.Label(_hud, "0:00", 34, TextAnchor.MiddleRight, Color.white);
                _timerText.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(200, 60), new Vector2(1, 1));
                _timerText.gameObject.AddComponent<Outline>().effectColor = new Color(0.05f, 0.3f, 0.6f, 0.8f);

                // crosshair
                var cross = AeroTheme.Image(_hud, "Crosshair", AeroTheme.Orb, new Color(1, 1, 1, 0.85f));
                cross.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(14, 14));
                cross.raycastTarget = false;

                // Aero hotbar along the bottom
                var bar = AeroTheme.Panel(_hud, "HotbarBack");
                bar.color = new Color(1, 1, 1, 0.55f);
                bar.raycastTarget = false;   // only the slot orbs take taps/clicks
                bar.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(1000, 150), new Vector2(0.5f, 0));
                _hotbarBack = bar.rectTransform;
                _hotbarRow = AeroTheme.Rect(bar.transform, "Row").Fill(10);
                _toolInfo = AeroTheme.Label(_hud, "", 22, TextAnchor.MiddleCenter, Color.white);
                _toolInfo.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, 40), new Vector2(900, 34), new Vector2(0.5f, 0));
                _toolInfo.transform.SetParent(bar.transform, false);
                _toolInfo.gameObject.AddComponent<Outline>().effectColor = new Color(0.05f, 0.3f, 0.6f, 0.8f);
            }
            else
            {
                // VR: one compact strip — money/timer/voxels above a row of orbs
                var back = AeroTheme.Panel(_hud, "Strip"); back.color = new Color(1, 1, 1, 0.6f);
                back.rectTransform.Fill();
                var info = AeroTheme.HRow(back.transform, "Info");
                ((RectTransform)info.transform).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -10), new Vector2(-40, 70), new Vector2(0.5f, 1));
                _moneyText = AeroTheme.Label(info.transform, "$0", 40, TextAnchor.MiddleLeft, AeroPalette.Gold); _moneyText.fontStyle = FontStyle.Bold;
                _voxelText = AeroTheme.Label(info.transform, "", 30);
                _timerText = AeroTheme.Label(info.transform, "0:00", 36, TextAnchor.MiddleRight);
                _voxelBar = AeroTheme.Bar(back.transform, "Voxels", AeroPalette.Lime);
                ((RectTransform)_voxelBar.transform.parent).Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -84), new Vector2(-60, 20), new Vector2(0.5f, 1));
                _hotbarRow = AeroTheme.Rect(back.transform, "Row").Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 14), new Vector2(-30, 280), new Vector2(0.5f, 0));
                _toolInfo = AeroTheme.Label(back.transform, "", 24);
                _toolInfo.rectTransform.Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 300), new Vector2(-40, 30), new Vector2(0.5f, 0));
            }
            // 8 per row; past 8 the rows stack upward (the panel grows with them)
            var row = _hotbarRow.gameObject.AddComponent<GridLayoutGroup>();
            row.cellSize = new Vector2(110, 130); row.spacing = new Vector2(10, 8);
            row.constraint = GridLayoutGroup.Constraint.FixedColumnCount; row.constraintCount = GameSettings.HotbarRow;
            row.startCorner = GridLayoutGroup.Corner.LowerLeft; row.childAlignment = TextAnchor.LowerCenter;

            bool mobile = !IsVR && (Application.isMobilePlatform || _forceMobile);
            BuildFightHud();
            if (mobile)
            {
                // on the HUD canvas, under both HUDs, so tapping a hotbar slot still switches weapons
                _mobile = MobileControls.Create(_hudCanvas.transform);
                _mobile.transform.SetAsFirstSibling();
            }
        }

        HotbarSlot GetSlot(int i)
        {
            while (_slots.Count <= i)
            {
                var s = new HotbarSlot();
                int idx = _slots.Count;
                s.rt = AeroTheme.Rect(_hotbarRow, "Slot" + idx);
                s.rt.sizeDelta = new Vector2(110, 130);
                s.ringR = AeroTheme.Image(s.rt, "RingR", AeroTheme.Circle, new Color(0.4f, 1f, 1f, 0.9f));
                s.ringR.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, 4), new Vector2(104, 104), new Vector2(0.5f, 1));
                s.ringL = AeroTheme.Image(s.rt, "RingL", AeroTheme.Circle, new Color(0.7f, 1f, 0.35f, 0.9f));
                s.ringL.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, 4), new Vector2(104, 104), new Vector2(0.5f, 1));
                s.orb = AeroTheme.Image(s.rt, "Orb", AeroTheme.Orb, Color.white);
                s.orb.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(96, 96), new Vector2(0.5f, 1));
                s.btn = s.orb.gameObject.AddComponent<Button>();
                s.btn.onClick.AddListener(() => PlayerAvatar.Local?.Tools.SelectHotbarSlot(idx));
                AeroTheme.PressFeedback(s.btn);
                s.name = AeroTheme.Label(s.orb.transform, "", 17, TextAnchor.MiddleCenter, AeroPalette.Ink, false);
                s.name.rectTransform.Fill(6); s.name.fontStyle = FontStyle.Bold;
                s.count = AeroTheme.Label(s.rt, "", 20, TextAnchor.LowerRight, Color.white, false);
                s.count.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -66), new Vector2(60, 30), new Vector2(1, 1));
                s.count.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0.25f, 0.5f, 0.9f);
                // cooldown: a dark sweep over the orb plus seconds left
                s.cool = AeroTheme.Image(s.orb.transform, "Cooldown", AeroTheme.Orb, new Color(0.05f, 0.1f, 0.25f, 0.62f));
                s.cool.rectTransform.Fill(); s.cool.raycastTarget = false;
                s.cool.type = Image.Type.Filled; s.cool.fillMethod = Image.FillMethod.Radial360; s.cool.fillOrigin = 2; s.cool.fillClockwise = false; s.cool.fillAmount = 0;
                s.coolText = AeroTheme.Label(s.orb.transform, "", 30, TextAnchor.MiddleCenter, Color.white, false);
                s.coolText.rectTransform.Fill(); s.coolText.fontStyle = FontStyle.Bold; s.coolText.raycastTarget = false;
                s.coolText.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0.2f, 0.45f, 1f);
                s.key = AeroTheme.Label(s.rt, (idx + 1).ToString(), 18, TextAnchor.LowerCenter, AeroPalette.Ink, false);
                s.key.rectTransform.Place(new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, 28), new Vector2(0.5f, 0));
                _slots.Add(s);
            }
            return _slots[i];
        }

        /// <summary>Cheap signature so the hotbar only rebuilds when something visible changed.</summary>
        long HotbarSignature(PlayerAvatar me)
        {
            long sig = (long)me.ownedTools.Value * 31 + me.Tools.leftTool.Value * 1009 + me.Tools.rightTool.Value * 7;
            for (int i = 0; i < me.charges.Count; i++) sig = sig * 17 + me.charges[i];
            return sig;
        }

        void RefreshHotbar(PlayerAvatar me)
        {
            // cooldowns every frame (cheap): why you can't swing again yet
            foreach (var s in _slots)
            {
                if (!s.rt.gameObject.activeSelf || s.tool < 0) continue;
                float f = me.Tools.CooldownFrac(s.tool, out float left);
                s.cool.fillAmount = f;
                s.coolText.text = left >= 0.3f ? left.ToString(left < 1f ? "0.0" : "0") : "";
                s.name.enabled = f <= 0;
            }
            long sig = HotbarSignature(me) * 7 + GameSettings.HotbarPins.GetHashCode();
            if (sig == _hotbarSig) return;
            _hotbarSig = sig;
            var bar = me.Tools.Hotbar();
            int l = me.Tools.leftTool.Value, r = me.Tools.rightTool.Value;
            for (int i = 0; i < Mathf.Max(bar.Count, _slots.Count); i++)
            {
                if (i >= bar.Count) { _slots[i].rt.gameObject.SetActive(false); continue; }
                var s = GetSlot(i); var t = bar[i];
                s.rt.gameObject.SetActive(true);
                s.tool = t.index;
                s.name.text = ShortName(t.name);
                s.count.text = t.consumable ? "x" + me.Charges(t.index) : "";
                bool selR = t.index == r, selL = IsVR && t.index == l;
                s.ringR.enabled = selR; s.ringL.enabled = selL;
                s.orb.color = t.twoHanded ? new Color(1f, 0.92f, 0.75f) : Color.white;
                s.rt.localScale = Vector3.one * (selR || selL ? 1.12f : 0.94f);
                s.key.text = IsVR ? (selL && selR ? "L+R" : selL ? "L" : selR ? "R" : "") : (i < 9 ? (i + 1).ToString() : "");
            }
            int rows = Mathf.Max(1, Mathf.CeilToInt(bar.Count / (float)GameSettings.HotbarRow));
            if (_hotbarBack) _hotbarBack.sizeDelta = new Vector2(_hotbarBack.sizeDelta.x, 20 + rows * 138);
            var eq = me.Tools.Equipped(false);
            _toolInfo.text = eq == null ? "" : $"{eq.name}  ·  {eq.desc}";
        }

        static string ShortName(string n)
        {
            var parts = n.Split(' ');
            return parts.Length >= 2 ? parts[0] + "\n" + parts[1] : n;
        }

        // ------------------------------------------------------------------ shop

        void BuildShop()
        {
            _shop = Screen_("Shop", new Vector2(1500, 920));
            var header = AeroTheme.Rect(_shop, "Header").Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -20), new Vector2(-60, 80), new Vector2(0.5f, 1));
            var title = Title(header, "Tool Shop", 52); title.alignment = TextAnchor.MiddleLeft; title.rectTransform.Fill();
            _shopMoney = AeroTheme.Label(header, "$0", 40, TextAnchor.MiddleRight, AeroPalette.Gold); _shopMoney.rectTransform.Fill(); _shopMoney.fontStyle = FontStyle.Bold;

            var gridRt = AeroTheme.Rect(_shop, "Grid").Place(new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -10), new Vector2(-60, -230));
            var grid = gridRt.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(340, 310); grid.spacing = new Vector2(14, 14);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
            grid.childAlignment = TextAnchor.UpperCenter;

            _shopTools = Balance.Tools.Where(t => t.id != "Detonator" && t.id != "Pickaxe" && t.cat != ToolCategory.Hidden).OrderBy(t => t.price).ToList();
            for (int i = 0; i < CardsPerPage; i++) _cards.Add(MakeCard(gridRt));

            var footer = AeroTheme.HRow(_shop, "Footer");
            ((RectTransform)footer.transform).Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 24), new Vector2(-300, 70), new Vector2(0.5f, 0));
            AeroTheme.Button(footer.transform, "◀ Prev", () => { _shopPageIdx = Mathf.Max(0, _shopPageIdx - 1); _shopSig = -1; }, AeroPalette.SkyDeep);
            _shopPage = AeroTheme.Label(footer.transform, "", 26);
            AeroTheme.Button(footer.transform, "Next ▶", () => { _shopPageIdx = Mathf.Min(PageCount - 1, _shopPageIdx + 1); _shopSig = -1; }, AeroPalette.SkyDeep);
            AeroTheme.Button(footer.transform, "Hotbar", () => { _hotbarEditOpen = true; _pickSig = -1; }, AeroPalette.Aqua);
            AeroTheme.Button(footer.transform, "Close", () => _shopOpen = false, AeroPalette.Danger);
        }

        // ------------------------------------------------------------------ hotbar picker (pause menu / shop; works in VR)

        void BuildHotbarEdit()
        {
            _hotbarEdit = Screen_("HotbarEdit", new Vector2(1400, 900));
            var t = Title(_hotbarEdit, "Hotbar", 50);
            t.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -20), new Vector2(-60, 70), new Vector2(0.5f, 1));
            _pickInfo = AeroTheme.Label(_hotbarEdit, "", 22);
            _pickInfo.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -96), new Vector2(-60, 60), new Vector2(0.5f, 1));
            _pickGrid = AeroTheme.Rect(_hotbarEdit, "Grid").Place(new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -20), new Vector2(-60, -280));
            var grid = _pickGrid.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(250, 76); grid.spacing = new Vector2(12, 12);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 5;
            grid.childAlignment = TextAnchor.UpperCenter;
            var footer = AeroTheme.HRow(_hotbarEdit, "Footer");
            ((RectTransform)footer.transform).Place(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 24), new Vector2(-500, 70), new Vector2(0.5f, 0));
            AeroTheme.Button(footer.transform, "Clear picks", () => { GameSettings.HotbarPins = ""; _pickSig = -1; }, AeroPalette.SkyDeep);
            AeroTheme.Button(footer.transform, "Done", () => _hotbarEditOpen = false, AeroPalette.Lime);
            _hotbarEdit.gameObject.SetActive(false);
        }

        void RefreshHotbarEdit(PlayerAvatar me)
        {
            var usable = me.Tools.Usable();
            var pins = new HashSet<string>(GameSettings.HotbarPins.Split(',').Where(x => x.Length > 0));
            long sig = HotbarSignature(me) * 31 + GameSettings.HotbarPins.GetHashCode();
            if (sig == _pickSig) return;
            _pickSig = sig;
            var shown = new HashSet<int>(me.Tools.Hotbar().Select(x => x.index));
            _pickInfo.text = usable.Count > GameSettings.HotbarMax
                ? $"You have {usable.Count} tools; the hotbar holds {GameSettings.HotbarMax} (two rows of {GameSettings.HotbarRow}). Tap to pin the ones you want ({pins.Count} pinned)."
                : $"All {usable.Count} of your tools fit on the hotbar. Pins matter once you own more than {GameSettings.HotbarMax}.";
            while (_pickButtons.Count < usable.Count) _pickButtons.Add(AeroTheme.Button(_pickGrid, "", null, AeroPalette.SkyDeep, 20));
            for (int i = 0; i < _pickButtons.Count; i++)
            {
                var b = _pickButtons[i];
                b.gameObject.SetActive(i < usable.Count);
                if (i >= usable.Count) continue;
                var tool = usable[i];
                bool pinned = pins.Contains(tool.id), onBar = shown.Contains(tool.index);
                b.SetLabel((pinned ? "★ " : onBar ? "✓ " : "") + tool.name);
                b.image.color = pinned ? AeroPalette.Gold : onBar ? AeroPalette.Lime : new Color(0.75f, 0.82f, 0.9f);
                b.onClick.RemoveAllListeners();
                b.onClick.AddListener(() =>
                {
                    var p = new List<string>(GameSettings.HotbarPins.Split(',').Where(x => x.Length > 0));
                    if (p.Contains(tool.id)) p.Remove(tool.id);
                    else if (p.Count < GameSettings.HotbarMax) p.Add(tool.id);
                    else { Toast($"The hotbar holds {GameSettings.HotbarMax}: unpin one first", AeroPalette.Danger); return; }
                    GameSettings.HotbarPins = string.Join(",", p);
                    _pickSig = -1;
                });
            }
        }

        int PageCount => Mathf.Max(1, Mathf.CeilToInt(_shopTools.Count / (float)CardsPerPage));

        ShopCard MakeCard(Transform parent)
        {
            var c = new ShopCard();
            var bg = AeroTheme.Image(parent, "Card", AeroTheme.PanelSprite, new Color(0.85f, 0.95f, 1f, 0.95f));
            c.rt = bg.rectTransform;
            c.title = AeroTheme.Label(bg.transform, "", 26, TextAnchor.UpperLeft); c.title.fontStyle = FontStyle.Bold;
            c.title.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -12), new Vector2(-30, 34), new Vector2(0.5f, 1));
            c.stats = AeroTheme.Label(bg.transform, "", 18, TextAnchor.UpperLeft, AeroPalette.SkyDeep);
            c.stats.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -50), new Vector2(-30, 80), new Vector2(0.5f, 1));
            c.desc = AeroTheme.Label(bg.transform, "", 17, TextAnchor.UpperLeft);
            c.desc.rectTransform.Place(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -134), new Vector2(-30, 70), new Vector2(0.5f, 1));
            c.state = AeroTheme.Label(bg.transform, "", 18, TextAnchor.MiddleLeft, AeroPalette.Grass);
            c.state.rectTransform.Place(new Vector2(0, 0), new Vector2(0.45f, 0), new Vector2(0, 18), new Vector2(0, 56), new Vector2(0, 0));
            c.state.rectTransform.offsetMin = new Vector2(16, 18);
            c.buy = AeroTheme.Button(bg.transform, "Buy", null, AeroPalette.Lime, 24);
            ((RectTransform)c.buy.transform).Place(new Vector2(1, 0), new Vector2(1, 0), new Vector2(-14, 14), new Vector2(170, 60), new Vector2(1, 0));
            c.buy.onClick.AddListener(() =>
            {
                var me = PlayerAvatar.Local;
                if (me == null || c.tool == null) return;
                if (me.money.Value < c.tool.price) { Toast("Not enough money", AeroPalette.Danger); return; }
                _pendingBuy = c.tool.index;
                me.BuyRpc(c.tool.index);
            });
            return c;
        }

        void RefreshShop(PlayerAvatar me)
        {
            long sig = HotbarSignature(me) * 13 + me.money.Value * 7 + _shopPageIdx;
            if (sig == _shopSig) return;
            _shopSig = sig;
            _shopMoney.text = AeroTheme.Money(me.money.Value);
            _shopPage.text = $"{_shopPageIdx + 1} / {PageCount}";
            for (int i = 0; i < _cards.Count; i++)
            {
                int ti = _shopPageIdx * CardsPerPage + i;
                var c = _cards[i];
                if (ti >= _shopTools.Count) { c.rt.gameObject.SetActive(false); continue; }
                var t = _shopTools[ti];
                c.tool = t;
                c.rt.gameObject.SetActive(true);
                c.title.text = t.name;
                c.stats.text = Stats(t);
                c.desc.text = t.desc;
                bool owned = !t.consumable && me.Owns(t.index);
                c.state.text = owned ? "Owned ✓" : t.consumable ? $"Have {me.Charges(t.index)}" : "";
                c.buy.interactable = !owned && me.money.Value >= t.price && Roles.CanShop(me.role.Value);
                c.buy.SetLabel(owned ? "Owned" : AeroTheme.Money(t.price) + (t.consumable ? $" ×{t.stack}" : ""));
            }
        }

        static string Stats(ToolDef t)
        {
            string hands = t.twoHanded ? "  ·  Two-handed" : "";
            switch (t.cat)
            {
                case ToolCategory.Movement: return $"Movement{hands}";
                case ToolCategory.Utility: return "Utility";
            }
            float rate = t.cooldown > 0 ? 1f / t.cooldown : 0;
            string dmg = t.pellets > 1 ? $"{t.damage:0}×{t.pellets}" : $"{t.damage:0}";
            string extra = t.pierceCells > 0 ? $"  ·  Pierce {t.pierceCells}" : t.fuse > 0 ? $"  ·  Fuse {t.fuse:0.#}s" : "";
            return $"{t.category}  ·  Tier {t.tier}{hands}\nDmg {dmg}  ·  Radius {t.radiusCells:0.#}  ·  {rate:0.#}/s{extra}";
        }

        // ================================================================== public API

        public void TogglePause()
        {
            if (_screen != Screen.Hud) return;
            _shopOpen = false;
            _pauseOpen = !_pauseOpen;
        }

        public void ToggleShop()
        {
            if (_screen != Screen.Hud) return;
            var me = PlayerAvatar.Local;
            if (me == null) return;
            if (!Roles.CanShop(me.role.Value)) { _pauseOpen = !_pauseOpen; return; }
            _shopOpen = !_shopOpen;
            _shopSig = -1;
            if (_shopOpen) _pauseOpen = false;
        }

        GameObject _bannerRoot; Text _bannerText; Button _bannerBtn;

        /// <summary>Killstreak status strip at the top of the HUD (null hides it). An optional button
        /// (e.g. "Done" to close the Raptor laptop).</summary>
        public void SetBanner(string text, string buttonLabel = null, UnityEngine.Events.UnityAction onButton = null)
        {
            if (_bannerRoot == null)
            {
                if (text == null || _hudCanvas == null) return;
                var bg = AeroTheme.Panel(_hudCanvas.transform, "StreakBanner");
                bg.color = new Color(0.05f, 0.12f, 0.3f, 0.88f);
                bg.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, IsVR ? -6 : -110), new Vector2(IsVR ? 1060 : 1000, 84), new Vector2(0.5f, 1));
                _bannerText = AeroTheme.Label(bg.transform, "", IsVR ? 30 : 28, TextAnchor.MiddleLeft, Color.white);
                _bannerText.rectTransform.Fill(24);
                _bannerBtn = AeroTheme.Button(bg.transform, "Done", null, AeroPalette.Danger, 24);
                ((RectTransform)_bannerBtn.transform).Place(new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-14, 0), new Vector2(150, 60), new Vector2(1, 0.5f));
                _bannerRoot = bg.gameObject;
            }
            _bannerRoot.SetActive(text != null);
            if (text == null) return;
            _bannerText.text = text;
            _bannerBtn.onClick.RemoveAllListeners();
            _bannerBtn.gameObject.SetActive(onButton != null);
            if (onButton != null) { _bannerBtn.SetLabel(buttonLabel ?? "Done"); _bannerBtn.onClick.AddListener(onButton); }
        }

        public void Toast(string msg) => Toast(msg, AeroPalette.SkyDeep);

        public void Toast(string msg, Color tint)
        {
            if (_toasts == null) return;
            var img = AeroTheme.Image(_toasts, "Toast", AeroTheme.Pill, tint);
            img.Size(64);
            var t = AeroTheme.Label(img.transform, msg, 26, TextAnchor.MiddleCenter, Color.white, false);
            t.rectTransform.Fill(10);
            t.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0.2f, 0.4f, 0.6f);
            Destroy(img.gameObject, 2.6f);
            while (_toasts.childCount > 4) DestroyImmediate(_toasts.GetChild(0).gameObject);
        }

        public void SetRagdollView(bool on, Transform follow)
        {
            var me = PlayerAvatar.Local;
            if (me == null) return;
            if (me.IsVR)
            {
                if (on) Toast("Whoa!", AeroPalette.Danger);
                return;
            }
            var rig = me.GetComponent<FlatRig>();
            var cam = rig != null ? rig.Cam : null;
            if (cam == null) return;
            if (on)
            {
                if (_ragdollFollow == null)
                {
                    _flatCamParent = cam.transform.parent;
                    _flatCamLocalPos = cam.transform.localPosition;
                    _flatCamLocalRot = cam.transform.localRotation;
                    cam.transform.SetParent(null, true);
                }
                _ragdollFollow = follow;
            }
            else if (_ragdollFollow != null || cam.transform.parent == null)
            {
                _ragdollFollow = null;
                if (_flatCamParent != null)
                {
                    cam.transform.SetParent(_flatCamParent, false);
                    cam.transform.localPosition = _flatCamLocalPos;
                    cam.transform.localRotation = _flatCamLocalRot;
                }
            }
        }

        public void Shake(float amount)
        {
            if (IsVR) return;   // never shake a VR camera
            var me = PlayerAvatar.Local;
            if (me != null && me.IsVR) return;
            _shake = Mathf.Max(_shake, Mathf.Clamp01(amount));
        }

        // ================================================================== update

        void LateUpdate() => UpdateVRPlacement(PlayerAvatar.Local);   // after the rig moved this frame

        void Update()
        {
            var nb = NetBootstrap.Instance;
            var nm = NetworkManager.Singleton;
            bool connected = nm != null && nm.IsListening && (nm.IsServer || nm.IsConnectedClient);
            var mm = MatchManager.Instance;
            var me = PlayerAvatar.Local;

            Screen want;
            if (!connected) want = (nb != null && nb.Busy) || (nm != null && nm.IsListening) ? Screen.Connecting : Screen.Main;
            else if (mm == null || !mm.IsSpawned || me == null || !me.IsSpawned) want = Screen.Connecting;
            else want = mm.state.Value == MatchState.Lobby ? Screen.Lobby : mm.state.Value == MatchState.Playing ? Screen.Hud : Screen.End;
            if (want != _screen) SetScreen(want);

            // flat: Escape toggles pause / closes popups
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            if (!IsVR && pad != null && pad.startButton.wasPressedThisFrame && _screen == Screen.Hud && !_settingsOpen && !_shopOpen) _pauseOpen = !_pauseOpen;
            if (!IsVR && kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                if (_settingsOpen) _settingsOpen = false;
                else if (_hotbarEditOpen) _hotbarEditOpen = false;
                else if (_shopOpen) _shopOpen = false;
                else if (_screen == Screen.Hud) _pauseOpen = !_pauseOpen;
            }

            bool miner = me != null && Roles.UsesTools(me.role.Value);
            bool fighter = me != null && me.role.Value == PlayerRole.Fighter;
            UpdatePendingSolo(nm, mm);
            if (me == null || !Roles.CanShop(me.role.Value)) _shopOpen = false;
            _settings.gameObject.SetActive(_settingsOpen);
            bool showBase = !_settingsOpen;
            _main.gameObject.SetActive(showBase && _screen == Screen.Main);
            _connecting.gameObject.SetActive(showBase && _screen == Screen.Connecting);
            _lobby.gameObject.SetActive(showBase && _screen == Screen.Lobby);
            _end.gameObject.SetActive(showBase && _screen == Screen.End);
            _shop.gameObject.SetActive(showBase && _screen == Screen.Hud && _shopOpen);
            if (_screen != Screen.Hud || me == null || !miner) _hotbarEditOpen = false;
            _hotbarEdit.gameObject.SetActive(showBase && _hotbarEditOpen);
            if (_hotbarEditOpen) { RefreshHotbarEdit(me); _shopOpen = false; _pauseOpen = false; }
            _pause.gameObject.SetActive(showBase && _screen == Screen.Hud && _pauseOpen && !_shopOpen);
            if (_pause.gameObject.activeSelf) UpdatePauseMenu(nm, mm, me, miner);
            _hud.gameObject.SetActive(_screen == Screen.Hud && miner);
            _fightHud.gameObject.SetActive(_screen == Screen.Hud && fighter);
            if (_mobile) _mobile.gameObject.SetActive(_screen == Screen.Hud && (miner || fighter) && !BlocksGameplayInput && !StreakState.HideTouchControls);
            // solo host: the pause menu really pauses
            bool solo = nm != null && nm.IsServer && nm.ConnectedClientsIds.Count <= 1;
            float wantScale = solo && _screen == Screen.Hud && (_pauseOpen || _settingsOpen) ? 0f : 1f;
            if (Time.timeScale != wantScale && (Time.timeScale == 0f || Time.timeScale == 1f)) Time.timeScale = wantScale;
            _sky.gameObject.SetActive(!IsVR && (_screen == Screen.Main || _screen == Screen.Connecting));

            switch (_screen)
            {
                case Screen.Main:
                    if (_status && nb != null) _status.text = nb.Status;
                    break;
                case Screen.Connecting:
                    _connectingText.text = nb != null && !string.IsNullOrEmpty(nb.Status) ? nb.Status : "Connecting…";
                    break;
                case Screen.Lobby: UpdateLobby(nm, mm); break;
                case Screen.End: UpdateEnd(nm, mm); break;
                case Screen.Hud:
                    if (miner) UpdateHud(me, mm);
                    if (fighter) UpdateFightHud(me);
                    if (_shopOpen && me != null) RefreshShop(me);
                    break;
            }

            if (_pendingBuy >= 0 && me != null)
            {
                var t = Balance.Tool(_pendingBuy);
                if (t != null && (me.Owns(t.index) && !t.consumable || t.consumable && me.Charges(t.index) > 0))
                {
                    Toast($"Bought {t.name}!", AeroPalette.Grass);
                    _pendingBuy = -1;
                }
            }

            UpdateCameras(me);
        }

        void SetScreen(Screen s)
        {
            _screen = s;
            if (s != Screen.Hud) Time.timeScale = 1f;
            if (s != Screen.Hud) { _shopOpen = false; _pauseOpen = false; }
            _hotbarSig = -1; _shopSig = -1; _lastMoney = int.MinValue; _lastTimer = -1; _lastAlive = -1;
            _lobbyRefresh = 0;
            if (s == Screen.Main) _ragdollFollow = null;
        }

        void UpdateLobby(NetworkManager nm, MatchManager mm)
        {
            bool host = nm.IsHost || nm.IsServer;
            _startBtn.gameObject.SetActive(host);
            _modeMining.interactable = host; _modeSiege.interactable = host; _modeTitan.interactable = host;
            _modeMining.transform.localScale = Vector3.one * (_lobbyMode == GameMode.Mining ? 1.06f : 0.94f);
            _modeSiege.transform.localScale = Vector3.one * (_lobbyMode == GameMode.GodSiege ? 1.06f : 0.94f);
            _modeTitan.transform.localScale = Vector3.one * (_lobbyMode == GameMode.TitanSiege ? 1.06f : 0.94f);
            UpdateFighterLobby(host);
            // non-VR players vote for their God Siege role (the host's mode pick is synced only on Start, so always show it)
            var meL = PlayerAvatar.Local;
            bool flat = meL != null && meL.platform.Value != Platform.VR;
            _voteRow.gameObject.SetActive(flat);
            if (flat)
            {
                _voteRaider.transform.localScale = Vector3.one * (meL.siegeVote.Value == 0 ? 1.06f : 0.9f);
                _voteCaster.transform.localScale = Vector3.one * (meL.siegeVote.Value == 1 ? 1.06f : 0.9f);
            }

            _lobbyRefresh -= Time.unscaledDeltaTime;
            if (_lobbyRefresh > 0) return;
            _lobbyRefresh = 0.5f;
            var nb = NetBootstrap.Instance;
            _lobbyCode.text = nb != null && !string.IsNullOrEmpty(nb.JoinCode) ? "Join code:  " + nb.JoinCode : "";
            var sb = new System.Text.StringBuilder();
            bool anyVR = false;
            foreach (var p in PlayerAvatar.All)
            {
                anyVR |= p.platform.Value == Platform.VR;
                string plat = p.platform.Value == Platform.VR ? "VR" : p.platform.Value == Platform.Mobile ? "Mobile" : "PC/Mac";
                string col = ColorUtility.ToHtmlStringRGB(p.Tint * 0.8f);
                string vote = p.platform.Value == Platform.VR ? "" : p.siegeVote.Value == 1 ? "   · wants Minion Caster" : "   · Raider";
                sb.Append($"<color=#{col}>●</color>  {p.displayName.Value}   <i>{plat}</i>{(p.OwnerClientId == NetworkManager.ServerClientId ? "   (host)" : "")}<size=18>{vote}</size>\n");
            }
            _lobbyPlayers.text = sb.ToString();
            _lobbyHint.text = Roles.IsFighterMode(_lobbyMode) ? FighterLobbyHint() : _lobbyMode == GameMode.Mining
                ? "Mining: everyone climbs and mines the cube together. The match ends when the last pixel is cleared."
                : _lobbyMode == GameMode.GodSiege
                ? (anyVR ? "God Siege: the VR player is a giant sun God defending the cube. Raiders dig at it on foot; one Minion Caster (picked from volunteers) sends unit waves. Break the Core to win."
                         : "God Siege needs a VR player as God — without one, the host plays God with mouse controls.")
                : (anyVR ? "Titan Siege: the VR player is a Titan smashing through three walls. Everyone else defends with cannons, artillery, rocket pods, RPGs, webs and jetpacks. Bots fill in so it's always at least 5 against 1."
                         : "Titan Siege: no headset here, so an AI Titan is coming. Defend the three walls with cannons, artillery, rocket pods, RPGs, webs and jetpacks.");
            if (!host) _lobbyHint.text += "\nWaiting for the host to start…";
        }

        void UpdateEnd(NetworkManager nm, MatchManager mm)
        {
            _endLobbyBtn.gameObject.SetActive(nm.IsHost || nm.IsServer);
            _lobbyRefresh -= Time.unscaledDeltaTime;
            if (_lobbyRefresh > 0) return;
            _lobbyRefresh = 1f;
            _endTitle.text = mm.resultText.Value.ToString();
            var superBoard = SuperEndScoreboard(mm);
            if (superBoard != null) { _endStats.supportRichText = true; _endStats.text = superBoard; return; }
            var tdEnd = Siege.TitanDirector.Instance;
            if (mm.mode.Value == GameMode.TitanSiege && tdEnd != null && tdEnd.scoreboard.Value.Length > 0)
            {
                _endStats.supportRichText = true;
                _endStats.text = tdEnd.scoreboard.Value.ToString();
                return;
            }
            var sb = new System.Text.StringBuilder();
            int rank = 1;
            foreach (var p in PlayerAvatar.All.OrderByDescending(p => p.voxels.Value).ThenByDescending(p => p.earned.Value))
            {
                string role = p.role.Value == PlayerRole.Miner ? "" : $" ({p.role.Value})";
                sb.Append($"{rank++}.  {p.displayName.Value}{role}   —   {p.voxels.Value:N0} voxels   ·   {AeroTheme.Money(p.earned.Value)} earned\n");
            }
            _endStats.text = sb.ToString();
        }

        void UpdateHud(PlayerAvatar me, MatchManager mm)
        {
            if (me.money.Value != _lastMoney)
            {
                _lastMoney = me.money.Value;
                _moneyText.text = AeroTheme.Money(_lastMoney);
            }
            int secs = (int)mm.elapsed.Value;
            if (secs != _lastTimer) { _lastTimer = secs; _timerText.text = MatchManager.FormatTime(secs); }
            var td = Siege.TitanDirector.Instance;
            if (td != null && td.IsActive)
            {
                // defenders: the Titan's health instead of voxels, plus what the turret you're at is doing
                float max = Mathf.Max(1f, td.titanMaxHp.Value);
                AeroTheme.SetBar(_voxelBar, td.titanHp.Value / max);
                string phase = td.prep.Value ? $"Dig ammo and load the turrets! {Mathf.CeilToInt(td.prepLeft.Value)}s" :
                               !td.transformed.Value ? "The Titan is about to transform..." :
                               $"Wall {td.wall.Value + 1} of 3" + (td.rage.Value == 2 ? "  ·  BERSERK" : td.rage.Value == 1 ? "  ·  ENRAGED" : "");
                string turret = Siege.DefenderController.TurretHint();
                string carry = $"Carrying: {me.ammoBalls.Value} cannonballs · {me.ammoRockets.Value} rockets";
                _voxelText.verticalOverflow = VerticalWrapMode.Overflow;
                _voxelText.text = $"Titan {td.titanHp.Value:0} HP  ·  {phase}\n{carry}" + (turret != null ? $"\n{turret}" : "");
                _lastAlive = -1;
                RefreshHotbar(me);
                return;
            }
            var cube = VoxelCube.Instance;
            if (cube != null && cube.IsSpawned && cube.alive.Value != _lastAlive)
            {
                _lastAlive = cube.alive.Value;
                float frac = _lastAlive / (float)Mathf.Max(1, cube.Total);
                AeroTheme.SetBar(_voxelBar, frac);
                _voxelText.text = $"{_lastAlive:N0} voxels left  ({frac * 100f:0.0}%)";
            }
            RefreshHotbar(me);
        }

        // ================================================================== cameras / VR placement

        void EnsureMenuCamera()
        {
            if (IsVR)
            {
                // The flat menu's orbiting lobby camera isn't head-tracked; VR always gets its own rig
                // (tracked head + laser-pointer hands), whatever camera the scene woke up first.
                foreach (var lc in FindObjectsByType<Cubestruction.LobbyCamera>(FindObjectsInactive.Include))
                    lc.gameObject.SetActive(false);
                XRTracking.Recentered += () => { _menuRecenter = true; _wasMenuVisible = false; };
            }
            else if (Camera.main != null) { _menuCam = Camera.main; _menuRig = _menuCam.gameObject; return; }
            _menuRig = new GameObject("MenuRig");
            _menuRig.transform.position = MenuRigSpot;
            var camGo = new GameObject("MenuCamera");
            camGo.transform.SetParent(_menuRig.transform, false);
            _menuCam = camGo.AddComponent<Camera>();
            _menuCam.tag = "MainCamera";
            _menuCam.nearClipPlane = 0.03f;
            camGo.AddComponent<AudioListener>();
            if (IsVR)
            {
                AddPose(camGo.transform, "<XRHMD>/centerEyePosition", "<XRHMD>/centerEyeRotation");
                _menuLeft = new GameObject("MenuLeftHand").transform; _menuLeft.SetParent(_menuRig.transform, false);
                _menuRight = new GameObject("MenuRightHand").transform; _menuRight.SetParent(_menuRig.transform, false);
                AddPose(_menuLeft, "<XRController>{LeftHand}/devicePosition", "<XRController>{LeftHand}/deviceRotation");
                AddPose(_menuRight, "<XRController>{RightHand}/devicePosition", "<XRController>{RightHand}/deviceRotation");
                _menuRig.transform.rotation = Quaternion.LookRotation(-_menuRig.transform.position.normalized.WithY0());
            }
        }

        static void AddPose(Transform t, string pos, string rot)
        {
            var tpd = t.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction(binding: pos));
            tpd.rotationInput = new InputActionProperty(new InputAction(binding: rot));
            tpd.positionInput.action.Enable(); tpd.rotationInput.action.Enable();
        }

        void UpdateCameras(PlayerAvatar me)
        {
            bool playerCam = me != null && me.IsSpawned;
            if (_menuRig != null && _menuRig.activeSelf == playerCam) _menuRig.SetActive(!playerCam);
            if (!playerCam && _menuCam != null && !IsVR)
            {
                // slow orbit around the arena for the menu background
                float a = Time.unscaledTime * 0.08f;
                _menuCam.transform.position = new Vector3(Mathf.Sin(a) * 32f, 14f, Mathf.Cos(a) * 32f);
                _menuCam.transform.LookAt(new Vector3(0, 8f, 0));
            }

            Camera flatCam = null;
            if (me != null && !me.IsVR) { var rig = me.GetComponent<FlatRig>(); if (rig != null && rig.Enabled()) flatCam = rig.Cam; }

            // ragdoll chase cam
            if (_ragdollFollow != null && flatCam != null)
            {
                var target = _ragdollFollow.position;
                var back = Vector3.ProjectOnPlane(flatCam.transform.position - target, Vector3.up);
                if (back.sqrMagnitude < 0.5f) back = -me.transform.forward;
                var want = target + back.normalized * 3.2f + Vector3.up * 1.6f;
                flatCam.transform.position = Vector3.Lerp(flatCam.transform.position, want, Time.deltaTime * 6f);
                flatCam.transform.rotation = Quaternion.Slerp(flatCam.transform.rotation, Quaternion.LookRotation(target - flatCam.transform.position), Time.deltaTime * 8f);
            }

            // shake (flat only): offset applied on top of whatever the camera is doing
            if (_shakeCam != null) _shakeCam.transform.position -= _shakeOffset;
            _shakeOffset = Vector3.zero; _shakeCam = null;
            if (_shake > 0.001f && flatCam != null)
            {
                _shakeOffset = Random.insideUnitSphere * _shake * 0.12f;
                flatCam.transform.position += _shakeOffset;
                _shakeCam = flatCam;
                _shake = Mathf.MoveTowards(_shake, 0, Time.deltaTime * 2.5f);
            }
        }

        bool _menuRecenter = true;
        static readonly Vector3 MenuRigSpot = new Vector3(0, 0, -20f);   // in front of the 17 m cube

        void UpdateVRPlacement(PlayerAvatar me)
        {
            if (!IsVR) return;
            XRTracking.Tick();
            if (_menuRecenter && _menuRig != null && _menuRig.activeInHierarchy && XRTracking.HasPose(_menuCam.transform))
            {
                // face the arena from the menu spot, whichever way the player is physically looking
                float yaw = Quaternion.LookRotation(-MenuRigSpot.WithY0()).eulerAngles.y;
                XRTracking.AlignPose(_menuRig.transform, _menuCam.transform, MenuRigSpot, yaw, out var pos, out var rootYaw);
                _menuRig.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, rootYaw, 0));
                _menuRecenter = false;
                _wasMenuVisible = false;   // snap the panel in front
            }
            if (_menuRecenter && me != null && me.IsSpawned) _menuRecenter = false;   // the player rig handles it
            Transform head = null; float scale = 1f;
            if (me != null && me.IsSpawned) { var vr = me.GetComponent<VRRig>(); if (vr != null && vr.Cam != null) head = vr.Cam.transform; scale = me.transform.localScale.x; }
            else if (_menuCam != null) head = _menuCam.transform;
            if (head == null) return;

            // pointer hands: player hands when spawned, menu rig hands before that
            _pointer.leftHand = me != null && me.IsSpawned ? me.leftHand : _menuLeft;
            _pointer.rightHand = me != null && me.IsSpawned ? me.rightHand : _menuRight;

            // menu panel: lazy follow, ~1.2 m ahead at eye level
            bool menuVisible = _screen != Screen.Hud || _shopOpen || _settingsOpen || _pauseOpen || _hotbarEditOpen;
            _menuCanvas.gameObject.SetActive(menuVisible || _toasts.childCount > 0);
            _pointer.enabled = menuVisible;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var target = head.position + fwd * 1.2f * scale;
            var toPanel = Vector3.ProjectOnPlane(_menuAnchor.position - head.position, Vector3.up);
            bool far = Vector3.Angle(toPanel, fwd) > 35f || Mathf.Abs(toPanel.magnitude - 1.2f * scale) > 0.5f * scale || !_wasMenuVisible;
            if (far || _following)
            {
                _following = Vector3.Distance(_menuAnchor.position, target) > 0.05f * scale;
                _menuAnchor.position = far && !_wasMenuVisible ? target : Vector3.Lerp(_menuAnchor.position, target, Time.deltaTime * 4f);
            }
            _menuAnchor.rotation = Quaternion.LookRotation(_menuAnchor.position - head.position);
            _menuAnchor.localScale = Vector3.one * 0.0008f * scale;
            _wasMenuVisible = menuVisible;

            // hotbar strip: bottom of the view. Copies the head pose every frame instead of being
            // parented, so it survives the player's camera being destroyed on leave/despawn.
            var ht = _hudCanvas.transform;
            ht.SetPositionAndRotation(head.TransformPoint(new Vector3(0, -0.28f, 0.6f)), head.rotation * Quaternion.Euler(25f, 0, 0));
            ht.localScale = Vector3.one * 0.00045f * scale;
        }

        bool _following, _wasMenuVisible;
    }

    static class GameMenusExt
    {
        public static Vector3 WithY0(this Vector3 v) => new Vector3(v.x, 0, v.z);
        public static bool Enabled(this Behaviour b) => b != null && b.isActiveAndEnabled;
    }
}
