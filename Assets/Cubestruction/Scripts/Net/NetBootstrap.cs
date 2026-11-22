using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Cubestruction.Net
{
    /// <summary>
    /// Connects players across Quest / PC / Mac / Android / iOS.
    ///  - Online: Unity Multiplayer Services sessions over Relay (works through NAT, share a short join code).
    ///  - LAN:    direct UnityTransport (no Unity account needed; good for testing on one network).
    /// Scene object; uses NetworkManager.Singleton unless one is assigned.
    /// </summary>
    public class NetBootstrap : MonoBehaviour
    {
        public static NetBootstrap Instance { get; private set; }
        public NetworkManager networkManager;
        public int maxPlayers = 6;
        public ushort defaultPort = 7777;

        public string JoinCode { get; private set; } = "";
        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }
        public bool IsConnected => Net != null && (Net.IsClient || Net.IsServer) && Net.IsListening;

        public event Action<string> OnError;
        public event Action OnConnected;
        public event Action OnDisconnected;

        ISession _session;
        NetworkManager Net => networkManager != null ? networkManager : NetworkManager.Singleton;

        void Awake()
        {
            Instance = this;
            Application.runInBackground = true;
        }

        void Start()
        {
            if (Net == null) { Fail("No NetworkManager in scene."); return; }
            Net.NetworkConfig.ConnectionApproval = false;
            Net.OnClientConnectedCallback += id => { if (id == Net.LocalClientId) { SetStatus("Connected"); OnConnected?.Invoke(); } };
            Net.OnClientStopped += _ => HandleStopped();
            Net.OnTransportFailure += () => { Fail("Connection lost."); HandleStopped(); };
        }

        void SetStatus(string s) { Status = s; }
        void Fail(string msg) { Status = msg; Debug.LogWarning("[Net] " + msg); OnError?.Invoke(msg); }

        // ------------------------------------------------------------------ online (Relay)

        async Task<bool> EnsureServices()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    SetStatus("Starting services…");
                    await UnityServices.InitializeAsync();
                }
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    SetStatus("Signing in…");
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }
                return true;
            }
            catch (Exception e)
            {
                Fail("Online services unavailable (link the project to Unity Cloud, or use LAN): " + e.Message);
                return false;
            }
        }

        public async void HostOnline()
        {
            if (Busy || IsConnected) return;
            Busy = true;
            try
            {
                if (!await EnsureServices()) return;
                SetStatus("Creating session…");
                var options = new SessionOptions { MaxPlayers = maxPlayers, IsPrivate = false }.WithRelayNetwork();
                _session = await MultiplayerService.Instance.CreateSessionAsync(options);
                JoinCode = _session.Code;
                SetStatus("Hosting — code " + JoinCode);
            }
            catch (Exception e) { Fail("Could not host: " + e.Message); }
            finally { Busy = false; }
        }

        public async void JoinOnline(string code)
        {
            if (Busy || IsConnected) return;
            code = (code ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0) { Fail("Enter a join code."); return; }
            Busy = true;
            try
            {
                if (!await EnsureServices()) return;
                SetStatus("Joining " + code + "…");
                _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
                JoinCode = _session.Code;
                SetStatus("Joined");
            }
            catch (Exception e) { Fail("Could not join: " + e.Message); }
            finally { Busy = false; }
        }

        // ------------------------------------------------------------------ LAN / direct

        public void HostLan(ushort port = 0)
        {
            if (IsConnected) return;
            var utp = Net.GetComponent<UnityTransport>();
            utp.SetConnectionData("0.0.0.0", port == 0 ? defaultPort : port, "0.0.0.0");
            JoinCode = LocalIp();
            if (Net.StartHost()) SetStatus($"Hosting LAN on {JoinCode}:{(port == 0 ? defaultPort : port)}");
            else Fail("Could not start LAN host (port in use?).");
        }

        public void JoinLan(string ip, ushort port = 0)
        {
            if (IsConnected) return;
            ip = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
            var utp = Net.GetComponent<UnityTransport>();
            utp.SetConnectionData(ip, port == 0 ? defaultPort : port);
            JoinCode = ip;
            if (Net.StartClient()) SetStatus("Connecting to " + ip + "…");
            else Fail("Could not start client.");
        }

        static string LocalIp()
        {
            try
            {
                foreach (var a in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return a.ToString();
            }
            catch { /* ignored: some platforms disallow DNS lookups */ }
            return "127.0.0.1";
        }

        // ------------------------------------------------------------------ leave

        public async void Leave()
        {
            var s = _session; _session = null;
            if (s != null)
            {
                try { await s.LeaveAsync(); } catch (Exception e) { Debug.LogWarning("[Net] leave: " + e.Message); }
            }
            if (Net != null && Net.IsListening) Net.Shutdown();
            JoinCode = "";
            SetStatus("");
        }

        void HandleStopped()
        {
            _session = null;
            JoinCode = "";
            if (string.IsNullOrEmpty(Status) || Status.StartsWith("Connected") || Status.StartsWith("Hosting") || Status.StartsWith("Joined"))
                SetStatus("Disconnected");
            OnDisconnected?.Invoke();
        }

        void OnApplicationQuit() { if (Net != null && Net.IsListening) Net.Shutdown(); }
    }
}
