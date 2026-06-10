# SekhemaHelper

A [GH](https://github.com/Gordin/GameHelper2) plugin that solves the **Trial of the Sekhemas**
(Sanctum) room layout live: while the floor map is open it scores every room and draws the
**best route** from your current position to the boss, directly over the in-game map.

The plugin reads the floor's rooms — their type, affliction and reward — straight from the game's
UI tree, weights each one against the active profile (and your own defences), then highlights the
highest-value path so you can pick the next room at a glance without leaving the map.

## Features

- **Best-path overlay.** A longest-weighted route over the floor's layered graph (player room →
  boss), drawn as colored frames around each room on the way. The route always starts from your
  **current** room, so the part you've already walked stays fixed.
- **Per-room scoring** = base + room type + affliction + reward + connectivity (a small bonus for
  rooms with more than one exit, to keep options open). Optional **debug overlay** prints each
  room's weight and the breakdown of every contributing term on top of the room.
- **Weight profiles, fully tunable.** Two built in — **Default** (balanced clear) and **No-Hit**
  (heavily penalises deadly afflictions and the timed Hourglass room). Every room-type / affliction
  / reward weight is editable right in the settings panel via sliders and saved to the plugin's own
  `config\settings.txt`, so you can tune scoring to your build; changes apply live and **Reset this
  profile to defaults** restores the built-in values. Switch profiles live from the combo.
- **Dynamic affliction weights from your stats.** Defence-dependent afflictions are scored against
  your live character: *Iron Manacles* / *Shattered Shield* / *Corrosive Concoction* scale with your
  effective Evasion / Energy Shield, and *Worn Sandals* is treated as harmless when **Queen of the
  Forest** is equipped. Stats are read from the player's `Stats` component each frame.
- **Language-independent room classification.** Rooms are identified by their internal
  `SanctumRooms` / `SanctumPersistentEffects` data rows, not by on-screen text, so it works on any
  client language. Room-type tokens are mapped to the in-game display names the profiles key on
  (`Arena`→Hourglass, `Lair`→Chalice, `Explore`→Escape, plus Ritual / Gauntlet / Boss).
- **Reward rooms** (keys, caches, fountains, merchant, pledge, honour, boon, …) are recognised from
  their `Treasure` room ids and scored from the profile's reward table.
- Resolves the Sanctum map panel from the game UI each frame and shows an always-on status line
  (when Debug is on) so a missing panel / closed map is visible instead of a blank overlay.

## Requirements

- A working [GH](https://github.com/Gordin/GameHelper2) checkout (this is a plugin, not a
  standalone app).
- .NET 10 SDK (the project targets `net10.0-windows`, x64).

## Build & install

This plugin is meant to live inside a GH source tree, because it references
`GameHelper.csproj` and copies its build output into GameHelper's `Plugins` folder.

1. Clone this repo into the GameHelper2 `Plugins` directory so the layout is:

   ```
   <GameHelper2>/
     GameHelper/
       GameHelper.csproj
     Plugins/
       SekhemaHelper/        ← contents of this repo
         SekhemaHelper.csproj
         SekhemaHelperCore.cs
         ...
   ```

   The `.csproj` expects `..\..\GameHelper\GameHelper.csproj` to exist relative to itself.

2. Build:

   ```
   dotnet build Plugins/SekhemaHelper/SekhemaHelper.csproj -c Debug
   ```

   The post-build step copies `SekhemaHelper.dll` into
   `GameHelper/<OutDir>/Plugins/SekhemaHelper/`.

3. Launch GameHelper2, enable **SekhemaHelper** in the plugin list, and open a Trial of the
   Sekhemas floor map.

## Settings

| Setting | Default | Notes |
|---|---|---|
| **Active Profile** | `Default` | Weight profile used for scoring: `Default` or `No-Hit`. |
| **Draw Best Path** | `on` | Toggle the route overlay. |
| **Frame Thickness** | `4` | Line thickness (px) of the path frames (1–10). |
| **Best Path Color** | green | Color of the route frames. |
| **Debug (show weights)** | `off` | Print each room's weight + term breakdown and a status HUD on the map. |
| **Debug Text / Background Color** | white / black | Colors for the debug overlay. |

### Weights — *(active profile)*

The **Weights** section edits every room-type, affliction and reward weight of the selected profile
with sliders (higher = more desirable; drag to adjust, Ctrl+click to type). Changes apply live and
are saved to `config\settings.txt`, loading back with the profile next session. **Reset this profile
to defaults** restores the built-in values.

## Credits

- Built as a plugin for [GameHelper2](https://github.com/Gordin/GameHelper2).
- Weight profiles ported from the legacy **PathfindSanctum** project.

## Disclaimer

This is a read-only overlay tool for personal use. Use at your own risk and in accordance with the game's terms of service.
