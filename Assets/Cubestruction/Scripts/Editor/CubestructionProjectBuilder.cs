#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cubestruction.Net;
using Cubestruction.Player;
using Cubestruction.Siege;
using Cubestruction.Tools;
using Cubestruction.UI;
using Cubestruction.Voxels;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cubestruction.EditorTools
{
    /// <summary>
    /// One-click project setup: Cubestruction ▸ Build Everything.
    /// Creates the URP pipeline asset, Aero materials, every network prefab, tool visual prefabs,
    /// the Cubestruction scene (NetworkManager, MatchManager, SiegeDirector, FX, pedestal, sky, bubbles)
    /// and Android/Quest player settings. Safe to re-run: it overwrites generated assets.
    /// </summary>
    public static class CubestructionProjectBuilder
    {
        const string Root = "Assets/Cubestruction";
        const string Gen = Root + "/Generated";
        const string Models = Root + "/Models";

        static readonly Dictionary<string, Material> _matCache = new Dictionary<string, Material>();

        [MenuItem("Cubestruction/Build Everything", priority = 0)]
        public static void BuildEverything()
        {
            _matCache.Clear();
            Directory.CreateDirectory(Gen + "/Materials");
            Directory.CreateDirectory(Gen + "/Prefabs");
            Directory.CreateDirectory(Root + "/Resources/ToolVisuals");
            Directory.CreateDirectory(Root + "/Scenes");
            AssetDatabase.Refresh();

            SetupRenderPipeline();
            SetupPlayerSettings();
            var mats = CreateCoreMaterials();
            BuildToolVisuals();
            var explosive = BuildExplosivePrefab();
            var cube = BuildCubePrefab(mats.voxel);
            var player = BuildPlayerPrefab(explosive, mats.vignette);
            var units = BuildUnitPrefabs();
            var list = BuildPrefabList(new[] { player, cube, explosive }.Concat(units).ToArray());
            BuildScene(player, cube, units, list, mats);
            AssetDatabase.SaveAssets();
            Debug.Log("<color=#3cf>Cubestruction:</color> build complete. Open Assets/Cubestruction/Scenes/Cubestruction.unity and press Play " +
                      "(PC), or see README for Quest/mobile builds.");
        }

        // ------------------------------------------------------------------ project settings

        static void SetupRenderPipeline()
        {
            const string path = Gen + "/CubestructionURP.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (existing == null)
            {
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, Gen + "/CubestructionURP_Renderer.asset");
                existing = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(existing, path);
            }
            existing.msaaSampleCount = 4;      // Quest: cheap MSAA, no post FX
            existing.supportsHDR = false;
            existing.shadowDistance = 0;
            existing.renderScale = 1f;
            EditorUtility.SetDirty(existing);
            GraphicsSettings.defaultRenderPipeline = existing;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = existing;
            }
        }

        static void SetupPlayerSettings()
        {
            PlayerSettings.companyName = "Cubestruction";
            PlayerSettings.productName = "Cubestruction";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.cubestruction.game");
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS, "com.cubestruction.game");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.runInBackground = true;   // keep hosting when alt-tabbed
        }

        // ------------------------------------------------------------------ materials

        struct CoreMats { public Material voxel, debris, glow, vignette, pedestal, bubble; }

        static CoreMats CreateCoreMaterials()
        {
            var gloss = Shader.Find("Cubestruction/AeroGloss");
            var unlit = Shader.Find("Cubestruction/UnlitTransparent");
            var m = new CoreMats
            {
                voxel = Mat("Voxel", gloss, Color.white, vertexColor: true, gloss: 60),
                debris = Mat("Debris", gloss, Color.white, vertexColor: true, gloss: 40),
                glow = Mat("Glow", unlit, Color.white),
                vignette = Mat("Vignette", unlit, Color.white),
                pedestal = Mat("Pedestal", gloss, new Color(0.45f, 0.85f, 0.35f)),
                bubble = Mat("Bubble", unlit, new Color(0.8f, 0.95f, 1f, 0.25f)),
            };
            m.glow.SetFloat("_DstBlend", (float)BlendMode.One);         // additive beams/tracers
            m.vignette.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.bubble.SetFloat("_DstBlend", (float)BlendMode.One);
            m.bubble.SetFloat("_Fresnel", 2.5f);
            return m;
        }

        static Material Mat(string name, Shader shader, Color c, bool vertexColor = false, float gloss = 90)
        {
            string path = $"{Gen}/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = shader;
            mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_UseVertexColor")) mat.SetFloat("_UseVertexColor", vertexColor ? 1 : 0);
            if (mat.HasProperty("_Gloss")) mat.SetFloat("_Gloss", gloss);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Replace imported FBX materials with AeroGloss materials of the same colour.</summary>
        static void Aerofy(GameObject go)
        {
            var gloss = Shader.Find("Cubestruction/AeroGloss");
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    string key = src ? src.name.Replace(" (Instance)", "") : "Default";
                    if (!_matCache.TryGetValue(key, out var m))
                    {
                        var col = src && src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : src ? src.color : Color.white;
                        bool isBody = key.StartsWith("Buddy_");
                        m = Mat("FBX_" + Sanitize(key), gloss, isBody ? Color.white * Mathf.Max(col.maxColorComponent, 0.9f) : col);
                        _matCache[key] = m;
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        static string Sanitize(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());

        static GameObject LoadModel(string relPath) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Models}/{relPath}.fbx");

        static GameObject InstantiateModel(string relPath, Transform parent, PrimitiveType fallback, Vector3 fallbackScale)
        {
            var src = LoadModel(relPath);
            GameObject go;
            if (src) go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            else
            {
                Debug.LogWarning($"Model {relPath}.fbx missing — using a primitive. Run Blender/build_all.py.");
                go = GameObject.CreatePrimitive(fallback);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.localScale = fallbackScale;
            }
            go.transform.SetParent(parent, false);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            Aerofy(go);
            return go;
        }

        // ------------------------------------------------------------------ tool visuals

        static void BuildToolVisuals()
        {
            foreach (var t in Balance.Tools)
            {
                var root = new GameObject(t.id);
                var model = InstantiateModel($"Tools/{t.id}", root.transform, PrimitiveType.Cube, new Vector3(0.06f, 0.06f, 0.35f));
                model.name = "Model";
                if (t.id == "ClimbingGloves") model.transform.localScale = Vector3.one * 0.9f;
                PrefabUtility.SaveAsPrefabAsset(root, $"{Root}/Resources/ToolVisuals/{t.id}.prefab");
                Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------------ network prefabs

        static GameObject SavePrefab(GameObject go, string name)
        {
            var p = PrefabUtility.SaveAsPrefabAsset(go, $"{Gen}/Prefabs/{name}.prefab");
            Object.DestroyImmediate(go);
            return p;
        }

        static GameObject BuildExplosivePrefab()
        {
            var go = new GameObject("Explosive");
            go.AddComponent<NetworkObject>();
            var rb = go.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            go.AddComponent<SphereCollider>().radius = 0.1f;
            go.AddComponent<NetworkTransform>();
            go.AddComponent<NetworkRigidbody>();
            go.AddComponent<ExplosiveProjectile>();
            return SavePrefab(go, "Explosive");
        }

        static GameObject BuildCubePrefab(Material voxel)
        {
            var go = new GameObject("VoxelCube");
            go.AddComponent<NetworkObject>();
            var vc = go.AddComponent<VoxelCube>();
            vc.voxelMaterial = voxel;
            vc.cellSize = 0.25f;
            return SavePrefab(go, "VoxelCube");
        }

        static GameObject BuildPlayerPrefab(GameObject explosive, Material vignette)
        {
            var go = new GameObject("Player");
            go.AddComponent<NetworkObject>();
            var nt = go.AddComponent<OwnerNetworkTransform>();
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = true;   // god giants
            var cc = go.AddComponent<CharacterController>();
            cc.height = 0.95f; cc.radius = 0.25f; cc.center = new Vector3(0, 0.475f, 0); cc.stepOffset = 0.3f; cc.slopeLimit = 55; cc.skinWidth = 0.03f;

            Transform Child(string n, Vector3 pos, bool synced)
            {
                var c = new GameObject(n).transform;
                c.SetParent(go.transform, false);
                c.localPosition = pos;
                if (synced) c.gameObject.AddComponent<OwnerNetworkTransform>();
                return c;
            }
            var head = Child("HeadAnchor", new Vector3(0, 0.85f, 0), true);
            var left = Child("LeftHand", new Vector3(-0.25f, 0.6f, 0.3f), true);
            var right = Child("RightHand", new Vector3(0.25f, 0.6f, 0.3f), true);
            foreach (var hand in new[] { left, right })
            {
                var socket = new GameObject("Socket").transform;
                socket.SetParent(hand, false);
                // controllers point forward; tools are modelled pointing along +Z with the grip at the origin
                socket.localRotation = Quaternion.Euler(20, 0, 0);
                var mitten = InstantiateModel("Env/Bubble", hand, PrimitiveType.Sphere, Vector3.one * 0.07f);
                mitten.name = "Mitten";
                mitten.transform.localScale = Vector3.one * 0.07f / Mathf.Max(0.001f, MeshSize(mitten));
            }

            var visualRoot = new GameObject("Visual").transform;
            visualRoot.SetParent(go.transform, false);
            var buddy = InstantiateModel("Buddy", visualRoot, PrimitiveType.Capsule, new Vector3(0.4f, 0.5f, 0.4f));
            buddy.name = "Buddy";
            var visual = visualRoot.gameObject.AddComponent<AvatarVisual>();

            var avatar = go.AddComponent<PlayerAvatar>();
            avatar.headAnchor = head; avatar.leftHand = left; avatar.rightHand = right;
            go.AddComponent<PlayerMotor>();
            go.AddComponent<ToolController>().explosivePrefab = explosive;
            go.AddComponent<PlayerRagdoll>().visual = visual;
            var vr = go.AddComponent<VRRig>(); vr.vignetteMaterial = vignette; vr.enabled = false;
            go.AddComponent<FlatRig>().enabled = false;
            go.AddComponent<GodController>().enabled = false;
            go.AddComponent<CommanderController>().enabled = false;
            return SavePrefab(go, "Player");
        }

        static float MeshSize(GameObject go)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            return mf && mf.sharedMesh ? mf.sharedMesh.bounds.size.x : 1f;
        }

        static GameObject[] BuildUnitPrefabs()
        {
            var list = new List<GameObject>();
            foreach (var u in Balance.Siege.units)
            {
                var go = new GameObject(u.id);
                go.AddComponent<NetworkObject>();
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = u.mass;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.constraints = RigidbodyConstraints.FreezeRotation;
                float size = u.id == "MegaBuddy" ? 2.2f : u.id == "BlockBrute" ? 1.4f : u.id == "CannonCart" ? 1.1f : 0.9f;
                var col = go.AddComponent<CapsuleCollider>();
                col.height = size; col.radius = size * 0.3f; col.center = new Vector3(0, size * 0.5f, 0);
                go.AddComponent<NetworkTransform>();
                go.AddComponent<NetworkRigidbody>();
                go.AddComponent<EnemyUnit>();
                var vis = new GameObject("Visual").transform;
                vis.SetParent(go.transform, false);
                var model = InstantiateModel($"Units/{u.id}", vis, PrimitiveType.Capsule, new Vector3(size * 0.6f, size * 0.5f, size * 0.6f));
                model.name = "Model";
                list.Add(SavePrefab(go, "Unit_" + u.id));
            }
            return list.ToArray();
        }

        static NetworkPrefabsList BuildPrefabList(GameObject[] prefabs)
        {
            const string path = Gen + "/CubestructionNetworkPrefabs.asset";
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path);
            if (list == null) { list = ScriptableObject.CreateInstance<NetworkPrefabsList>(); AssetDatabase.CreateAsset(list, path); }
            foreach (var p in list.PrefabList.ToList()) list.Remove(p);
            foreach (var p in prefabs) list.Add(new NetworkPrefab { Prefab = p });
            EditorUtility.SetDirty(list);
            return list;
        }

        // ------------------------------------------------------------------ scene

        static void BuildScene(GameObject player, GameObject cube, GameObject[] units, NetworkPrefabsList list, CoreMats mats)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // sky + light: bright Aero daylight
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetColor("_SkyTint", new Color(0.45f, 0.75f, 1f));
            sky.SetColor("_GroundColor", new Color(0.55f, 0.85f, 0.6f));
            sky.SetFloat("_AtmosphereThickness", 0.6f);
            sky.SetFloat("_Exposure", 1.25f);
            AssetDatabase.CreateAsset(sky, Gen + "/Materials/AeroSky.mat");
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.7f, 0.88f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.75f, 0.9f, 0.85f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.7f, 0.4f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.78f, 0.92f, 1f);
            RenderSettings.fogStartDistance = 40; RenderSettings.fogEndDistance = 160;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.97f, 0.9f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.Euler(50, -35, 0);

            // pedestal sized for the mining cube + a walkable ring
            var pedestal = InstantiateModel("Env/Pedestal", null, PrimitiveType.Cylinder, new Vector3(14, 0.5f, 14));
            pedestal.name = "Pedestal";
            float wanted = 40 * 0.25f + 10f;       // cube 10 m + 5 m ring each side
            var pb = pedestal.GetComponentInChildren<MeshFilter>()?.sharedMesh?.bounds.size.x ?? 14f;
            pedestal.transform.localScale = new Vector3(wanted / pb, 1, wanted / pb);
            foreach (var mf in pedestal.GetComponentsInChildren<MeshFilter>()) mf.gameObject.AddComponent<MeshCollider>();
            // big soft "grass" ground far below so falling players see something lovely
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Meadow"; ground.transform.position = new Vector3(0, -12, 0); ground.transform.localScale = Vector3.one * 40;
            ground.GetComponent<Renderer>().sharedMaterial = Mat("Meadow", Shader.Find("Cubestruction/AeroGloss"), new Color(0.35f, 0.8f, 0.35f), gloss: 12);
            // siege arena floor around the pedestal (units walk on it)
            var arena = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            arena.name = "SiegeArena"; arena.transform.position = new Vector3(0, -0.26f, 0); arena.transform.localScale = new Vector3(56, 0.25f, 56);
            arena.GetComponent<Renderer>().sharedMaterial = mats.pedestal;

            // decorative floating bubbles
            var bubbles = new GameObject("Bubbles").transform;
            var rng = new System.Random(7);
            for (int i = 0; i < 40; i++)
            {
                var b = InstantiateModel("Env/Bubble", bubbles, PrimitiveType.Sphere, Vector3.one);
                foreach (var r in b.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mats.bubble;
                float a = (float)rng.NextDouble() * Mathf.PI * 2, d = 20 + (float)rng.NextDouble() * 50;
                b.transform.position = new Vector3(Mathf.Cos(a) * d, 2 + (float)rng.NextDouble() * 25, Mathf.Sin(a) * d);
                b.transform.localScale = Vector3.one * (0.5f + (float)rng.NextDouble() * 3f);
                b.AddComponent<FloatBob>();
            }

            // shop kiosk (decorative landmark; the shop opens from the menu button anywhere)
            var kiosk = InstantiateModel("Env/ShopKiosk", null, PrimitiveType.Cube, new Vector3(1, 2, 1));
            kiosk.name = "ShopKiosk"; kiosk.transform.position = new Vector3(0, 0, -(5 + 3.5f));
            foreach (var mf in kiosk.GetComponentsInChildren<MeshFilter>()) mf.gameObject.AddComponent<MeshCollider>();

            // networking
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            var utp = nmGo.AddComponent<UnityTransport>();
            nm.NetworkConfig = new NetworkConfig { NetworkTransport = utp, PlayerPrefab = player, ConnectionApproval = false, TickRate = 30 };
            nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);
            nmGo.AddComponent<NetBootstrap>();

            // scene network objects
            var mmGo = new GameObject("MatchManager");
            mmGo.AddComponent<NetworkObject>();
            var mm = mmGo.AddComponent<MatchManager>();
            mm.cubePrefab = cube.GetComponent<NetworkObject>();
            mm.pedestal = pedestal.transform;

            var sdGo = new GameObject("SiegeDirector");
            sdGo.AddComponent<NetworkObject>();
            var sd = sdGo.AddComponent<SiegeDirector>();
            sd.unitPrefabs = units;
            var portals = new List<Transform>();
            for (int i = 0; i < 3; i++)
            {
                float a = Mathf.PI * 0.5f + (i - 1) * 0.9f;   // three portals on the far side
                var p = InstantiateModel("Env/Bubble", sdGo.transform, PrimitiveType.Sphere, Vector3.one);
                p.name = $"Portal{i}";
                foreach (var r in p.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mats.bubble;
                p.transform.position = new Vector3(Mathf.Cos(a) * 22f, 1.5f, Mathf.Sin(a) * 22f);
                p.transform.localScale = Vector3.one * 3f;
                portals.Add(p.transform);
            }
            sd.spawnPortals = portals.ToArray();

            var fxGo = new GameObject("FxHub");
            fxGo.AddComponent<NetworkObject>();
            var fx = fxGo.AddComponent<FxHub>();
            fx.glowMaterial = mats.glow; fx.bubbleMaterial = mats.bubble;
            fx.sparkBurst = MakeSparks(fxGo.transform, mats.debris);
            fx.boomClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/boom.wav");
            fx.hitClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/hit.wav");
            fx.zapClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/zap.wav");

            var debrisGo = new GameObject("VoxelDebris");
            var ps = debrisGo.AddComponent<ParticleSystem>();
            ps.Stop();
            debrisGo.GetComponent<ParticleSystemRenderer>().sharedMaterial = mats.debris;
            debrisGo.AddComponent<VoxelDebris>();

            new GameObject("GameMenus").AddComponent<GameMenus>();

            // a lobby camera so there's something to look at before a player spawns
            var lobbyCam = new GameObject("LobbyCamera").AddComponent<Camera>();
            lobbyCam.transform.SetPositionAndRotation(new Vector3(0, 9, -22), Quaternion.Euler(18, 0, 0));
            lobbyCam.gameObject.AddComponent<LobbyCamera>();

            const string scenePath = Root + "/Scenes/Cubestruction.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        }

        static ParticleSystem MakeSparks(Transform parent, Material mat)
        {
            var go = new GameObject("Sparks");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();
            var main = ps.main;
            main.playOnAwake = false; main.loop = false;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 600;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.1f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            r.sharedMaterial = mat;
            return ps;
        }
    }
}
#endif
