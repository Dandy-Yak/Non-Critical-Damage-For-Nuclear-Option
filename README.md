# NonCriticalDamage

A BepInEx 5 mod for Nuclear Option that reduces incoming damage to player aircraft, so
they can take several hits before being destroyed. ("I'll teach you how to do that when we get back") Designed for use with my
[Ace Combat Reloads](https://github.com/Dandy-Yak/Ace-Combat-Style-Reloads-For-Nuclear-Option)
mod to create an authentic Ace Combat experience.

## Install

1. Install BepInEx 5 for Nuclear Option.
2. Put `NonCriticalDamage.dll` in `BepInEx/plugins`.
3. Optional: install BepInEx Configuration Manager to change the settings in game (F1).

## What it does

Damage to protected aircraft (normally player-flown) is reduced before the game applies
it. The values are fixed in the code (the `Tuning` class in `Plugin.cs`):

| Value | Setting | Effect |
|---|---|---|
| Hit point multiplier | 10 | Bullet, fire, collision and pilot G-force damage is divided by 10. |
| Blast damage multiplier | 10 | Explosion damage (missiles, bombs, flak) is divided by 10. |
| Max damage per hit | 25 | After dividing and armour, one hit can do at most 25 pierce, blast or fire damage to one part. Collisions and G-force are not capped. |
| Blast shove multiplier | 3 | The physical shove from explosions is a third as strong. |
| Max overpressure per hit | 1000 | After dividing, one explosion can push one part with at most 1000 overpressure. |
| Engine toughness | 4 | Engines and the parts they depend on take a quarter of that again. |

The pilot is protected too. Nuclear blasts are not reduced.
Bullet and explosion hits shake the camera, in the cockpit and the orbit and chase views.

## Multiplayer

Hit point damage and engine protection are decided on the host, so the host needs the
mod. The explosion shove, fuel ignition and camera shake happen on the machine of whoever
is flying the aircraft, so It is required for the host and client to have the mod installed

The mod adds a tag to the game version, so players with and without it (or with a
different version) don't see or join each other's games. It works alongside Ace Combat
Reloads, which does the same.

## Settings

After the first launch, edit `BepInEx/config/nuclearoption.noncriticaldamage.cfg`:

| Setting | Default | Meaning |
|---|---|---|
| `AffectAIAircraft` | false | Protect AI-flown aircraft too. |
| `ShowHurtOverlay` | false | Show the red screen flash when the pilot is hurt. |
| `CameraShake` | 5 | Strength of the camera shake on bullet and explosion hits. 0 = off, 5 = full strength. |
| `DebugLogging` | false | Log every reduction to `BepInEx/LogOutput.log`. Pierce and fire are shown after armour. |

## Building

1. Open `NonCriticalDamage.csproj` and check `GameDir` points at your Nuclear Option folder.
2. Run `dotnet build -c Release`.
3. Copy `bin/Release/net472/NonCriticalDamage.dll` into `BepInEx/plugins/`.

## Credits

Created by Dandy Yak. Developed with AI assistance.

## License

Free to use, modify and share, but not to sell. See [LICENSE](LICENSE).
