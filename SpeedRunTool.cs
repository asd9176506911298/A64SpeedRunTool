using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using KinematicCharacterController;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Attributes;

namespace a64SpeedRunTool
{
    internal class SpeedRunTool : MonoBehaviour
    {
        // Teleport save data
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private bool hasSavedPosition = false;

        // Signature scan data
        private IntPtr typeInfoPtrAddr = IntPtr.Zero;
        private IntPtr skipFlagAddr = IntPtr.Zero;
        private float autoSkipTimer = 0f;
        private List<(Transform transform, string name, string state)> enemyStates
            = new List<(Transform, string, string)>();
        private float enemyScanTimer = 0f;
        private const float EnemyScanInterval = 0.3f;

        // NoClip / Jump settings
        public KeyCode noClipToggleKey = KeyCode.V;
        public float noClipSpeed = 12f;
        public float noClipFastMultiplier = 3f;
        private bool noClipActive = false;
        private KinematicCharacterMotor cachedMotor;
        private bool cachedCapsulePrevEnabled = true;
        private Transform cachedCamTransform;

        // Name of the actual first-person camera object used for rendering.
        // This scene has multiple objects named "Main Camera", so Camera.main
        // often grabs the wrong one. Look it up by name instead. If your game
        // uses a different camera name in another scene, change this field.
        public string fpsCameraObjectName = "CameraFPS(Clone)";

        public KeyCode jumpKey = KeyCode.Space;
        public float jumpForce = 8f;

        // Game speed control
        public KeyCode speedDownKey = KeyCode.Z;      // Slow down (lower custom scale)
        public KeyCode speedUpKey = KeyCode.X;        // Speed up (raise custom scale)
        public KeyCode speedToggleKey = KeyCode.C;    // Toggle: normal speed <-> custom scale
        public float speedStep = 0.1f;                // Amount changed per Z/X press
        public float minTimeScale = 0.05f;
        public float maxTimeScale = 3f;
        private float customTimeScale = 1f;           // Your target custom scale
        private bool usingCustomSpeed = false;
        private float defaultFixedDeltaTime = 0.02f;  // Original physics update interval

        // Windows API
        [DllImport("kernel32.dll")]
        private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect;
            public IntPtr RegionSize; public uint State; public uint Protect; public uint Type;
        }

        public void Awake()
        {
            GameObject.DontDestroyOnLoad(this.gameObject);
            Plugin.Log.LogInfo("[SpeedRunTool] Awake: Initializing Scanner...");
            defaultFixedDeltaTime = Time.fixedDeltaTime; // Cache original physics step so we can restore it later
            FindTypeInfoAddressOnce();
        }

        private d6StateManager trackedEnemy = null;
        private float displayTimer = 0f;
        private string currentStateText = "";

        public void Update()
        {
            if (typeInfoPtrAddr != IntPtr.Zero && skipFlagAddr == IntPtr.Zero)
            {
                MonitorInitialization();
            }

            if (skipFlagAddr != IntPtr.Zero)
            {
                autoSkipTimer += Time.deltaTime;
                if (autoSkipTimer >= 0.5f)
                {
                    TryApplySkip();
                    autoSkipTimer = 0f;
                }
            }

            if (Input.GetKeyDown(KeyCode.F1)) SavePosition();
            if (Input.GetKeyDown(KeyCode.F2)) LoadAndTeleport();
            if (Input.GetKeyDown(KeyCode.F3))
            {
                var enemies = GameObject.FindGameObjectsWithTag("mob");
                if (enemies.Length > 0)
                {
                    trackedEnemy = enemies[0].GetComponent<d6StateManager>();
                    Plugin.Log.LogInfo($"[F3] Now tracking: {enemies[0].name}");
                }
            }

            // NoClip toggle and movement
            if (Input.GetKeyDown(noClipToggleKey))
            {
                ToggleNoClip();
            }
            if (noClipActive)
            {
                HandleNoClipMovement();
            }

            // Jump (experimental, see notes below)
            if (Input.GetKeyDown(jumpKey))
            {
                TryForceJump();
            }

            // Game speed control (Z slow down / X speed up / C toggle normal vs custom speed)
            if (Input.GetKeyDown(speedDownKey))
            {
                customTimeScale = Mathf.Clamp(customTimeScale - speedStep, minTimeScale, maxTimeScale);
                Plugin.Log.LogInfo($"[Speed] Custom scale -> {customTimeScale:F2}x");
                if (usingCustomSpeed) ApplyTimeScale(customTimeScale);
            }
            if (Input.GetKeyDown(speedUpKey))
            {
                customTimeScale = Mathf.Clamp(customTimeScale + speedStep, minTimeScale, maxTimeScale);
                Plugin.Log.LogInfo($"[Speed] Custom scale -> {customTimeScale:F2}x");
                if (usingCustomSpeed) ApplyTimeScale(customTimeScale);
            }
            if (Input.GetKeyDown(speedToggleKey))
            {
                usingCustomSpeed = !usingCustomSpeed;
                ApplyTimeScale(usingCustomSpeed ? customTimeScale : 1f);
                Plugin.Log.LogInfo(usingCustomSpeed
                    ? $"[Speed] Switched to custom speed: {customTimeScale:F2}x"
                    : "[Speed] Switched back to normal speed 1.00x");
            }

            enemyScanTimer += Time.deltaTime;
            if (enemyScanTimer >= EnemyScanInterval)
            {
                enemyScanTimer = 0f;
                RefreshAllEnemyStates();
            }

            if (trackedEnemy != null)
            {
                displayTimer += Time.deltaTime;
                if (displayTimer >= 0.2f)
                {
                    displayTimer = 0f;
                    currentStateText = GetCurrentStateName(trackedEnemy);
                }
            }
        }

        // Apply a time scale and keep fixedDeltaTime in sync so slow-mo/fast-forward
        // doesn't make physics simulation stutter or behave incorrectly.
        [HideFromIl2Cpp]
        private void ApplyTimeScale(float scale)
        {
            Time.timeScale = scale;
            Time.fixedDeltaTime = defaultFixedDeltaTime * scale;
        }

        // Find the player's KinematicCharacterMotor (only searched once, then cached)
        [HideFromIl2Cpp]
        private KinematicCharacterMotor GetPlayerMotor()
        {
            if (cachedMotor != null) return cachedMotor;
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                cachedMotor = player.GetComponent<KinematicCharacterMotor>();
            }
            return cachedMotor;
        }

        // Toggle NoClip
        [HideFromIl2Cpp]
        private void ToggleNoClip()
        {
            var motor = GetPlayerMotor();
            if (motor == null)
            {
                Plugin.Log.LogWarning("[NoClip] Could not find the player's KinematicCharacterMotor.");
                return;
            }

            noClipActive = !noClipActive;

            if (noClipActive)
            {
                // Disable KCC simulation so the character is no longer bound by physics/collision
                if (motor.Capsule != null)
                {
                    cachedCapsulePrevEnabled = motor.Capsule.enabled;
                    motor.Capsule.enabled = false;
                }
                motor.enabled = false;
                Plugin.Log.LogInfo("[NoClip] Enabled.");
            }
            else
            {
                // Resync KCC's internal state using the current position, to avoid
                // clipping or launching the character when leaving NoClip.
                motor.SetPositionAndRotation(motor.transform.position, motor.transform.rotation, true);
                if (motor.Capsule != null)
                {
                    motor.Capsule.enabled = cachedCapsulePrevEnabled;
                }
                motor.enabled = true;
                Plugin.Log.LogInfo("[NoClip] Disabled, motor resynced.");
            }
        }

        // Find the real first-person camera (not Camera.main, since this scene has
        // multiple identically-named cameras)
        [HideFromIl2Cpp]
        private Transform GetDirectionReference()
        {
            if (cachedCamTransform != null) return cachedCamTransform;

            var camObj = GameObject.Find(fpsCameraObjectName);
            if (camObj != null)
            {
                cachedCamTransform = camObj.transform;
                Plugin.Log.LogInfo($"[NoClip] Locked direction reference camera: {fpsCameraObjectName}");
                return cachedCamTransform;
            }

            Plugin.Log.LogWarning($"[NoClip] Could not find an object named \"{fpsCameraObjectName}\", falling back to the character's own orientation.");
            return cachedMotor != null ? cachedMotor.transform : null;
        }

        // Free-fly movement while NoClip is active.
        // Uses the locked first-person camera's full direction (including pitch),
        // matching the usual "fly where you look" NoClip feel. Not flattened.
        // Vertical movement is handled separately via E/Q, independent of look angle.
        // Movement is written through motor.SetPositionAndRotation() so KCC's
        // internally tracked coordinates stay in sync with the transform.
        [HideFromIl2Cpp]
        private void HandleNoClipMovement()
        {
            var motor = cachedMotor;
            if (motor == null) return;

            Transform refT = GetDirectionReference();
            if (refT == null) return;

            // This game uses Rewired for input, and the Input Manager's
            // Horizontal/Vertical axis settings are unreliable here (this was the
            // real cause of W/A not responding and Q drifting). Read raw key
            // states directly instead, bypassing the axis configuration.
            float h = 0f;
            if (Input.GetKey(KeyCode.D)) h += 1f;
            if (Input.GetKey(KeyCode.A)) h -= 1f;
            float v = 0f;
            if (Input.GetKey(KeyCode.W)) v += 1f;
            if (Input.GetKey(KeyCode.S)) v -= 1f;
            float up = 0f;
            if (Input.GetKey(KeyCode.E)) up += 1f;
            if (Input.GetKey(KeyCode.Q)) up -= 1f;

            Vector3 move = refT.right * h + refT.forward * v + Vector3.up * up;
            if (move.sqrMagnitude < 0.0001f) return;

            float speed = noClipSpeed * (Input.GetKey(KeyCode.LeftShift) ? noClipFastMultiplier : 1f);
            Vector3 newPos = motor.transform.position + move.normalized * speed * Time.deltaTime;

            // Move via the official KCC API so the motor's internal coordinates
            // stay in sync with what's rendered.
            motor.SetPositionAndRotation(newPos, motor.transform.rotation, true);
        }

        // Attempt to jump (experimental).
        // Note: KCC character velocity is usually recalculated every FixedUpdate by
        // the game's own ICharacterController.UpdateVelocity(), so writing
        // BaseVelocity directly here may get overwritten the very next frame.
        // If this has no visible effect, a Harmony postfix patch on the game's
        // player controller's UpdateVelocity method is needed for a reliable jump.
        [HideFromIl2Cpp]
        private void TryForceJump()
        {
            if (noClipActive) return; // No need to jump while in NoClip
            var motor = GetPlayerMotor();
            if (motor == null) return;

            Vector3 v = motor.BaseVelocity;
            v.y = jumpForce;
            motor.BaseVelocity = v;
            motor.ForceUnground();
        }

        [HideFromIl2Cpp]
        private void RefreshAllEnemyStates()
        {
            try
            {
                var newList = new List<(Transform, string, string)>();
                var enemies = GameObject.FindGameObjectsWithTag("mob");

                foreach (var enemy in enemies)
                {
                    var sm = enemy.GetComponent<d6StateManager>();
                    if (sm == null) continue;

                    string stateName = GetCurrentStateName(sm);
                    newList.Add((enemy.transform, enemy.name, stateName));
                }

                enemyStates = newList;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EnemyStates] Refresh failed: {e}");
            }
        }

        [HideFromIl2Cpp]
        private string GetCurrentStateName(d6StateManager sm)
        {
            try
            {
                var stateObj = sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_1;
                if (stateObj == null) return "None";

                long ptr = stateObj.Pointer.ToInt64();

                var nameDict = sm.field_Private_Dictionary_2_String_ObjectPublicBoInBoObInBoObBoObObUnique_0;
                if (nameDict != null)
                {
                    var it = nameDict.GetEnumerator();
                    while (it.MoveNext())
                    {
                        if (it.Current.Value != null && it.Current.Value.Pointer.ToInt64() == ptr)
                            return it.Current.Key;
                    }
                }
                return "Unknown";
            }
            catch
            {
                return "Error";
            }
        }

        public void OnGUI()
        {
            // Persistent on-screen display of the current speed scale
            var speedStyle = new GUIStyle(GUI.skin.label);
            speedStyle.fontSize = 16;
            speedStyle.fontStyle = FontStyle.Bold;
            speedStyle.normal.textColor = usingCustomSpeed ? Color.cyan : Color.white;

            string speedLabel = usingCustomSpeed
                ? $"Speed: {customTimeScale:F2}x (Custom)"
                : $"Speed: 1.00x (Normal)";
            GUI.Label(new Rect(10, 50, 300, 30), speedLabel, speedStyle);

            if (enemyStates.Count == 0) return;

            var cam = Camera.main;
            if (cam == null) return;

            var style = new GUIStyle(GUI.skin.label);
            style.fontSize = 14;
            style.normal.textColor = Color.yellow;
            style.alignment = TextAnchor.MiddleCenter;

            foreach (var (t, name, state) in enemyStates)
            {
                if (t == null) continue;

                Vector3 worldPos = t.position + Vector3.up * 2f;
                Vector3 screenPos = cam.WorldToScreenPoint(worldPos);

                if (screenPos.z <= 0) continue;

                float guiX = screenPos.x - 60f;
                float guiY = Screen.height - screenPos.y;

                GUI.Label(new Rect(guiX, guiY, 120, 40), $"{state}", style);
            }
        }

        private void SavePosition()
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                savedPosition = player.transform.position;
                savedRotation = player.transform.rotation;
                hasSavedPosition = true;
                Plugin.Log.LogInfo($"[F1] Saved: {savedPosition}");
            }
        }

        private void LoadAndTeleport()
        {
            if (!hasSavedPosition) return;
            Plugin.Log.LogInfo("[F2] Starting Reload Sequence...");
            StartCoroutine(ReloadAndRestore().WrapToIl2Cpp());
        }

        private IEnumerator ReloadAndRestore()
        {
            Scene currentScene = SceneManager.GetActiveScene();
            int buildIndex = currentScene.buildIndex;
            string mainSceneName = currentScene.name;

            var additiveSceneNames = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name != mainSceneName && scene.name != "DontDestroyOnLoad" && scene.name != "HideAndDontSave")
                    additiveSceneNames.Add(scene.name);
            }

            SceneManager.LoadScene(buildIndex, LoadSceneMode.Single);

            while (SceneManager.GetActiveScene().name != mainSceneName)
            {
                TryApplySkip();
                yield return null;
            }

            foreach (var sceneName in additiveSceneNames)
            {
                AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                while (asyncLoad != null && !asyncLoad.isDone)
                {
                    TryApplySkip();
                    yield return null;
                }
            }

            GameObject player = null;
            float timeout = 10f;
            while (player == null && timeout > 0)
            {
                TryApplySkip();
                player = GameObject.FindGameObjectWithTag("Player");
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (player != null)
            {
                var motor = player.GetComponent<KinematicCharacterMotor>();
                if (motor != null) motor.SetPositionAndRotation(savedPosition, savedRotation, true);
                else { player.transform.position = savedPosition; player.transform.rotation = savedRotation; }
                Plugin.Log.LogInfo("[F2] Teleport Success.");
            }

            Plugin.Log.LogInfo("[Coroutine] Finished.");
        }

        [HideFromIl2Cpp]
        private unsafe void TryApplySkip()
        {
            if (skipFlagAddr == IntPtr.Zero) return;
            try { *(byte*)skipFlagAddr = 1; } catch { }
        }

        [HideFromIl2Cpp]
        private unsafe void FindTypeInfoAddressOnce()
        {
            ProcessModule module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.ToLower().Contains("gameassembly"));
            if (module == null)
            {
                Plugin.Log.LogWarning("[Scanner] GameAssembly module not found.");
                return;
            }

            string[] patterns = new[]
            {
                "48 8B 05 ?? ?? ?? ?? 48 8B 88 ?? ?? ?? ?? C6 01 ?? 4D 85 F6 0F 84 ?? ?? ?? ?? 41 80 7E ?? ?? 74",
                "48 8B 05 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8B 88 ?? ?? ?? ?? C6 01 ?? 48 83 C4 ?? 5F C3 E8 ?? ?? ?? ?? CC E8 ?? ?? ?? ?? CC E8 ?? ?? ?? ?? CC E8 ?? ?? ?? ?? CC CC CC 48 89 5C 24 ?? 57 48 83 EC ?? 80 3D ?? ?? ?? ?? ?? 8B DA",
            };

            for (int i = 0; i < patterns.Length; i++)
            {
                IntPtr found = SafeFullModuleScan(module.BaseAddress, (long)module.ModuleMemorySize, patterns[i]);
                if (found != IntPtr.Zero)
                {
                    byte* instr = (byte*)found;
                    int ripOffset = *(int*)(instr + 3);
                    typeInfoPtrAddr = (IntPtr)(instr + 7 + ripOffset);
                    Plugin.Log.LogInfo($"[Scanner] Pattern #{i} matched. Tracking TypeInfo at 0x{typeInfoPtrAddr.ToInt64():X}");
                    return;
                }
                else
                {
                    Plugin.Log.LogWarning($"[Scanner] Pattern #{i} not found, trying next...");
                }
            }

            Plugin.Log.LogError("[Scanner] All patterns failed. TypeInfo not found.");
        }

        [HideFromIl2Cpp]
        private unsafe void MonitorInitialization()
        {
            if (!IsMemoryReadable(typeInfoPtrAddr)) return;
            IntPtr typeInfo = *(IntPtr*)typeInfoPtrAddr;
            if (typeInfo == IntPtr.Zero) return;

            IntPtr staticFieldsBaseAddr = (IntPtr)((byte*)typeInfo + 0xB8);
            if (!IsMemoryReadable(staticFieldsBaseAddr)) return;

            IntPtr staticFields = *(IntPtr*)staticFieldsBaseAddr;
            if (staticFields == IntPtr.Zero) return;

            skipFlagAddr = staticFields;
            Plugin.Log.LogInfo($"[Scanner] SkipFlag BOUND at 0x{skipFlagAddr.ToInt64():X}");
            TryApplySkip();
        }

        [HideFromIl2Cpp]
        private unsafe IntPtr SafeFullModuleScan(IntPtr baseAddr, long size, string pattern)
        {
            string[] parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            byte?[] p = new byte?[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                p[i] = (parts[i] == "?" || parts[i] == "??") ? null : (byte?)Convert.ToByte(parts[i], 16);

            long current = baseAddr.ToInt64();
            long end = current + size;
            while (current < end)
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery((IntPtr)current, out mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;
                if (mbi.State == 0x1000 && (mbi.Protect & 0x101) == 0)
                {
                    byte* ptr = (byte*)mbi.BaseAddress;
                    long scanLimit = Math.Min(mbi.RegionSize.ToInt64(), end - current);
                    for (long i = 0; i < scanLimit - p.Length; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < p.Length; j++)
                        {
                            if (p[j].HasValue && p[j].Value != ptr[i + j]) { match = false; break; }
                        }
                        if (match) return (IntPtr)(ptr + i);
                    }
                }
                current += mbi.RegionSize.ToInt64();
            }
            return IntPtr.Zero;
        }

        [HideFromIl2Cpp]
        private bool IsMemoryReadable(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return false;
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(ptr, out mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) return false;
            return mbi.State == 0x1000 && (mbi.Protect & 0x101) == 0;
        }
    }
}