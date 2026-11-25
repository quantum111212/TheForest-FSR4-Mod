using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TheForestFSR4
{
    public enum Fsr4QualityMode
    {
        NativeAA = 0,
        UltraQuality = 1,
        Quality = 2,
        Balanced = 3,
        Performance = 4,
        UltraPerformance = 5
    }

    internal static class NativeBridge
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate void InitLoggingDelegate(string logPath);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFrameDataDelegate(
            IntPtr colorPtr,
            IntPtr depthPtr,
            IntPtr mvPtr,
            IntPtr outputPtr,
            int renderW,
            int renderH,
            int outW,
            int outH,
            float jitterX,
            float jitterY,
            float mvScaleX,
            float mvScaleY,
            int qualityValue,
            float sharpness,
            int reset,
            int isHdr,
            int depthInverted);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetRenderEventFuncDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetStatusDelegate(out int created, out int evalCount, out int lastError);

        private static InitLoggingDelegate _initLogging;
        private static SetFrameDataDelegate _setFrameData;
        private static GetRenderEventFuncDelegate _getRenderEventFunc;
        private static GetStatusDelegate _getStatus;

        public static bool Loaded { get; private set; }
        public static IntPtr RenderEventFunc { get; private set; }

        public static void FSR4_SetFrameData(
            IntPtr colorPtr,
            IntPtr depthPtr,
            IntPtr mvPtr,
            IntPtr outputPtr,
            int renderW,
            int renderH,
            int outW,
            int outH,
            float jitterX,
            float jitterY,
            float mvScaleX,
            float mvScaleY,
            int qualityValue,
            float sharpness,
            int reset,
            int isHdr,
            int depthInverted)
        {
            if (_setFrameData != null)
            {
                _setFrameData(colorPtr, depthPtr, mvPtr, outputPtr,
                    renderW, renderH, outW, outH,
                    jitterX, jitterY, mvScaleX, mvScaleY,
                    qualityValue, sharpness, reset, isHdr, depthInverted);
            }
        }

        public static void FSR4_GetStatus(out int created, out int evalCount, out int lastError)
        {
            if (_getStatus != null)
            {
                _getStatus(out created, out evalCount, out lastError);
            }
            else
            {
                created = 0;
                evalCount = 0;
                lastError = -1;
            }
        }

        private static T BindExport<T>(IntPtr mod, string name, ManualLogSource log) where T : class
        {
            IntPtr addr = GetProcAddress(mod, name);
            if (addr == IntPtr.Zero)
            {
                log.LogError("GetProcAddress failed for export: " + name);
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer(addr, typeof(T)) as T;
        }

        public static bool Initialize(string pluginDir, string gameRoot, ManualLogSource log)
        {
            if (Loaded) return true;

            string nativePath = Path.Combine(pluginDir, "TheForestFSR4Native.dll");
            if (!File.Exists(nativePath))
            {
                log.LogError("Native bridge DLL not found at: " + nativePath);
                return false;
            }

            IntPtr mod = LoadLibraryW(nativePath);
            if (mod == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                log.LogError("LoadLibraryW failed for " + nativePath + " (Win32 error " + err + ")");
                return false;
            }

            _initLogging = BindExport<InitLoggingDelegate>(mod, "FSR4_InitLogging", log);
            _setFrameData = BindExport<SetFrameDataDelegate>(mod, "FSR4_SetFrameData", log);
            _getRenderEventFunc = BindExport<GetRenderEventFuncDelegate>(mod, "FSR4_GetRenderEventFunc", log);
            _getStatus = BindExport<GetStatusDelegate>(mod, "FSR4_GetStatus", log);

            if (_initLogging == null || _setFrameData == null || _getRenderEventFunc == null || _getStatus == null)
            {
                return false;
            }

            string logPath = Path.Combine(gameRoot, "TheForestFSR4Native.log");
            _initLogging(logPath);
            RenderEventFunc = _getRenderEventFunc();
            Loaded = (RenderEventFunc != IntPtr.Zero);
            log.LogInfo("Loaded TheForestFSR4Native.dll via GetProcAddress (eventFunc=0x" + RenderEventFunc.ToInt64().ToString("X") + ")");
            return Loaded;
        }
    }

    internal class FSR4DisplayBlit : MonoBehaviour
    {
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (TheForestFSR4Plugin.Instance == null)
            {
                Graphics.Blit(src, dst);
                return;
            }
            TheForestFSR4Plugin.Instance.BlitFinalToDestination(src, dst);
        }
    }

    internal class FSR4FallbackCameraHook : MonoBehaviour
    {
        private Camera _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
        }

        private void OnPreCull()
        {
            if (TheForestFSR4Plugin.Instance == null || _cam == null) return;
            TheForestFSR4Plugin.Instance.HandleFallbackPreCull(_cam);
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (TheForestFSR4Plugin.Instance == null || _cam == null)
            {
                Graphics.Blit(src, dst);
                return;
            }
            TheForestFSR4Plugin.Instance.HandleFallbackRenderImage(_cam, src, dst);
        }

        private void OnPostRender()
        {
            if (TheForestFSR4Plugin.Instance == null || _cam == null) return;
            TheForestFSR4Plugin.Instance.HandlePostRender(_cam);
        }
    }

    [BepInPlugin("com.theforest.fsr4", "The Forest FSR 4", "1.0.0")]
    public class TheForestFSR4Plugin : BaseUnityPlugin
    {
        public static TheForestFSR4Plugin Instance { get; private set; }

        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<Fsr4QualityMode> _cfgQuality;
        private ConfigEntry<float> _cfgSharpness;
        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<KeyCode> _cfgCycleKey;
        private ConfigEntry<bool> _cfgShowToast;
        private ConfigEntry<bool> _cfgDiagLog;
        private ConfigEntry<bool> _cfgFlipMvY;
        private ConfigEntry<bool> _cfgFlipJitterY;

        private Harmony _harmony;
        private bool _nativeReady;

        // Render targets
        private RenderTexture _lowResRT;
        private RenderTexture _colorInRT;
        private RenderTexture _depthRT;
        private RenderTexture _mvRT;
        private RenderTexture _upscaledRT;
        private RenderTexture _finalDisplayRT;

        private int _allocRenderW;
        private int _allocRenderH;
        private int _allocDisplayW;
        private int _allocDisplayH;

        // Active camera & capture state
        private Camera _activeMainCam;
        private Camera _blitCam;
        private CommandBuffer _captureCb;
        private Camera _captureCbCam;

        // Per-frame state
        private bool _framePrepared;
        private bool _hasOutputThisFrame;
        private bool _suppressUnityAA;
        private bool _forceResetNextFrame = true;
        private int _jitterIndex;
        private float _frameJitterX;
        private float _frameJitterY;
        private Vector3 _lastCamPos;
        private Quaternion _lastCamRot;
        private bool _hasLastCamPose;
        private int _frameCounter;
        private bool _diagSampled;
        private bool _loggedPostEffectsAfterPP;
        private bool _loggedCaptureSource;
        private bool _loggedPreCullOk;
        private bool _loggedJitterOk;
        private bool _loggedRenderImgEntered;
        private bool _loggedPostRenderOk;
        private bool _loggedBlitOk;
        private bool _useLowResBlitThisFrame;
        private int _abFrameCount;
        private bool _abCapturedOn;
        private bool _abCapturedOff;
        private bool _abForceOff;
        private bool _abLoggedFsrRgb;
        private bool _abLoggedStockRgb;
        private bool _loggedStockSrgb;
        private bool _loggedFsrSrgb;
        private int _errorLogCount;
        private readonly Dictionary<Type, bool> _onRenderImageTypeCache = new Dictionary<Type, bool>();

        private bool TypeHasOnRenderImage(Type t)
        {
            bool result;
            if (_onRenderImageTypeCache.TryGetValue(t, out result)) return result;
            MethodInfo m = t.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            result = (m != null);
            _onRenderImageTypeCache[t] = result;
            return result;
        }

        private bool HasEnabledImageEffectAfter(Camera cam, PostProcessingBehaviour ppb)
        {
            if (cam == null || ppb == null) return false;
            MonoBehaviour[] mbs = cam.GetComponents<MonoBehaviour>();
            bool passed = false;
            for (int i = 0; i < mbs.Length; i++)
            {
                MonoBehaviour mb = mbs[i];
                if (mb == null) continue;
                if (mb == ppb)
                {
                    passed = true;
                    continue;
                }
                if (passed && mb.enabled && TypeHasOnRenderImage(mb.GetType()))
                {
                    return true;
                }
            }
            return false;
        }

        private void LogErrOnce(string where, Exception ex)
        {
            if (_errorLogCount < 10)
            {
                _errorLogCount++;
                Logger.LogError("[" + where + "] " + ex);
            }
        }

        // Toast UI
        private string _toastText = "";
        private float _toastTimer;
        private GUIStyle _toastStyle;

        // Tracked cameras for diagnostics
        private readonly HashSet<int> _loggedCamIds = new HashSet<int>();
        private float _sceneScanTimer;

        private void Awake()
        {
            try
            {
                Instance = this;

                _cfgEnabled = Config.Bind("General", "Enabled", true, "Enable AMD FSR 4 upscaling via OptiScaler.");
                _cfgQuality = Config.Bind("General", "QualityMode", Fsr4QualityMode.Quality,
                    "FSR 4 preset: NativeAA (1.0x), UltraQuality (1.3x), Quality (1.5x), Balanced (1.7x), Performance (2.0x), UltraPerformance (3.0x).");
                _cfgSharpness = Config.Bind("General", "Sharpness", 0.35f,
                    new ConfigDescription("RCAS / FSR sharpness (0.0 to 1.0).", new AcceptableValueRange<float>(0.0f, 1.0f)));
                _cfgToggleKey = Config.Bind("Hotkeys", "ToggleFSR4", KeyCode.F9, "Toggle FSR 4 on/off live.");
                _cfgCycleKey = Config.Bind("Hotkeys", "CycleQuality", KeyCode.F10, "Cycle FSR 4 quality presets live.");
                _cfgShowToast = Config.Bind("UI", "ShowStatusToast", true, "Show brief on-screen notification when toggling or changing FSR 4 mode.");
                _cfgDiagLog = Config.Bind("Diagnostics", "VerboseLogging", true, "Log camera pipeline and initial FSR 4 verification details.");
                _cfgFlipMvY = Config.Bind("Advanced", "FlipMotionVectorY", false, "Flip Y sign on motion vectors if needed.");
                _cfgFlipJitterY = Config.Bind("Advanced", "FlipJitterY", false, "Flip Y sign on jitter offset if needed.");

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                Logger.LogInfo("TheForestFSR4 starting. Unity=" + Application.unityVersion
                    + " ColorSpace=" + QualitySettings.activeColorSpace
                    + " ReversedZ=" + SystemInfo.usesReversedZBuffer
                    + " Screen=" + Screen.width + "x" + Screen.height
                    + " Gfx=" + SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceName + ")");

                _nativeReady = NativeBridge.Initialize(pluginDir, gameRoot, Logger);

                _harmony = new Harmony("com.theforest.fsr4");
                ApplyHarmonyPatches();

                SceneManager.sceneLoaded += OnSceneLoaded;
                ShowToast("FSR 4 " + (_cfgEnabled.Value ? _cfgQuality.Value.ToString() : "OFF"));
            }
            catch (Exception ex)
            {
                Logger.LogError("TheForestFSR4 Awake exception: " + ex);
            }
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                Type ppbType = typeof(PostProcessingBehaviour);
                MethodInfo mPreCull = AccessTools.Method(ppbType, "OnPreCull");
                MethodInfo mRenderImg = AccessTools.Method(ppbType, "OnRenderImage");
                MethodInfo mPostRender = AccessTools.Method(ppbType, "OnPostRender");

                if (mPreCull != null)
                {
                    _harmony.Patch(mPreCull,
                        prefix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "PPB_OnPreCull_Prefix"),
                        postfix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "PPB_OnPreCull_Postfix"));
                }
                if (mRenderImg != null)
                {
                    _harmony.Patch(mRenderImg,
                        prefix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "PPB_OnRenderImage_Prefix"),
                        postfix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "PPB_OnRenderImage_Postfix"));
                }
                if (mPostRender != null)
                {
                    _harmony.Patch(mPostRender,
                        postfix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "PPB_OnPostRender_Postfix"));
                }

                MethodInfo mTaaActive = AccessTools.PropertyGetter(typeof(TaaComponent), "active");
                if (mTaaActive != null)
                {
                    _harmony.Patch(mTaaActive,
                        prefix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "AA_GetActive_Prefix"));
                }

                MethodInfo mFxaaActive = AccessTools.PropertyGetter(typeof(FxaaComponent), "active");
                if (mFxaaActive != null)
                {
                    _harmony.Patch(mFxaaActive,
                        prefix: new HarmonyMethod(typeof(TheForestFSR4Plugin), "AA_GetActive_Prefix"));
                }

                Logger.LogInfo("Harmony patches applied cleanly to PostProcessingBehaviour, TaaComponent, FxaaComponent.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to apply Harmony patches: " + ex);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("Scene loaded: " + scene.name + " (mode=" + mode + ")");
            _forceResetNextFrame = true;
            _hasLastCamPose = false;
            _loggedCamIds.Clear();
            _sceneScanTimer = 0.25f;
        }

        private void Update()
        {
            _hasOutputThisFrame = false;
            RestoreCameraTarget();

            if (_toastTimer > 0f)
            {
                _toastTimer -= Time.unscaledDeltaTime;
            }

            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                _cfgEnabled.Value = !_cfgEnabled.Value;
                _forceResetNextFrame = true;
                if (!_cfgEnabled.Value)
                {
                    RestoreCameraTarget();
                    if (_blitCam != null) _blitCam.enabled = false;
                }
                string msg = "FSR 4: " + (_cfgEnabled.Value ? ("ON (" + _cfgQuality.Value + ")") : "OFF");
                Logger.LogInfo(msg);
                ShowToast(msg);
            }

            if (Input.GetKeyDown(_cfgCycleKey.Value))
            {
                int next = (((int)_cfgQuality.Value) + 1) % 6;
                _cfgQuality.Value = (Fsr4QualityMode)next;
                _forceResetNextFrame = true;
                float s = GetScaleFactor(_cfgQuality.Value);
                int rw = Mathf.Max(64, (Mathf.RoundToInt(Screen.width / s) / 2) * 2);
                int rh = Mathf.Max(64, (Mathf.RoundToInt(Screen.height / s) / 2) * 2);
                string msg = string.Format("FSR 4: {0} ({1}x{2} -> {3}x{4})", _cfgQuality.Value, rw, rh, Screen.width, Screen.height);
                Logger.LogInfo(msg);
                ShowToast(msg);
            }

            _sceneScanTimer -= Time.unscaledDeltaTime;
            if (_sceneScanTimer <= 0f)
            {
                _sceneScanTimer = 1.5f;
                ScanCamerasForDiagnosticsAndFallback();
            }

            string abDir = Environment.GetEnvironmentVariable("FSR4_AB_TEST_DIR");
            if (!string.IsNullOrEmpty(abDir) && _activeMainCam != null)
            {
                _abFrameCount++;
                if (!_abCapturedOn && _abFrameCount == 80)
                {
                    _abCapturedOn = true;
                    Application.CaptureScreenshot(Path.Combine(abDir, "ab_fsr4_on.png"));
                    Logger.LogInfo("AB Test: captured ab_fsr4_on.png at frame " + _abFrameCount + "; switching FSR 4 OFF.");
                }
                else if (_abCapturedOn && !_abCapturedOff && _abFrameCount == 95)
                {
                    _abForceOff = true;
                    RestoreCameraTarget();
                    if (_blitCam != null) _blitCam.enabled = false;
                }
                else if (_abCapturedOn && !_abCapturedOff && _abFrameCount == 140)
                {
                    _abCapturedOff = true;
                    Application.CaptureScreenshot(Path.Combine(abDir, "ab_fsr4_off.png"));
                    Logger.LogInfo("AB Test: captured ab_fsr4_off.png at frame " + _abFrameCount);
                }
            }
        }

        private void ScanCamerasForDiagnosticsAndFallback()
        {
            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null || c == _blitCam) continue;

                int id = c.GetInstanceID();
                if (_cfgDiagLog.Value && !_loggedCamIds.Contains(id))
                {
                    _loggedCamIds.Add(id);
                    MonoBehaviour[] mbs = c.GetComponents<MonoBehaviour>();
                    List<string> compNames = new List<string>();
                    for (int j = 0; j < mbs.Length; j++)
                    {
                        if (mbs[j] != null)
                        {
                            compNames.Add(mbs[j].GetType().Name + (mbs[j].enabled ? "" : "(off)"));
                        }
                    }
                    Logger.LogInfo(string.Format(
                        "Camera[{0}] tag={1} depth={2} hdr={3} clear={4} ortho={5} path={6} dtm={7} rt={8} comps=[{9}]",
                        c.name, c.tag, c.depth, c.allowHDR, c.clearFlags, c.orthographic, c.actualRenderingPath,
                        c.depthTextureMode, c.targetTexture != null ? c.targetTexture.name : "null",
                        string.Join(", ", compNames.ToArray())));
                }

                // If main camera has no enabled PostProcessingBehaviour, attach fallback hook
                if (IsCandidateMainCamera(c))
                {
                    PostProcessingBehaviour ppb = c.GetComponent<PostProcessingBehaviour>();
                    bool hasActivePpb = (ppb != null && ppb.enabled && ppb.profile != null);
                    FSR4FallbackCameraHook fb = c.GetComponent<FSR4FallbackCameraHook>();
                    if (!hasActivePpb)
                    {
                        if (fb == null)
                        {
                            fb = c.gameObject.AddComponent<FSR4FallbackCameraHook>();
                            Logger.LogInfo("Attached FSR4FallbackCameraHook to camera: " + c.name);
                        }
                        if (!fb.enabled) fb.enabled = true;
                    }
                    else if (fb != null && fb.enabled)
                    {
                        fb.enabled = false;
                    }
                }
            }
        }

        private bool IsCandidateMainCamera(Camera cam)
        {
            if (cam == null || !cam.enabled || cam.orthographic || cam == _blitCam) return false;
            if (cam.targetTexture != null && cam.targetTexture != _lowResRT) return false;

            string name = cam.name ?? "";
            if (name.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Gui", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Reflect", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (cam.GetComponent<ImageEffectOptimizer>() != null) return true;
            if (cam.CompareTag("MainCamera")) return true;
            return false;
        }

        private static float GetScaleFactor(Fsr4QualityMode mode)
        {
            switch (mode)
            {
                case Fsr4QualityMode.NativeAA: return 1.0f;
                case Fsr4QualityMode.UltraQuality: return 1.3f;
                case Fsr4QualityMode.Quality: return 1.5f;
                case Fsr4QualityMode.Balanced: return 1.7f;
                case Fsr4QualityMode.Performance: return 2.0f;
                case Fsr4QualityMode.UltraPerformance: return 3.0f;
                default: return 1.5f;
            }
        }

        private static int GetNgxQualityValue(Fsr4QualityMode mode)
        {
            // NVSDK_NGX_PerfQuality_Value enum:
            // 0=MaxPerf, 1=Balanced, 2=MaxQuality, 3=UltraPerformance, 4=UltraQuality, 5=DLAA
            switch (mode)
            {
                case Fsr4QualityMode.NativeAA: return 5;
                case Fsr4QualityMode.UltraQuality: return 4;
                case Fsr4QualityMode.Quality: return 2;
                case Fsr4QualityMode.Balanced: return 1;
                case Fsr4QualityMode.Performance: return 0;
                case Fsr4QualityMode.UltraPerformance: return 3;
                default: return 2;
            }
        }

        private static float Halton(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;
            int i = index;
            while (i > 0)
            {
                result += (i % radix) * fraction;
                i /= radix;
                fraction /= radix;
            }
            return result;
        }

        private bool EnsureResources(Camera cam)
        {
            int displayW = Screen.width;
            int displayH = Screen.height;
            if (displayW < 64 || displayH < 64) return false;

            float scale = GetScaleFactor(_cfgQuality.Value);
            int renderW = Mathf.Max(64, (Mathf.RoundToInt(displayW / scale) / 2) * 2);
            int renderH = Mathf.Max(64, (Mathf.RoundToInt(displayH / scale) / 2) * 2);

            if (_lowResRT != null &&
                _allocRenderW == renderW && _allocRenderH == renderH &&
                _allocDisplayW == displayW && _allocDisplayH == displayH)
            {
                return true;
            }

            ReleaseRenderTargets();

            _lowResRT = new RenderTexture(renderW, renderH, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _lowResRT.name = "FSR4_LowResScene";
            _lowResRT.antiAliasing = 1;
            _lowResRT.filterMode = FilterMode.Bilinear;
            _lowResRT.Create();

            _colorInRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _colorInRT.name = "FSR4_ColorIn";
            _colorInRT.antiAliasing = 1;
            _colorInRT.filterMode = FilterMode.Point;
            _colorInRT.Create();

            _depthRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            _depthRT.name = "FSR4_Depth";
            _depthRT.antiAliasing = 1;
            _depthRT.filterMode = FilterMode.Point;
            _depthRT.Create();

            _mvRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            _mvRT.name = "FSR4_MotionVectors";
            _mvRT.antiAliasing = 1;
            _mvRT.filterMode = FilterMode.Point;
            _mvRT.Create();

            _upscaledRT = new RenderTexture(displayW, displayH, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _upscaledRT.name = "FSR4_UpscaledHDR";
            _upscaledRT.antiAliasing = 1;
            _upscaledRT.enableRandomWrite = true;
            _upscaledRT.filterMode = FilterMode.Bilinear;
            _upscaledRT.Create();

            // Keep final display RT in Linear ARGBHalf so PostProcessingBehaviour writes exact linear HDR/tonemapped
            // values with zero premature 8-bit quantization or double sRGB conversion; BlitFinalToBackbuffer applies
            // the single hardware Linear->sRGB encode when blitting to the swapchain backbuffer.
            _finalDisplayRT = new RenderTexture(displayW, displayH, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _finalDisplayRT.name = "FSR4_FinalDisplay";
            _finalDisplayRT.antiAliasing = 1;
            _finalDisplayRT.filterMode = FilterMode.Bilinear;
            _finalDisplayRT.Create();

            _allocRenderW = renderW;
            _allocRenderH = renderH;
            _allocDisplayW = displayW;
            _allocDisplayH = displayH;
            _forceResetNextFrame = true;
            _diagSampled = false;

            Logger.LogInfo(string.Format("Allocated FSR 4 RTs: render={0}x{1} -> display={2}x{3} (mode={4})",
                renderW, renderH, displayW, displayH, _cfgQuality.Value));

            return true;
        }

        private void RebuildCaptureCommandBuffer(Camera cam)
        {
            DetachCaptureCommandBuffer();
            if (cam == null || _mvRT == null || _depthRT == null) return;

            _captureCb = new CommandBuffer();
            _captureCb.name = "TheForestFSR4_Capture";
            _captureCb.Blit(BuiltinRenderTextureType.MotionVectors, new RenderTargetIdentifier(_mvRT));
            _captureCb.Blit(BuiltinRenderTextureType.Depth, new RenderTargetIdentifier(_depthRT));
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, _captureCb);
            _captureCbCam = cam;
        }

        private void DetachCaptureCommandBuffer()
        {
            if (_captureCb != null)
            {
                if (_captureCbCam != null)
                {
                    _captureCbCam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _captureCb);
                }
                _captureCb.Release();
                _captureCb = null;
                _captureCbCam = null;
            }
        }

        private void EnsureBlitCamera(Camera mainCam)
        {
            if (_blitCam == null)
            {
                GameObject go = new GameObject("TheForestFSR4_DisplayBlit");
                DontDestroyOnLoad(go);
                _blitCam = go.AddComponent<Camera>();
                _blitCam.cullingMask = 0;
                _blitCam.clearFlags = CameraClearFlags.Nothing;
                _blitCam.targetTexture = null;
                _blitCam.useOcclusionCulling = false;
                _blitCam.allowMSAA = false;
                go.AddComponent<FSR4DisplayBlit>();
            }
            _blitCam.allowHDR = mainCam.allowHDR;
            _blitCam.enabled = true;
            _blitCam.depth = mainCam.depth + 0.01f;
        }

        private void PrepareCameraPreCull(Camera cam)
        {
            if (!_cfgEnabled.Value || _abForceOff || !_nativeReady) return;
            if (!IsCandidateMainCamera(cam)) return;
            if (!EnsureResources(cam)) return;

            if (_activeMainCam != cam)
            {
                RestoreCameraTarget();
                _activeMainCam = cam;
                _forceResetNextFrame = true;
                _hasLastCamPose = false;
            }

            cam.targetTexture = _lowResRT;
            cam.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            EnsureBlitCamera(cam);
            _framePrepared = true;
            if (!_loggedPreCullOk)
            {
                _loggedPreCullOk = true;
                Logger.LogInfo("Milestone: PrepareCameraPreCull OK on " + cam.name + " (rt=" + _lowResRT.width + "x" + _lowResRT.height + ", blitDepth=" + _blitCam.depth + ")");
            }
        }

        private void ApplyFsr4Jitter(Camera cam)
        {
            if (!_framePrepared || cam != _activeMainCam) return;

            cam.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            float ratio = (float)_allocDisplayW / (float)_allocRenderW;
            int phaseCount = Mathf.Max(8, Mathf.CeilToInt(8.0f * ratio * ratio));
            int sampleIndex = (_jitterIndex % phaseCount) + 1;
            _jitterIndex++;

            float jx = Halton(sampleIndex, 2) - 0.5f;
            float jy = Halton(sampleIndex, 3) - 0.5f;

            cam.ResetProjectionMatrix();
            Matrix4x4 baseProj = cam.projectionMatrix;
            cam.nonJitteredProjectionMatrix = baseProj;

            Matrix4x4 jitterProj = baseProj;
            jitterProj.m02 += (2.0f * jx) / (float)_allocRenderW;
            jitterProj.m12 += (2.0f * jy) / (float)_allocRenderH;
            cam.projectionMatrix = jitterProj;
            cam.useJitteredProjectionMatrixForTransparentRendering = true;

            _frameJitterX = -jx;
            _frameJitterY = _cfgFlipJitterY.Value ? jy : -jy;
            if (!_loggedJitterOk)
            {
                _loggedJitterOk = true;
                Logger.LogInfo("Milestone: ApplyFsr4Jitter OK (phaseCount=" + phaseCount + ", jx=" + jx.ToString("F4") + ", jy=" + jy.ToString("F4") + ")");
            }
        }

        private bool DispatchFsr4(Camera cam, RenderTexture source)
        {
            if (!_framePrepared || cam != _activeMainCam || source == null) return false;

            // Copy scene linear HDR color into persistent RT
            Graphics.Blit(source, _colorInRT);

            // Prefer Shader global textures if populated; CommandBuffer already captured fallback into _depthRT/_mvRT
            Texture globalDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Texture globalMv = Shader.GetGlobalTexture("_CameraMotionVectorsTexture");

            if (!_loggedCaptureSource)
            {
                _loggedCaptureSource = true;
                Logger.LogInfo(string.Format(
                    "Capture sources: globalDepth={0} ({1}) globalMv={2} ({3}) source={4}x{5} fmt={6}",
                    globalDepth != null ? globalDepth.name : "null",
                    globalDepth != null ? (globalDepth.width + "x" + globalDepth.height) : "-",
                    globalMv != null ? globalMv.name : "null",
                    globalMv != null ? (globalMv.width + "x" + globalMv.height) : "-",
                    source.width, source.height, source.format));
            }

            if (globalDepth != null)
            {
                Graphics.Blit(globalDepth, _depthRT);
            }
            else if (_captureCbCam != cam)
            {
                RebuildCaptureCommandBuffer(cam);
            }
            if (globalMv != null)
            {
                Graphics.Blit(globalMv, _mvRT);
            }
            else if (_captureCbCam != cam)
            {
                RebuildCaptureCommandBuffer(cam);
            }

            // Detect camera cut/teleport for temporal history reset
            bool reset = _forceResetNextFrame;
            Vector3 pos = cam.transform.position;
            Quaternion rot = cam.transform.rotation;
            if (_hasLastCamPose)
            {
                if ((pos - _lastCamPos).sqrMagnitude > 225.0f || Quaternion.Angle(rot, _lastCamRot) > 45.0f)
                {
                    reset = true;
                }
            }
            else
            {
                reset = true;
            }
            _lastCamPos = pos;
            _lastCamRot = rot;
            _hasLastCamPose = true;
            _forceResetNextFrame = false;

            float mvScaleX = -(float)_allocRenderW;
            float mvScaleY = _cfgFlipMvY.Value ? (float)_allocRenderH : -(float)_allocRenderH;
            int isHdr = (cam.allowHDR || source.format == RenderTextureFormat.ARGBHalf || source.format == RenderTextureFormat.ARGBFloat) ? 1 : 0;
            int depthInv = SystemInfo.usesReversedZBuffer ? 1 : 0;

            NativeBridge.FSR4_SetFrameData(
                _colorInRT.GetNativeTexturePtr(),
                _depthRT.GetNativeTexturePtr(),
                _mvRT.GetNativeTexturePtr(),
                _upscaledRT.GetNativeTexturePtr(),
                _allocRenderW,
                _allocRenderH,
                _allocDisplayW,
                _allocDisplayH,
                _frameJitterX,
                _frameJitterY,
                mvScaleX,
                mvScaleY,
                GetNgxQualityValue(_cfgQuality.Value),
                _cfgSharpness.Value,
                reset ? 1 : 0,
                isHdr,
                depthInv);

            GL.IssuePluginEvent(NativeBridge.RenderEventFunc, 1);
            GL.InvalidateState();

            _frameCounter++;

            int created, evalCount, lastErr;
            NativeBridge.FSR4_GetStatus(out created, out evalCount, out lastErr);

            // Optional diagnostic pixel check only when FSR4_DIAG_DUMP_DIR is explicitly set
            if (_cfgDiagLog.Value && !_diagSampled && _frameCounter == 45)
            {
                _diagSampled = true;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSR4_DIAG_DUMP_DIR")))
                {
                    LogDiagnosticSample(created, evalCount, lastErr);
                }
                else
                {
                    Logger.LogInfo(string.Format(
                        "FSR 4 active: created={0} evalCount={1} lastErr=0x{2:X} ({3}x{4} -> {5}x{6}, {7})",
                        created, evalCount, lastErr, _allocRenderW, _allocRenderH, _allocDisplayW, _allocDisplayH, _cfgQuality.Value));
                }
            }

            // If native evaluation failed before succeeding even once, fallback safely to bilinear copy
            if (evalCount == 0 && lastErr != 0 && lastErr != 1)
            {
                Graphics.Blit(source, _upscaledRT);
            }

            return true;
        }

        private void LogDiagnosticSample(int created, int evalCount, int lastErr)
        {
            try
            {
                float dMin, dMax, mvMaxAbs, inAvg, outAvg;
                SampleGridStats(_depthRT, out dMin, out dMax, out inAvg);
                float mvMin, mvMax;
                SampleGridStats(_mvRT, out mvMin, out mvMax, out mvMaxAbs);
                float cMin, cMax;
                SampleGridStats(_colorInRT, out cMin, out cMax, out inAvg);
                float uMin, uMax;
                SampleGridStats(_upscaledRT, out uMin, out uMax, out outAvg);
                Logger.LogInfo(string.Format(
                    "FSR4 Frame 45 Diag: created={0} evalCount={1} lastErr=0x{2:X} depth=[{3:F5}..{4:F5}] mv=[{5:F5}..{6:F5}] colorInAvg={7:F4} fsr4OutAvg={8:F4}",
                    created, evalCount, lastErr, dMin, dMax, mvMin, mvMax, inAvg, outAvg));

                string dumpDir = Environment.GetEnvironmentVariable("FSR4_DIAG_DUMP_DIR");
                if (!string.IsNullOrEmpty(dumpDir) && Directory.Exists(dumpDir))
                {
                    DumpRtToPng(_colorInRT, Path.Combine(dumpDir, "diag_color_720p.png"), true);
                    DumpRtToPng(_upscaledRT, Path.Combine(dumpDir, "diag_fsr4_1080p.png"), true);
                    DumpRtToPng(_finalDisplayRT, Path.Combine(dumpDir, "diag_final_1080p.png"), false);
                    Logger.LogInfo("Saved diagnostic PNGs to " + dumpDir);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Diag sample failed: " + ex.Message);
            }
        }

        private static void SampleGridStats(RenderTexture rt, out float minVal, out float maxVal, out float avgVal)
        {
            minVal = float.MaxValue;
            maxVal = float.MinValue;
            double sum = 0.0;
            RenderTexture prev = RenderTexture.active;
            RenderTexture tmp = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            Texture2D tex = new Texture2D(16, 16, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
            tex.Apply(false, false);
            Color[] px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                float v = px[i].r;
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
                sum += Math.Abs(v);
            }
            avgVal = (float)(sum / px.Length);
            UnityEngine.Object.Destroy(tex);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }

        private static void DumpRtToPng(RenderTexture rt, string path, bool linearToSrgb)
        {
            if (rt == null) return;
            RenderTexture prev = RenderTexture.active;
            RenderTexture tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32,
                linearToSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Default);
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false, false);
            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes(path, png);
            UnityEngine.Object.Destroy(tex);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }

        private static Vector3 SampleMeanRGB(RenderTexture rt)
        {
            if (rt == null) return Vector3.zero;
            RenderTexture prev = RenderTexture.active;
            RenderTexture tmp = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            Texture2D tex = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 32, 32), 0, 0, false);
            tex.Apply(false, false);
            Color[] px = tex.GetPixels();
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i++)
            {
                r += px[i].r;
                g += px[i].g;
                b += px[i].b;
            }
            UnityEngine.Object.Destroy(tex);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return new Vector3((float)(r / px.Length), (float)(g / px.Length), (float)(b / px.Length));
        }

        public void HandleFallbackPreCull(Camera cam)
        {
            PrepareCameraPreCull(cam);
            ApplyFsr4Jitter(cam);
        }

        public void HandleFallbackRenderImage(Camera cam, RenderTexture src, RenderTexture dst)
        {
            if (_framePrepared && cam == _activeMainCam && DispatchFsr4(cam, src))
            {
                Graphics.Blit(_upscaledRT, _finalDisplayRT);
                _hasOutputThisFrame = true;
                _framePrepared = false;
                if (dst != null && dst != _lowResRT)
                {
                    Graphics.Blit(_finalDisplayRT, dst);
                }
            }
            else
            {
                Graphics.Blit(src, dst);
            }
        }

        public void HandlePostRender(Camera cam)
        {
            // NOTE: In Unity, OnPostRender runs BEFORE OnRenderImage!
            // Only reset the jittered projection matrix here; keep _framePrepared and targetTexture
            // alive through OnRenderImage, then restore targetTexture in BlitFinalToBackbuffer / Update.
            if (cam != null && cam == _activeMainCam)
            {
                cam.ResetProjectionMatrix();
                if (!_loggedPostRenderOk)
                {
                    _loggedPostRenderOk = true;
                    Logger.LogInfo("Milestone: HandlePostRender OK on " + cam.name);
                }
            }
        }

        public void BlitFinalToDestination(RenderTexture camSrc, RenderTexture dst)
        {
            try
            {
                RestoreCameraTarget();
                if (!_cfgEnabled.Value || _abForceOff || !_hasOutputThisFrame)
                {
                    Graphics.Blit(camSrc, dst);
                    return;
                }
                RenderTexture src = (_useLowResBlitThisFrame && _lowResRT != null) ? _lowResRT : _finalDisplayRT;
                if (src == null)
                {
                    Graphics.Blit(camSrc, dst);
                    return;
                }
                bool prevSrgb = GL.sRGBWrite;
                // Unity's OnRenderImage to dst==null already handles sRGB write when Linear color space is active.
                Graphics.Blit(src, dst);
                if (!_loggedBlitOk)
                {
                    _loggedBlitOk = true;
                    Logger.LogInfo(string.Format("Milestone: BlitFinalToDestination OK ({0}x{1}, fmt={2}, sRGB={3}, dst={4}, GL.sRGBWrite={5})",
                        src.width, src.height, src.format, src.sRGB,
                        dst != null ? (dst.name + " " + dst.format + " sRGB=" + dst.sRGB) : "null(backbuffer)", prevSrgb));
                }
            }
            catch (Exception ex)
            {
                LogErrOnce("BlitFinalToDestination", ex);
                Graphics.Blit(camSrc, dst);
            }
        }

        private void RestoreCameraTarget()
        {
            if (_activeMainCam != null)
            {
                _activeMainCam.ResetProjectionMatrix();
                if (_activeMainCam.targetTexture == _lowResRT)
                {
                    _activeMainCam.targetTexture = null;
                }
            }
            _framePrepared = false;
        }

        private void ReleaseRenderTargets()
        {
            RestoreCameraTarget();
            DetachCaptureCommandBuffer();
            ReleaseRT(ref _lowResRT);
            ReleaseRT(ref _colorInRT);
            ReleaseRT(ref _depthRT);
            ReleaseRT(ref _mvRT);
            ReleaseRT(ref _upscaledRT);
            ReleaseRT(ref _finalDisplayRT);
            _allocRenderW = _allocRenderH = _allocDisplayW = _allocDisplayH = 0;
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                rt = null;
            }
        }

        private void ShowToast(string text)
        {
            _toastText = text;
            _toastTimer = 3.0f;
        }

        private void OnGUI()
        {
            if (!_cfgShowToast.Value || _toastTimer <= 0f || string.IsNullOrEmpty(_toastText)) return;
            if (_toastStyle == null)
            {
                _toastStyle = new GUIStyle(GUI.skin.box);
                _toastStyle.fontSize = 16;
                _toastStyle.normal.textColor = Color.white;
                _toastStyle.alignment = TextAnchor.MiddleLeft;
                _toastStyle.padding = new RectOffset(10, 10, 6, 6);
            }
            GUI.Box(new Rect(16, 16, 320, 34), _toastText, _toastStyle);
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ReleaseRenderTargets();
            if (_blitCam != null)
            {
                UnityEngine.Object.Destroy(_blitCam.gameObject);
                _blitCam = null;
            }
            if (_nativeReady && NativeBridge.RenderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(NativeBridge.RenderEventFunc, 3);
            }
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }

        // ====================================================================
        // Harmony Hooks
        // ====================================================================

        private static void PPB_OnPreCull_Prefix(PostProcessingBehaviour __instance)
        {
            if (Instance == null || __instance == null) return;
            try
            {
                Camera cam = __instance.GetComponent<Camera>();
                Instance.PrepareCameraPreCull(cam);
            }
            catch (Exception ex) { Instance.LogErrOnce("PPB_OnPreCull_Prefix", ex); }
        }

        private static void PPB_OnPreCull_Postfix(PostProcessingBehaviour __instance)
        {
            if (Instance == null || __instance == null) return;
            try
            {
                Camera cam = __instance.GetComponent<Camera>();
                Instance.ApplyFsr4Jitter(cam);
            }
            catch (Exception ex) { Instance.LogErrOnce("PPB_OnPreCull_Postfix", ex); }
        }

        private static void PPB_OnRenderImage_Prefix(
            PostProcessingBehaviour __instance,
            ref RenderTexture source,
            ref RenderTexture destination,
            out RenderTexture __state)
        {
            __state = null;
            if (Instance == null || __instance == null) return;
            try
            {
                Camera cam = __instance.GetComponent<Camera>();
                if (!Instance._loggedRenderImgEntered)
                {
                    Instance._loggedRenderImgEntered = true;
                    Instance.Logger.LogInfo("Milestone: PPB_OnRenderImage_Prefix entered cam=" + (cam != null ? cam.name : "null")
                        + " framePrepared=" + Instance._framePrepared
                        + " isMain=" + (cam == Instance._activeMainCam)
                        + " src=" + (source != null ? (source.width + "x" + source.height) : "null")
                        + " dst=" + (destination != null ? (destination.width + "x" + destination.height) : "null"));
                }
                if (!Instance._framePrepared || cam != Instance._activeMainCam)
                {
                    if (!Instance._loggedStockSrgb && cam != null && Instance.IsCandidateMainCamera(cam))
                    {
                        Instance._loggedStockSrgb = true;
                        Instance.Logger.LogInfo(string.Format("Stock PPB_OnRenderImage: GL.sRGBWrite={0} src={1}({2},sRGB={3}) dst={4}",
                            GL.sRGBWrite,
                            source != null ? (source.width + "x" + source.height) : "null",
                            source != null ? source.format.ToString() : "-",
                            source != null ? source.sRGB.ToString() : "-",
                            destination != null ? (destination.name + " " + destination.format + " sRGB=" + destination.sRGB) : "null(backbuffer)"));
                    }
                    if (!Instance._abLoggedStockRgb && Instance._abForceOff && Instance._abFrameCount >= 130 && cam != null && Instance.IsCandidateMainCamera(cam))
                    {
                        Instance._abLoggedStockRgb = true;
                        Vector3 sRgb = SampleMeanRGB(source);
                        Instance.Logger.LogInfo(string.Format("AB RGB [STOCK source]: ({0:F5}, {1:F5}, {2:F5})", sRgb.x, sRgb.y, sRgb.z));
                    }
                    return;
                }

                if (!Instance._loggedFsrSrgb)
                {
                    Instance._loggedFsrSrgb = true;
                    Instance.Logger.LogInfo(string.Format("FSR4 PPB_OnRenderImage: GL.sRGBWrite={0} src={1}({2},sRGB={3}) dst={4}",
                        GL.sRGBWrite,
                        source != null ? (source.width + "x" + source.height) : "null",
                        source != null ? source.format.ToString() : "-",
                        source != null ? source.sRGB.ToString() : "-",
                        destination != null ? (destination.name + " " + destination.format + " sRGB=" + destination.sRGB) : "null"));
                }

                if (Instance.DispatchFsr4(cam, source))
                {
                    if (!Instance._abLoggedFsrRgb && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSR4_AB_TEST_DIR")) && Instance._abFrameCount >= 75)
                    {
                        Instance._abLoggedFsrRgb = true;
                        Vector3 inRgb = SampleMeanRGB(Instance._colorInRT);
                        Vector3 upRgb = SampleMeanRGB(Instance._upscaledRT);
                        Instance.Logger.LogInfo(string.Format("AB RGB [FSR4 colorIn]: ({0:F5}, {1:F5}, {2:F5})  [FSR4 upscaled]: ({3:F5}, {4:F5}, {5:F5})",
                            inRgb.x, inRgb.y, inRgb.z, upRgb.x, upRgb.y, upRgb.z));
                    }

                    __state = destination;
                    Instance._suppressUnityAA = true;
                    bool hasPostEffect = Instance.HasEnabledImageEffectAfter(cam, __instance);
                    Instance._useLowResBlitThisFrame = hasPostEffect;
                    source = Instance._upscaledRT;
                    destination = Instance._finalDisplayRT;
                }
            }
            catch (Exception ex) { Instance.LogErrOnce("PPB_OnRenderImage_Prefix", ex); }
        }

        private static void PPB_OnRenderImage_Postfix(
            PostProcessingBehaviour __instance,
            RenderTexture __state)
        {
            if (Instance == null || __instance == null) return;
            try
            {
                if (!Instance._suppressUnityAA) return;

                Instance._suppressUnityAA = false;
                Instance._framePrepared = false;
                Instance._hasOutputThisFrame = true;

                if (Instance._useLowResBlitThisFrame && __state != null)
                {
                    if (!Instance._loggedPostEffectsAfterPP)
                    {
                        Instance._loggedPostEffectsAfterPP = true;
                        Instance.Logger.LogInfo("Enabled post-PP OnRenderImage detected; feeding output into " + __state.name);
                    }
                    Graphics.Blit(Instance._finalDisplayRT, __state);
                }
            }
            catch (Exception ex) { Instance.LogErrOnce("PPB_OnRenderImage_Postfix", ex); }
        }

        private static void PPB_OnPostRender_Postfix(PostProcessingBehaviour __instance)
        {
            if (Instance == null || __instance == null) return;
            try
            {
                Camera cam = __instance.GetComponent<Camera>();
                Instance.HandlePostRender(cam);
            }
            catch (Exception ex) { Instance.LogErrOnce("PPB_OnPostRender_Postfix", ex); }
        }

        private static bool AA_GetActive_Prefix(ref bool __result)
        {
            if (Instance != null && Instance._suppressUnityAA)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
