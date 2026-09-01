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
        private IntPtr typeInfoPtrAddr = IntPtr.Zero; // 指向 TypeInfo 的門牌
        private IntPtr skipFlagAddr = IntPtr.Zero;    // 最終靜態變數地址
        private float autoSkipTimer = 0f;
        private List<(Transform transform, string name, string state)> enemyStates
            = new List<(Transform, string, string)>();
        private float enemyScanTimer = 0f;
        private const float EnemyScanInterval = 0.3f; // 状态文字不用太频繁刷新

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

            // 執行唯一一次的特徵碼掃描
            FindTypeInfoAddressOnce();
        }

        private d6StateManager trackedEnemy = null;
        private float displayTimer = 0f;
        private string currentStateText = "";

        public void Update()
        {
            // 1. 如果有門牌但還沒綁定變數，持續監控初始化 (等到動畫跑起來)
            if (typeInfoPtrAddr != IntPtr.Zero && skipFlagAddr == IntPtr.Zero)
            {
                MonitorInitialization();
            }

            // 2. 只要有找到變數地址，每隔 5 秒執行一次 TryApplySkip
            if (skipFlagAddr != IntPtr.Zero)
            {
                autoSkipTimer += Time.deltaTime;
                if (autoSkipTimer >= 5.0f)
                {
                    TryApplySkip();
                    autoSkipTimer = 0f;
                }
            }

            // 3. 鍵盤輸入
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

            enemyScanTimer += Time.deltaTime;
            if (enemyScanTimer >= EnemyScanInterval)
            {
                enemyScanTimer = 0f;
                RefreshAllEnemyStates();
            }

            // 每 0.2 秒刷新一次状态文字，避免每帧都算
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

        // === OnGUI 显示清单 ===
        public void OnGUI()
        {
            if (enemyStates.Count == 0) return;

            var cam = Camera.main;
            if (cam == null) return;

            // 简单的文字样式设定 (可选，让字更醒目)
            var style = new GUIStyle(GUI.skin.label);
            style.fontSize = 14;
            style.normal.textColor = Color.yellow;
            style.alignment = TextAnchor.MiddleCenter;

            foreach (var (t, name, state) in enemyStates)
            {
                if (t == null) continue; // 敌人可能已被销毁

                // 头顶偏移，视角色高度调整 (2f 是大概的头顶高度，可依模型调整)
                Vector3 worldPos = t.position + Vector3.up * 2f;
                Vector3 screenPos = cam.WorldToScreenPoint(worldPos);

                if (screenPos.z <= 0) continue; // 在镜头后面，不显示

                // GUI 的 Y 轴跟 WorldToScreenPoint 是相反的
                float guiX = screenPos.x - 60f;   // 让文字置中 (宽度120的一半)
                float guiY = Screen.height - screenPos.y;

                GUI.Label(new Rect(guiX, guiY, 120, 40), $"{state}", style);
            }
        }

        [HideFromIl2Cpp]
        private void DumpCurrentState(d6StateManager sm)
        {
            try
            {
                // 建立 ptr -> 状态名 对照表
                var ptrToName = new Dictionary<long, string>();
                var nameDict = sm.field_Private_Dictionary_2_String_ObjectPublicBoInBoObInBoObBoObObUnique_0;
                if (nameDict != null)
                {
                    var it = nameDict.GetEnumerator();
                    while (it.MoveNext())
                    {
                        var kv = it.Current;
                        if (kv.Value != null)
                            ptrToName[kv.Value.Pointer.ToInt64()] = kv.Key;
                    }
                }

                // 检查 4 个候选字段，看哪个能对上名字
                var candidates = new (string label, dynamic val)[]
                {
            ("_0", sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_0),
            ("_1", sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_1),
            ("_2", sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_2),
            ("_3", sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_3),
                };

                foreach (var (label, val) in candidates)
                {
                    if (val == null) { Plugin.Log.LogInfo($"[F3] {label}: null"); continue; }

                    long ptr = val.Pointer.ToInt64();
                    if (ptrToName.TryGetValue(ptr, out var name))
                        Plugin.Log.LogInfo($"[F3] {label} = {name}  (ptr={ptr})");
                    else
                        Plugin.Log.LogInfo($"[F3] {label} = <unmatched>  (ptr={ptr})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[F3] Failed: {e}");
            }
        }

        [HideFromIl2Cpp]
        private void DumpStateManager(d6StateManager sm)
        {
            try
            {
                // 1. 印出 string -> State 注册表 (所有状态名)
                var nameDict = sm.field_Private_Dictionary_2_String_ObjectPublicBoInBoObInBoObBoObObUnique_0;
                if (nameDict != null)
                {
                    var it = nameDict.GetEnumerator();
                    while (it.MoveNext())
                    {
                        var kv = it.Current;
                        var ptr = kv.Value == null ? "null" : kv.Value.Pointer.ToString();
                        Plugin.Log.LogInfo($"[F3] StateName: {kv.Key}  ptr={ptr}");
                    }
                }

                // 2. 印出 int -> string ID 对照表
                var idDict = sm.field_Private_Dictionary_2_Int32_String_0;
                if (idDict != null)
                {
                    var it2 = idDict.GetEnumerator();
                    while (it2.MoveNext())
                    {
                        Plugin.Log.LogInfo($"[F3] IdMap: {it2.Current.Key} -> {it2.Current.Value}");
                    }
                }

                // 3. 印出 4 个候选"状态"字段目前指向哪个物件 (用 Pointer 比对)
                var s0 = sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_0;
                var s1 = sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_1;
                var s2 = sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_2;
                var s3 = sm.field_Private_ObjectPublicBoInBoObInBoObBoObObUnique_3;

                Plugin.Log.LogInfo($"[F3] Candidate _0 ptr: {(s0 == null ? "null" : s0.Pointer.ToString())}");
                Plugin.Log.LogInfo($"[F3] Candidate _1 ptr: {(s1 == null ? "null" : s1.Pointer.ToString())}");
                Plugin.Log.LogInfo($"[F3] Candidate _2 ptr: {(s2 == null ? "null" : s2.Pointer.ToString())}");
                Plugin.Log.LogInfo($"[F3] Candidate _3 ptr: {(s3 == null ? "null" : s3.Pointer.ToString())}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[F3] Dump failed: {e}");
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

            // 記錄所有附加場景
            var additiveSceneNames = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name != mainSceneName && scene.name != "DontDestroyOnLoad" && scene.name != "HideAndDontSave")
                    additiveSceneNames.Add(scene.name);
            }

            // 重載主場景 (會清除一切)
            SceneManager.LoadScene(buildIndex, LoadSceneMode.Single);

            // 等主場景加載 (期間不斷嘗試跳過動畫)
            while (SceneManager.GetActiveScene().name != mainSceneName)
            {
                TryApplySkip();
                yield return null;
            }

            // 重新加回附加場景
            foreach (var sceneName in additiveSceneNames)
            {
                AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                while (asyncLoad != null && !asyncLoad.isDone)
                {
                    TryApplySkip();
                    yield return null;
                }
            }

            // 等待玩家物件生成 (最多 10 秒)
            GameObject player = null;
            float timeout = 10f;
            while (player == null && timeout > 0)
            {
                TryApplySkip(); // 動畫可能在這個階段播放，必須持續寫入
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

            // 多組候選特徵碼，依序嘗試，直到找到為止。
            // 不同版本/編譯優化可能導致指令排列不同，所以保留多組備援。
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