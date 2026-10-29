# Nuclear FX

Nuclear blast visual effects for **Nuclear Option** — blinding blackout → fade-in → noise → HUD shutdown & restart → shake.

给《Nuclear Option》做的核爆视觉反馈 mod：**致盲黑屏 → 渐亮 → 噪点 → HUD 下线重启 → 抖动**。

> 非官方 mod，与 Nuclear Option 开发者无关。  
> Unofficial mod, not affiliated with the Nuclear Option developers.

---

## 效果 / What it does

当核弹在影响范围内引爆时：

| 距离 | 效果 |
|---|---|
| 半径 × 0.5 内 | 黑屏 → 渐亮 + 噪点 + HUD 下线重启 + 抖动 |
| 半径 × 0.5 ~ 0.8 | 轻微压暗 + 噪点 + HUD 下线重启 + 抖动 |
| 半径 × 0.8 ~ 1.0 | 轻微抖动 + 噪点 |
| **直视核爆**（半径内） | **无论多远都会黑屏致盲** |

![preview](preview.gif)

*（截图/GIF 待补充）*

---

## 安装 / Installation

### 前置 / Requirements

- **Nuclear Option** (Steam)
- **BepInEx 5.4.x (x64)** installed in the game root

### 步骤 / Steps

1. Download `NuclearFX-Package.zip` from [Releases](../../releases/latest)
2. Extract it
3. Drag the `plugins` folder into the game root:

```
.../steamapps/common/Nuclear Option/
├── NuclearOption.exe
├── winhttp.dll
└── BepInEx/
    └── plugins/
        └── NuclearFX/
            └── NuclearFX.dll
```

4. Launch the game. If it loaded correctly, `BepInEx/plugins/NuclearFX/nuclearfx.log` will contain:

```
patched Warhead.Detonate
```

---

## 配置 / Configuration

### F1 菜单（推荐）

Press **F1** in game → **Nuclear FX** → adjust values live.

### 配置文件

```
BepInEx/config/com.dsh.nuclearfx.cfg
```

Requires a game restart after editing.

### 完整参数 / Full options

**[General]**

| Key | Default | Description |
|---|---|---|
| `NukeThresholdKg` | 1000 | Minimum yield (kg TNT) to count as a nuke |
| `RadiusFactor` | 1.0 | Effect radius = 13 × yield^(1/3) × factor |
| `DebugLog` | false | Write every nuke to `nuclearfx.log` |

**[Effect] Range**

| Key | Default | Description |
|---|---|---|
| `FlashRadiusFactor` | 0.5 | Blackout range = radius × this |
| `HudRadiusFactor` | 0.8 | HUD shutdown range = radius × this |
| `BlindAngleDegrees` | 35 | Line-of-sight angle for flash blindness |

**[Effect] Timing**

| Key | Default | Description |
|---|---|---|
| `BlackoutSeconds` | 0.5 | Full black duration |
| `BlindBlackoutMultiplier` | 1.8 | Blackout × this when blinded |
| `FadeInSeconds` | 0.35 | Fade-in duration |
| `DarkenSeconds` | 0.28 | Dim duration outside blackout range |
| `HudDownSeconds` | 1.5 | Base HUD shutdown duration (scaled by strength) |
| `HudRestartFlickers` | 3 | HUD restart flicker count |
| `HudRestartOnMs` | 50 | Flicker-on duration (ms) |
| `HudRestartOffMs` | 90 | Flicker-off duration (ms) |

**[Effect] Visuals**

| Key | Default | Description |
|---|---|---|
| `NoiseStrength` | 0.6 | Max noise opacity (0–1) |
| `NoiseDuration` | 1.6 | Noise fade-out duration |
| `JitterStrength` | 22 | HUD shake amplitude (px) |
| `JitterSeconds` | 1.4 | HUD shake duration |

---

## 覆盖的核弹 / Supported nukes

The plugin hooks the game's `Missile.Warhead.Detonate`, so it works with anything using the stock explosion system:

| Nuke | Supported |
|---|---|
| Stock nukes (GPO-MK11 etc.) | ✅ |
| TsarBomba 5 / 10 / 15 / 20 / 25 / 50 MT | ✅ |
| Any mod using stock detonation | ✅ |
| Fully custom explosion logic | ❌ |

Regular bombs are below `NukeThresholdKg` and are ignored.

---

## 兼容性 / Compatibility

- **NoNukeAftermath** — no conflict
- **TsarBomba** — no conflict
- **VWS voice warning** — no conflict
- **Multiplayer** — client-side visuals only, different settings per player are fine

---

## 从源码编译 / Build from source

Requires the .NET Framework `csc` (ships with Windows) and the game's managed assemblies.

```powershell
csc /target:library /out:NuclearFX.dll ^
  /r:"<game>\BepInEx\core\BepInEx.dll" ^
  /r:"<game>\BepInEx\core\0Harmony.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\Assembly-CSharp.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\Mirage.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\UnityEngine.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\UnityEngine.CoreModule.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\UnityEngine.UIModule.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\UnityEngine.UI.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\UnityEngine.AudioModule.dll" ^
  /r:"<game>\NuclearOption_Data\Managed\netstandard.dll" ^
  src\Plugin.cs
```

---

## 卸载 / Uninstall

Delete `BepInEx/plugins/NuclearFX/`.

---

## 日志与排错 / Troubleshooting

```
BepInEx/plugins/NuclearFX/nuclearfx.log
```

Expected:

```
patched Warhead.Detonate
createOverlay ok, overlay=True
NUKE yield=250000000 dist=509 radius=8189 strength=0.94 angle=12 looking=True blind=True flash=True hud=True
effect start s=0.94 flash=True hud=True blind=True overlay=True
jitter start target=HUDCenter dur=1.4 strength=0.94
effect end
```

| Symptom | Check |
|---|---|
| Nothing happens | Is `patched` in the log? Does the nuke use stock detonation? |
| `NUKE` but no `effect start` | Overlay creation failed — check `createOverlay` lines |
| HUD doesn't hide | `FlightHud.i=ok` or `NULL` in log (NULL = not in a vehicle) |
| No shake | Check `jitter start target=` — which node it picked |

---

## 许可 / License

[MIT](LICENSE)
