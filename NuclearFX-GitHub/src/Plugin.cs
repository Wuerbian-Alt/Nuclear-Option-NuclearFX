using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearFX
{
    /// <summary>
    /// 核爆视觉反馈：HUD 下线 → 噪点 → 重启。
    /// 检测点：Missile.Warhead.Detonate(position, blastYield)
    /// 强度按距离/当量衰减。
    /// </summary>
    [BepInPlugin("com.dsh.nuclearfx", "Nuclear FX", "1.0.0")]
    public class NuclearFx : BaseUnityPlugin
    {
        internal static NuclearFx Instance;
        internal static ManualLogSource Log;
        private static string Dir;

        private ConfigEntry<float> _nukeThresholdKg;
        private ConfigEntry<float> _radiusFactor;
        private ConfigEntry<float> _hudDownSeconds;
        private ConfigEntry<float> _noiseStrength;
        private ConfigEntry<float> _blackoutSeconds;
        private ConfigEntry<float> _flashRadiusFactor;
        private ConfigEntry<float> _hudRadiusFactor;
        private ConfigEntry<float> _blindAngle;
        private ConfigEntry<float> _blindExtra;
        private ConfigEntry<float> _fadeInSeconds;
        private ConfigEntry<float> _darkenSeconds;
        private ConfigEntry<float> _noiseDuration;
        private ConfigEntry<float> _jitterAmp;
        private ConfigEntry<float> _jitterSeconds;
        private ConfigEntry<int> _hudRestartFlickers;
        private ConfigEntry<float> _hudRestartOnMs;
        private ConfigEntry<float> _hudRestartOffMs;
        private ConfigEntry<bool> _debugLog;

        private GameObject _overlay;
        private RawImage _noiseImg;
        private Image _flashImg;
        private Texture2D _noiseTex;
        private Color32[] _noiseBuf;
        private float _noiseTimer;
        private Coroutine _routine;

        // HUD 抖动状态：在 LateUpdate 里应用，避免被游戏的 Update 覆盖
        private Transform _jitterTarget;
        private Vector3 _jitterBase;
        private float _jitterUntil;
        private float _jitterDuration;
        private float _jitterStrength;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            _nukeThresholdKg = Config.Bind("General", "NukeThresholdKg", 1000f,
                "低于这个当量（kg TNT）不算核弹，不触发效果");
            _radiusFactor = Config.Bind("General", "RadiusFactor", 1f,
                "影响半径系数，半径 = 13 * yield^(1/3) * 系数");
            _hudDownSeconds = Config.Bind("Effect", "HudDownSeconds", 1.5f,
                "HUD 完全下线的基准时长（秒），会按强度缩放");
            _blackoutSeconds = Config.Bind("Effect", "BlackoutSeconds", 0.5f,
                "核爆后纯黑持续时长（秒），之后开始渐亮");
            _flashRadiusFactor = Config.Bind("Effect", "FlashRadiusFactor", 0.5f,
                "黑屏范围 = 影响半径 × 该系数；超出这个范围只轻微变暗，不黑屏");
            _hudRadiusFactor = Config.Bind("Effect", "HudRadiusFactor", 0.8f,
                "HUD 下线范围 = 影响半径 × 该系数；超出这个范围 HUD 不下线，只有抖动");
            _blindAngle = Config.Bind("Effect", "BlindAngleDegrees", 35f,
                "直视判定角（度）：视线与爆心的夹角小于它算直视，会触发闪光致盲");
            _blindExtra = Config.Bind("Effect", "BlindBlackoutMultiplier", 1.8f,
                "直视致盲时黑屏时长 = BlackoutSeconds × 该倍数");

            _fadeInSeconds = Config.Bind("Effect", "FadeInSeconds", 0.35f,
                "纯黑结束后渐亮到正常的时长（秒）");
            _darkenSeconds = Config.Bind("Effect", "DarkenSeconds", 0.28f,
                "黑屏范围外、只轻微压暗时的时长（秒）");
            _noiseDuration = Config.Bind("Effect", "NoiseDuration", 1.6f,
                "噪点从最大不透明度衰减到 0 的时长（秒）");
            _jitterAmp = Config.Bind("Effect", "JitterStrength", 22f,
                "HUD 抖动最大幅度（像素）");
            _jitterSeconds = Config.Bind("Effect", "JitterSeconds", 1.4f,
                "HUD 抖动持续时长（秒）");
            _hudRestartFlickers = Config.Bind("Effect", "HudRestartFlickers", 3,
                "HUD 重启时的闪烁次数");
            _hudRestartOnMs = Config.Bind("Effect", "HudRestartOnMs", 50f,
                "闪烁时 HUD 亮起的时长（毫秒）");
            _hudRestartOffMs = Config.Bind("Effect", "HudRestartOffMs", 90f,
                "闪烁时 HUD 熄灭的时长（毫秒）");
            _noiseStrength = Config.Bind("Effect", "NoiseStrength", 0.6f,
                "噪点最大不透明度 0~1");
            _debugLog = Config.Bind("General", "DebugLog", false, "把每次核爆写进 nuclearfx.log");

            CreateOverlay();

            try
            {
                new Harmony("com.dsh.nuclearfx").PatchAll();
                Write("patched Warhead.Detonate");
            }
            catch (Exception e)
            {
                Write("patch failed: " + e.Message);
                Log.LogError(e);
            }
        }

        private void Update()
        {
            if (_overlay != null && _overlay.activeSelf) UpdateNoise();
        }

        private void LateUpdate()
        {
            if (_jitterTarget == null) return;
            float now = Time.unscaledTime;
            if (now < _jitterUntil)
            {
                float k = (_jitterUntil - now) / Mathf.Max(0.01f, _jitterDuration);
                float amp = Mathf.Max(0f, _jitterAmp.Value) * _jitterStrength * k;
                _jitterTarget.localPosition = _jitterBase + new Vector3(
                    UnityEngine.Random.Range(-amp, amp),
                    UnityEngine.Random.Range(-amp, amp),
                    0f);
            }
            else
            {
                _jitterTarget.localPosition = _jitterBase;
                _jitterTarget = null;
            }
        }

        private void StartJitter(float duration, float strength)
        {
            try
            {
                if (_jitterTarget != null)
                {
                    _jitterTarget.localPosition = _jitterBase;
                    _jitterTarget = null;
                }

                object inst = FlightHud.i;
                if (!(inst is FlightHud)) { Write("jitter: no FlightHud"); return; }
                FlightHud fh = (FlightHud)inst;

                Transform target = null;
                string which = "";

                // 优先 HUD 中心节点——HUD 元素实际挂在这里
                FieldInfo hf = typeof(FlightHud).GetField("HUDCenter",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (hf != null) target = hf.GetValue(fh) as Transform;
                if (target != null) which = "HUDCenter";

                if (target == null)
                {
                    FieldInfo cf = typeof(FlightHud).GetField("canvas",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    Canvas c = cf != null ? cf.GetValue(fh) as Canvas : null;
                    if (c != null) { target = c.transform; which = "canvas"; }
                }

                if (target == null) { target = fh.transform; which = "root"; }

                _jitterTarget = target;
                _jitterBase = target.localPosition;
                _jitterUntil = Time.unscaledTime + duration;
                _jitterDuration = duration;
                _jitterStrength = strength;
                Write("jitter start target=" + which + " dur=" + duration
                    + " strength=" + strength.ToString("0.00")
                    + " base=" + _jitterBase);
            }
            catch (Exception e) { Write("jitter failed: " + e.Message); }
        }

        private static void Write(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(Dir, "nuclearfx.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\r\n");
            }
            catch { }
        }

        /* ---------------- 触发 ---------------- */

        internal void OnExplosion(Vector3 pos, float yield)
        {
            if (yield < _nukeThresholdKg.Value) return;

            Vector3 player = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            float radius = 13f * Mathf.Pow(yield, 1f / 3f) * _radiusFactor.Value;
            float dist = Vector3.Distance(pos, player);
            if (dist > radius) return;

            float strength = Mathf.Clamp01(1f - dist / radius);
            strength = Mathf.Max(strength, 0.15f);   // 半径边缘也有一点感觉

            // 两个临界范围：黑屏范围 < HUD 范围 < 总半径
            bool doFlash = dist <= radius * _flashRadiusFactor.Value;
            bool doHud = dist <= radius * _hudRadiusFactor.Value;

            // 直视核爆 → 闪光致盲（只要在影响半径内，多远都会致盲）
            bool looking = false;
            float angle = 999f;
            if (Camera.main != null)
            {
                Vector3 toBlast = (pos - player);
                if (toBlast.sqrMagnitude > 1f)
                {
                    toBlast.Normalize();
                    float dot = Vector3.Dot(Camera.main.transform.forward, toBlast);
                    angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;
                    looking = angle <= _blindAngle.Value;
                }
            }
            bool blind = looking && dist <= radius;

            if (blind) doFlash = true;   // 直视致盲：无论距离，一律黑屏

            Write("NUKE yield=" + yield.ToString("0") + " dist=" + dist.ToString("0")
                + " radius=" + radius.ToString("0") + " strength=" + strength.ToString("0.00")
                + " angle=" + angle.ToString("0") + " looking=" + looking + " blind=" + blind
                + " flash=" + doFlash + " hud=" + doHud
                + (Camera.main == null ? " [no camera]" : ""));

            // 只在黑屏范围内才在当帧立即黑幕
            if (doFlash) BlackoutNow();

            Trigger(strength, doFlash, doHud, blind);
        }

        /// <summary>不经过协程，立刻把黑幕拉满（协程要下一帧才执行，会慢一帧导致闪白）。</summary>
        private void BlackoutNow()
        {
            try
            {
                if (_overlay == null) CreateOverlay();
                if (_overlay == null) return;
                _overlay.SetActive(true);
                if (_flashImg != null) _flashImg.color = new Color(0f, 0f, 0f, 1f);
                if (_noiseImg != null) _noiseImg.color = new Color(1f, 1f, 1f, 0f);
            }
            catch (Exception ex) { Write("blackout failed: " + ex.Message); }
        }

        private void Trigger(float strength, bool doFlash, bool doHud, bool blind)
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(Effect(strength, doFlash, doHud, blind));
        }

        private IEnumerator Effect(float s, bool doFlash, bool doHud, bool blind)
        {
            if (_overlay == null)
            {
                Write("overlay is null, rebuilding");
                try { CreateOverlay(); } catch (Exception ex) { Write("rebuild failed: " + ex); }
            }
            Write("effect start s=" + s.ToString("0.00") + " flash=" + doFlash
                + " hud=" + doHud + " blind=" + blind + " overlay=" + (_overlay != null));
            if (_overlay == null) { Write("abort: no overlay"); yield break; }
            _overlay.SetActive(true);

            float noiseMax = _noiseStrength.Value * s;
            float elapsed = 0f;

            if (doFlash)
            {
                // 1) 黑屏 + HUD 同时下线
                _flashImg.color = new Color(0f, 0f, 0f, 1f);
                if (doHud) SetHud(false);

                // 2) 保持纯黑（直视致盲更久）
                float blackHold = Mathf.Max(0f, _blackoutSeconds.Value);
                if (blind) blackHold *= Mathf.Max(1f, _blindExtra.Value);
                float t = 0f;
                while (t < blackHold)
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
                elapsed += blackHold;

                // 3) 渐亮 + 噪点淡入
                float fadeDur = Mathf.Max(0.01f, _fadeInSeconds.Value);
                t = 0f;
                while (t < fadeDur)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / fadeDur);
                    _flashImg.color = new Color(0f, 0f, 0f, 1f - k);
                    _noiseImg.color = new Color(1f, 1f, 1f, Mathf.Lerp(0f, noiseMax, k));
                    yield return null;
                }
                _flashImg.color = new Color(0f, 0f, 0f, 0f);
                elapsed += fadeDur;
            }
            else
            {
                // 黑屏范围外：只轻微压暗
                if (doHud) SetHud(false);
                float darkDur = Mathf.Max(0.01f, _darkenSeconds.Value);
                float t = 0f;
                float darkMax = 0.35f * s;
                while (t < darkDur)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / darkDur);
                    _flashImg.color = new Color(0f, 0f, 0f, Mathf.Lerp(darkMax, 0f, k));
                    _noiseImg.color = new Color(1f, 1f, 1f, Mathf.Lerp(0f, noiseMax, k));
                    yield return null;
                }
                _flashImg.color = new Color(0f, 0f, 0f, 0f);
                elapsed += darkDur;
            }

            if (doHud)
            {
                // 4) HUD 保持下线的剩余时长
                float hold = _hudDownSeconds.Value * (0.5f + 0.5f * s);
                float remaining = Mathf.Max(0f, hold - elapsed);
                float e = 0f;
                while (e < remaining)
                {
                    e += Time.unscaledDeltaTime;
                    _noiseImg.color = new Color(1f, 1f, 1f, noiseMax);
                    yield return null;
                }

                // 5) 重启：闪烁几次再稳定
                int flickers = Mathf.Clamp(_hudRestartFlickers.Value, 0, 20);
                float onSec = Mathf.Max(0.01f, _hudRestartOnMs.Value) / 1000f;
                float offSec = Mathf.Max(0.01f, _hudRestartOffMs.Value) / 1000f;
                for (int i = 0; i < flickers; i++)
                {
                    SetHud(true);
                    yield return new WaitForSecondsRealtime(onSec);
                    SetHud(false);
                    yield return new WaitForSecondsRealtime(offSec);
                }
                SetHud(true);
            }

            // 6) HUD 抖动（下线与否都抖）
            StartJitter(Mathf.Max(0f, _jitterSeconds.Value), s);

            // 7) 噪点衰减
            float fade = Mathf.Max(0.01f, _noiseDuration.Value);
            float e2 = 0f;
            while (e2 < fade)
            {
                e2 += Time.unscaledDeltaTime;
                float a = Mathf.Lerp(noiseMax, 0f, Mathf.Clamp01(e2 / fade));
                _noiseImg.color = new Color(1f, 1f, 1f, a);
                yield return null;
            }

            _noiseImg.color = new Color(1f, 1f, 1f, 0f);
            _flashImg.color = new Color(1f, 1f, 1f, 0f);
            _overlay.SetActive(false);
            _routine = null;
            Write("effect end");
        }

        private void SetHud(bool on)
        {
            try
            {
                bool hasInstance = FlightHud.i != null;
                Write("SetHud(" + on + ") FlightHud.i=" + (hasInstance ? "ok" : "NULL"));
                FlightHud.EnableCanvas(on);
            }
            catch (Exception e) { Write("EnableCanvas failed: " + e.Message); }
        }

        /* ---------------- 噪点层 ---------------- */

        private void CreateOverlay()
        {
            Write("createOverlay begin");
            _overlay = new GameObject("NuclearFX_Overlay");
            UnityEngine.Object.DontDestroyOnLoad(_overlay);

            Canvas canvas = _overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            _overlay.AddComponent<CanvasScaler>();

            // 噪点
            GameObject noise = new GameObject("Noise");
            noise.transform.SetParent(_overlay.transform, false);
            _noiseImg = noise.AddComponent<RawImage>();
            RectTransform nr = noise.GetComponent<RectTransform>();
            nr.anchorMin = Vector2.zero;
            nr.anchorMax = Vector2.one;
            nr.offsetMin = Vector2.zero;
            nr.offsetMax = Vector2.zero;
            _noiseImg.raycastTarget = false;

            _noiseTex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            _noiseTex.wrapMode = TextureWrapMode.Repeat;
            _noiseTex.filterMode = FilterMode.Point;   // 像素化，颗粒更清晰
            _noiseBuf = new Color32[256 * 256];
            _noiseImg.texture = _noiseTex;
            _noiseImg.color = new Color(1f, 1f, 1f, 0f);

            // 白闪
            GameObject flash = new GameObject("Flash");
            flash.transform.SetParent(_overlay.transform, false);
            _flashImg = flash.AddComponent<Image>();
            RectTransform fr = flash.GetComponent<RectTransform>();
            fr.anchorMin = Vector2.zero;
            fr.anchorMax = Vector2.one;
            fr.offsetMin = Vector2.zero;
            fr.offsetMax = Vector2.zero;
            _flashImg.raycastTarget = false;
            _flashImg.color = new Color(1f, 1f, 1f, 0f);

            _overlay.SetActive(false);
            Write("createOverlay ok, overlay=" + (_overlay != null));
        }

        private void UpdateNoise()
        {
            _noiseTimer += Time.unscaledDeltaTime;
            if (_noiseTimer < 0.033f) return;   // 约 30 fps
            _noiseTimer = 0f;

            for (int i = 0; i < _noiseBuf.Length; i++)
            {
                byte v = (byte)UnityEngine.Random.Range(0, 256);
                _noiseBuf[i] = new Color32(v, v, v, 255);
            }
            _noiseTex.SetPixels32(_noiseBuf);
            _noiseTex.Apply(false);
        }
    }

    [HarmonyPatch(typeof(Missile.Warhead), "Detonate")]
    internal static class WarheadDetonatePatch
    {
        // Detonate(Rigidbody rb, PersistentID ownerID, Vector3 position, Vector3 normal,
        //          bool armed, float blastYield, bool hitArmor, bool hitTerrain)
        private static void Postfix(Vector3 __2, float __5, bool __4)
        {
            try
            {
                // 注意：不能检查 __4(armed)，游戏里这个值即使爆炸了也可能为 false
                if (NuclearFx.Instance != null)
                    NuclearFx.Instance.OnExplosion(__2, __5);
            }
            catch (Exception e)
            {
                try { NuclearFx.Log.LogWarning("hook error: " + e.Message); } catch { }
            }
        }
    }
}
