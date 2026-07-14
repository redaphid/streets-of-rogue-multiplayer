# Streets of Rogue modding docs — index

Start here. This tree documents the game's internals (from `decompiled/`), the
EightPlayers/WizardMod plugins, and how to build new mods.

> `decompiled/` (2,937 C# files, the full decompiled game) is **gitignored** and
> exists only in the main checkout at `~/Projects/streets-of-rogue/multiplayer/`
> — not in worktrees or clones. All `decompiled/...` references assume it.

## Start here — task → doc

| I want to… | Read |
|---|---|
| Understand the game's code | [game-internals/architecture.md](game-internals/architecture.md), then the domain docs below |
| Make a new mod (scaffold, build, deploy) | [modding/getting-started.md](modding/getting-started.md) → [modding/recipes.md](modding/recipes.md) |
| Add custom sprites/sounds/text that actually render | [game-internals/sprites-audio-localization.md](game-internals/sprites-audio-localization.md) |
| Drive a **running** game programmatically | [eightplayers/command-channel.md](eightplayers/command-channel.md) |
| Understand the EightPlayers plugin itself | [eightplayers/architecture.md](eightplayers/architecture.md) |
| Make a custom character the easy way (no code) | `~/Projects/streets-of-rogue/character-creator` (own repo, well-documented) |
| Work on the ECS netcode | [ecs-systems.md](ecs-systems.md) — **canonical** per-system spec |
| Borrow techniques from RogueLibs | [modding/roguelibs-lessons.md](modding/roguelibs-lessons.md) |

## Game internals (map of `decompiled/`)

- [game-internals/architecture.md](game-internals/architecture.md) — GameController, lifecycle (per-floor scene reloads!), SessionData/SessionDataBig, spawning & pooling, input (Rewired), Mirror host authority, conventions & where to grep.
- [game-internals/agents-ai-combat.md](game-internals/agents-ai-combat.md) — Agent, Brain/goals/GoalArbitrate, relationships ("Hateful", relHate ≥ 5), the one damage funnel, movement, interactions, stats.
- [game-internals/content-systems.md](game-internals/content-systems.md) — items, status effects (= timed traits), traits, special abilities, unlocks, mutators, string-ID conventions, where to hook to add each content type.
- [game-internals/world-and-ui.md](game-internals/world-and-ui.md) — level generation, ObjectReal hierarchy, menus/HUD, character select.
- [game-internals/sprites-audio-localization.md](game-internals/sprites-audio-localization.md) — the TWO sprite systems (GameResources dicts vs tk2d collections), sprite injection, audio, NameDB; includes the WizardMod asset post-mortem.

## Modding guides

- [modding/getting-started.md](modding/getting-started.md) — BepInEx plugin scaffold, csproj against the game DLLs, build/deploy/iterate on this machine.
- [modding/recipes.md](modding/recipes.md) — worked recipes: new item, status effect, trait, ability, character, object, sound, mutator.
- [modding/roguelibs-lessons.md](modding/roguelibs-lessons.md) — the discontinued framework's treasure map: what it patched per feature, the Cecil field-injection trick, correct tk2d sprite injection, save-safety patterns. RogueLibs is a read-only reference — don't depend on it.

## EightPlayers / WizardMod (this repo's plugins)

- [eightplayers/architecture.md](eightplayers/architecture.md) — module map, Core/patch pattern, EcsNet overview, cross-repo map.
- [eightplayers/command-channel.md](eightplayers/command-channel.md) — **full** verb + events reference, both transports, how to add a verb.
- [WIZARD.md](WIZARD.md) — the Wizard character mod: install, per-patch rationale. Accurate to code.

## ECS netcode (Cloudflare co-op)

- [ecs-systems.md](ecs-systems.md) — **canonical** per-system spec (choke point → wire event → apply → e2e), freshest of the ECS docs.
- [ecs-netcode.md](ecs-netcode.md) — architecture/vision. ⚠️ Its "what works now"/"ported so far" lists undercount the current systems — trust ecs-systems.md.
- [ecs-migration-plan.md](ecs-migration-plan.md) — strategy: the two hack piles (ForceSeed, VirtualInput) and their clean replacements, plank order, rogue-brain split.
- [trace-choke-points.md](trace-choke-points.md) — choke-point survey + SOR_TRACE event vocabulary. Line numbers may drift with the decompile.

## Operational / historical

- [debug-harness.md](debug-harness.md) — playbook for launching Proton instances and driving them. ⚠️ Its verb tables are a subset; the full list is [eightplayers/command-channel.md](eightplayers/command-channel.md).
- [HANDOFF.md](HANDOFF.md) — session-resume state. ⚠️ Point-in-time (commit hashes, TODOs that may be done) — verify against code before trusting.
