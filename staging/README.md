# NightTerrors

> *She comes when the fire dies low.*

When all players sleep, there is a chance that Mara finds you.

You awaken not in your bed, but somewhere else entirely — a distant shore, a frozen peak, the bowels of a swamp. Around you, monsters stir. Your gear may be gone. Someone else's life may be in your hands.

Survive until dawn, or don't. Either way, you wake up in your bed. The night passes as if nothing happened.

---

## What Happens

Each time all players sleep, there is a configurable chance (1-in-20 by default) that the event triggers. If it does:

- All players are teleported to a random location anywhere in the world
- A **scenario** is chosen at random:
  - **Keep Gear** — you have what you had
  - **Go Naked** — stripped of everything
  - **Different Equipment** — given a strange kit suited to no situation in particular
  - **Swap Equipment** — you have someone else's inventory, they have yours
- Monsters appropriate to the biome spawn nearby
- The weather turns
- A timer runs — if you're still alive when it expires, Mara sends you back anyway
- No skill loss on death. No tombstone. Your inventory is restored when you respawn.

---

## Configuration

All settings are in `BepInEx/config/nightterrors.cfg`.

| Setting | Default | Description |
|---|---|---|
| `TriggerChance` | `20` | 1-in-N chance per sleep. Set to `1` for testing. |
| `FriendlyFire` | `false` | Allow players to damage each other during the event. |
| `SpawnMonsters` | `true` | Spawn monsters at the teleport location. |
| `MonsterCount` | `3` | Number of monsters to spawn. |
| `ScenarioWeights` | `1,1,1,1` | Relative weights for: KeepGear, GoNaked, DifferentEquipment, SwapEquipment. |
| `EventDuration` | `30` | Maximum event duration in seconds before survivors are sent back. |
| `WeatherPool` | *(see cfg)* | Comma-separated list of environment names to pick from. Empty to disable. |

---

## Installation

Install with a mod manager, or drop `NightTerrors.dll` into `BepInEx/plugins/` on both the **server** and all **clients**.

---

## Source

[github.com/jasonuithol/NightTerrors](https://github.com/jasonuithol/NightTerrors)
