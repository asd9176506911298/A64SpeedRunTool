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
        // 傳送點資訊
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private bool hasSavedPosition = false;

        // 特徵碼掃描資訊
        private IntPtr typeInfoPtrAddr = IntPtr.Zero;
        private IntPtr skipFlagAddr = IntPtr.Zero;
        private float autoSkipTimer = 0f;
        private List<(Transform transform, string name, string state)> enemyStates
            = new List<(Transform, string, string)>();
        private float enemyScanTimer = 0f;
        private const float EnemyScanInterval = 0.3f;

        // === 新增：NoClip / Jump 相關 ===
        public KeyCode noClipToggleKey = KeyCode.V;
        public float noClipSpeed = 12f;
        public float noClipFastMultiplier = 3f;
        private bool noClipActive = false;
        private KinematicCharacterMotor cachedMotor;
        private bool cachedCapsulePrevEnabled = true;
        private Transform cachedCamTransform;

        // 場景裡實際用來看畫面的第一人稱攝影機物件名稱。
        // 因為這個場景同時存在多台叫 "Main Camera" 的攝影機，Camera.main 常常抓錯，
        // 所以改成直接用名字鎖定真正的那一台。如果你的遊戲換了場景名稱不一樣，改這裡就好。
        public string fpsCameraObjectName = "CameraFPS(Clone)";

        public KeyCode jumpKey = KeyCode.Space;
        public float jumpForce = 8f;

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

            // === 新增：NoClip 切換與移動 ===
            if (Input.GetKeyDown(noClipToggleKey))
            {
                ToggleNoClip();
            }
            if (noClipActive)
            {
                HandleNoClipMovement();
            }

            // === 新增：跳躍（實驗性，見上方說明）===
            if (Input.GetKeyDown(jumpKey))
            {
                TryForceJump();
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

        // === 新增：找到玩家身上的 KinematicCharacterMotor（只找一次，快取起來）===
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

        // === 新增：切換 NoClip ===
        [HideFromIl2Cpp]
        private void ToggleNoClip()
        {
            var motor = GetPlayerMotor();
            if (motor == null)
            {
                Plugin.Log.LogWarning("[NoClip] 找不到玩家的 KinematicCharacterMotor。");
                return;
            }

            noClipActive = !noClipActive;

            if (noClipActive)
            {
                // 關閉 KCC 的模擬，讓角色不再受物理/碰撞限制
                if (motor.Capsule != null)
                {
                    cachedCapsulePrevEnabled = motor.Capsule.enabled;
                    motor.Capsule.enabled = false;
                }
                motor.enabled = false;
                Plugin.Log.LogInfo("[NoClip] 已開啟。");
            }
            else
            {
                // 用目前位置重新同步 KCC 的內部狀態，避免恢復瞬間穿模或彈飛
                motor.SetPositionAndRotation(motor.transform.position, motor.transform.rotation, true);
                if (motor.Capsule != null)
                {
                    motor.Capsule.enabled = cachedCapsulePrevEnabled;
                }
                motor.enabled = true;
                Plugin.Log.LogInfo("[NoClip] 已關閉，狀態已同步。");
            }
        }

        // === 新增：找到真正的第一人稱攝影機（不用 Camera.main，因為場景有多台同名攝影機）===
        [HideFromIl2Cpp]
        private Transform GetDirectionReference()
        {
            if (cachedCamTransform != null) return cachedCamTransform;

            var camObj = GameObject.Find(fpsCameraObjectName);
            if (camObj != null)
            {
                cachedCamTransform = camObj.transform;
                Plugin.Log.LogInfo($"[NoClip] 已鎖定方向參考攝影機: {fpsCameraObjectName}");
                return cachedCamTransform;
            }

            Plugin.Log.LogWarning($"[NoClip] 找不到名為 \"{fpsCameraObjectName}\" 的物件，退回用角色本身方向。");
            return cachedMotor != null ? cachedMotor.transform : null;
        }

        // === 新增：NoClip 模式下的自由飛行移動 ===
        // 用實際鎖定的第一人稱攝影機的完整方向（含俯仰角），符合 Noclip 飛行「看哪飛哪」的直覺，
        // 不攤平。上下另外用 E/Q 控制，跟看的角度無關。
        // 移動時透過 motor.SetPositionAndRotation() 寫入，讓 KCC 內部追蹤的座標跟 transform 保持同步。
        [HideFromIl2Cpp]
        private void HandleNoClipMovement()
        {
            var motor = cachedMotor;
            if (motor == null) return;

            Transform refT = GetDirectionReference();
            if (refT == null) return;

            // 這款遊戲用 Rewired 做輸入，Input Manager 的 Horizontal/Vertical 軸設定不可靠
            // （這就是之前 W/A 沒反應、Q 會飄的真正原因），改成直接讀鍵盤按鍵狀態，繞過軸設定。
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

            // 用 KCC 官方 API 搬移，確保 Motor 內部座標跟畫面同步
            motor.SetPositionAndRotation(newPos, motor.transform.rotation, true);
        }

        // === 新增：嘗試跳躍（實驗性）===
        // 注意：KCC 角色的速度通常由遊戲自己的 ICharacterController.UpdateVelocity()
        // 每個 FixedUpdate 重新計算，這裡直接寫 BaseVelocity 可能會在下一幀被蓋掉。
        // 若沒有效果，需要用 Harmony 對遊戲角色控制器的 UpdateVelocity 做 Postfix 補丁才能穩定生效。
        [HideFromIl2Cpp]
        private void TryForceJump()
        {
            if (noClipActive) return; // NoClip 中不需要跳躍
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