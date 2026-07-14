# Getting started: scaffold, build, deploy, and iterate a SoR BepInEx mod

**What this covers:** everything needed to go from an empty folder to a loaded,
log-confirmed BepInEx plugin for Streets of Rogue on this machine — prerequisites,
the game-install layout, the csproj recipe, a plugin skeleton, the build/deploy
loop, logging, and how to debug against the live game. **Read this first** when
starting any new mod. For *what to patch* once you're running, see
[recipes.md](recipes.md); for the game's internals, see
[../game-internals/architecture.md](../game-internals/architecture.md).

## Prerequisites

- **.NET 8 SDK** — installed at `~/.dotnet/dotnet` on this machine (override with
  the `DOTNET` env var in scripts). The SDK builds `net472` assemblies fine via
  `Microsoft.NETFramework.ReferenceAssemblies`.
- **A local install of Streets of Rogue.** Mods link directly against the game's
  own DLLs (`Assembly-CSharp.dll`, `UnityEngine*.dll`, `0Harmony.dll`), which are
  copyrighted and exist only on a machine that owns the game. **This is why CI
  never builds from source** — release workflows attach prebuilt `dist/*.zip`
  artifacts committed to the repo (see `.github/workflows/release.yml` and
  "Publishing a release" in the repo README).
- **BepInEx 5** already installed into the game folder (this machine has it; new
  installs bundle it via the release zips, never via git).

## The game install on this machine

```
~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue/
├── StreetsOfRogue.exe            # Windows depot — Steam removed the native
├── StreetsOfRogue_Data/Managed/  # Linux build in Jul 2026; runs via Proton
├── winhttp.dll                   # BepInEx's loader shim (Windows build);
├── doorstop_config.ini           #   no Steam launch options needed under Proton
└── BepInEx/
    ├── core/                     # BepInEx.dll, 0Harmony.dll
    ├── plugins/                  # ← your mod DLL goes here
    ├── config/                   # per-plugin .cfg files (auto-generated)
    └── LogOutput.log             # ← the log you will live in
```

Key facts:

- It's the **Windows depot run through Proton** (flatpak Steam). The managed DLLs
  are in `StreetsOfRogue_Data/Managed/`. If Steam ever restores the native Linux
  build, DLLs would be in `StreetsOfRogueLinux_Data/Managed/` instead — csproj
  files in this ecosystem probe for both (see below).
- BepInEx loads through `winhttp.dll` + `doorstop_config.ini` sitting next to the
  exe. Under Proton this Just Works with no Steam launch options. (A native Linux
  build would instead need `./run_bepinex.sh # %command%` as the launch options.)
- Save data lives in the Proton prefix:
  `.../steamapps/compatdata/512900/pfx/drive_c/users/steamuser/Documents/Streets of Rogue/`.

## The csproj recipe

Copy this shape (it is the distilled common core of `EightPlayers/EightPlayers.csproj`,
`WizardMod/WizardMod.csproj`, and character-creator's `CharacterCreator.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>          <!-- the game is Mono/.NET 4.7.2 -->
    <AssemblyName>MyMod</AssemblyName>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <DebugType>embedded</DebugType>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>

    <!-- Locate the game; allow -p:GameDir=... override for other machines. -->
    <GameDir Condition="'$(GameDir)' == ''">/home/redaphid/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue</GameDir>
    <!-- Windows depot vs Linux depot: pick whichever Managed/ exists. -->
    <ManagedDir Condition="Exists('$(GameDir)/StreetsOfRogue_Data/Managed')">$(GameDir)/StreetsOfRogue_Data/Managed</ManagedDir>
    <ManagedDir Condition="'$(ManagedDir)' == '' And Exists('$(GameDir)/StreetsOfRogueLinux_Data/Managed')">$(GameDir)/StreetsOfRogueLinux_Data/Managed</ManagedDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Private=false on EVERY game/loader reference: never copy these DLLs
         into your build output (they're the game's own copyrighted files and
         already present at runtime). -->
    <Reference Include="BepInEx"><HintPath>$(GameDir)/BepInEx/core/BepInEx.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="0Harmony"><HintPath>$(GameDir)/BepInEx/core/0Harmony.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Assembly-CSharp"><HintPath>$(ManagedDir)/Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine"><HintPath>$(ManagedDir)/UnityEngine.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(ManagedDir)/UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
    <!-- Add per need: UnityEngine.UI, UnityEngine.ImageConversionModule (PNG
         loading), UnityEngine.Physics2DModule, UnityEngine.InputLegacyModule,
         Mirror (netcode), Rewired_Core (input), Newtonsoft.Json,
         com.rlabrecque.steamworks.net — all from $(ManagedDir), all Private=false. -->
  </ItemGroup>
</Project>
```

Gotchas learned the hard way:

- **`CopyLocalLockFileAssemblies` for bundled dependencies.** If your mod uses a
  real NuGet package that must ship alongside it (EightPlayers bundles
  `MoonSharp.Interpreter.dll` for its Lua engine), set
  `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` so the DLL
  lands in `bin/` — and your deploy step must copy **both** DLLs to `plugins/`.
  BepInEx resolves plugin dependencies from the plugins dir.
- **Prefer the game's bundled Newtonsoft.Json over Unity's `JsonUtility`** for
  JSON: `JsonUtility` fails to populate nested `[Serializable]` objects declared
  in a plugin assembly (character-creator hit this; see the comment in its csproj).
- **Embedded assets:** `<EmbeddedResource Include="Resources/MyIcon.png" />` plus
  a `GetManifestResourceNames()`/`EndsWith` loader (see
  `WizardMod/Plugin.cs LoadEmbedded`). Reference
  `UnityEngine.ImageConversionModule` for `Texture2D.LoadImage`.

## Plugin skeleton

```csharp
using BepInEx;
using HarmonyLib;

[BepInPlugin(GUID, "My Mod", "1.0.0")]
public class MyModPlugin : BaseUnityPlugin
{
    public const string GUID = "com.hypnodroid.mymod";   // unique, stable
    internal static BepInEx.Logging.ManualLogSource Log;
    internal static ConfigEntry<bool> SomeToggle;

    private void Awake()
    {
        Log = Logger;
        SomeToggle = Config.Bind("General", "SomeToggle", true,
            "Description shown in BepInEx/config/com.hypnodroid.mymod.cfg");
        new Harmony(GUID).PatchAll(typeof(MyPatches));   // or PatchAll() for whole assembly
        Log.LogInfo("MyMod loaded");
    }
}
```

Conventions from the existing mods: `harmony.PatchAll(typeof(X))` once per patch
class (WizardMod), config bound in `Awake` before patching (EightPlayers), a
static `Log` so patch classes can log. If you need per-frame work, do it in the
plugin's own `Update()` — the plugin is a real MonoBehaviour (EightPlayers pumps
all its subsystems this way) — rather than patching `Updater`.

## Build, deploy, iterate

```sh
# Build
~/.dotnet/dotnet build -c Release MyMod/MyMod.csproj
# -> MyMod/bin/Release/net472/MyMod.dll

# Deploy = copy to plugins (plus any bundled dependency DLLs)
cp MyMod/bin/Release/net472/MyMod.dll \
   "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue/BepInEx/plugins/"
```

Wrap those two steps in a `scripts/dev-install.sh` (copy the pattern from
character-creator's `scripts/dev-install.sh` — it also syncs data folders and
prints what it installed). Then launch and grep the log:

- **Launch via Steam** (normal, single window): just start the game from Steam.
- **Launch via `./start.sh`** (repo root, delegates to `scripts/start.sh`): the
  pad-rotating split-screen launcher. Each run clones the game dir (hard-linked
  exe, symlinked `Data`/`plugins`), gives it an isolated Wine prefix, seeds save
  data from the main prefix, and binds the next gamepad (`SOR_PAD`). Note the
  clones **symlink `BepInEx/plugins`** to the main install, so a single
  `dev-install.sh` updates every instance; but each clone has its **own
  `BepInEx/` runtime dirs** — logs and the EightPlayers command channel are
  per-instance.
- **Confirm load:** `BepInEx/LogOutput.log` in the instance's game dir. Your
  `Log.LogInfo` lines, Harmony patch errors, and all `Debug.LogError` spam from
  the game land here. A mod that throws in `Awake` shows a full stack trace here
  and is otherwise silently absent in game.

## Debugging against the live game

Two assets make this ecosystem unusually debuggable — use them before adding
printf-patches of your own:

1. **The decompiled source** at `decompiled/` (repo root, gitignored, ~2,900
   files — main checkout only, not in worktrees). This is ground truth for what
   to patch. Grep it for string IDs (`"ChloroformHankie"`), method names, or the
   giant switches. Read
   [../game-internals/architecture.md](../game-internals/architecture.md) for the
   map (GameController/`gc`, the per-floor scene reload, pooling, host authority)
   before spelunking.
2. **The EightPlayers command channel** — if EightPlayers.dll is installed, every
   running instance exposes ~90 verbs over `BepInEx/ep_cmd.txt`/`ep_out.txt` and
   a faster HTTP channel (port in `BepInEx/ep_port.txt`). See
   [../eightplayers/command-channel.md](../eightplayers/command-channel.md).
   Highlights for mod development: `state`/`agents`/`inventory` (observe),
   `spawnagent`/`give`/`status` (set up test scenarios instantly), and the
   **reflection verbs** (`inspect`, `get`, `set`, `call`, `find`, `members`) which
   let you poke any live object — e.g. verify your injected sprite is actually in
   `GameResources.itemDic` without a rebuild.

## Where to go next

- **Adding content** (items, traits, abilities, characters, objects, sounds):
  [recipes.md](recipes.md) — per-task checklists with the exact hook points.
- **Making a playable character specifically:** don't hand-write it — use the
  data-driven **character-creator** mod at
  `~/Projects/streets-of-rogue/character-creator` (one `character.json` per
  character, no per-character code; custom powers as drop-in `IAbilityEffect`
  classes). Its `docs/CHARACTER_FORMAT.md` is the format reference. Hand-written
  WizardMod (`WizardMod/`, `docs/WIZARD.md`) remains the worked example of the
  underlying patches.
- **Techniques for hard problems** (real tk2d sprite injection, per-instance data
  on game objects, save-safe unlocks): [roguelibs-lessons.md](roguelibs-lessons.md).
