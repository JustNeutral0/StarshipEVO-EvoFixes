# EvoFixes

A Starship EVO mod that fixes game bugs and adds a few quality-of-life changes, all in one DLL. Each fix can be
switched off on its own in the **EvoFixes** tab of [Mod Settings](https://github.com/JustNeutral0/StarshipEVO-ModSettings)
(optional; without it every fix is simply on).

> Early test release. Built and tested against Starship EVO early-access **26w45a** (Unity 6 beta).
> Game updates may break it until it's rebuilt. Please report problems in Issues.

## What it fixes

- **Power recount (multiplayer).** A ship spawned by a player who joined someone else's game shows too little power,
  and switching its reactors off takes it below zero (game issue
  [#8031](https://github.com/tsunamayo/Starship-EVO/issues/8031)). On a joined player's machine, every 2 seconds
  each ship's power output and capacity are checked against the sum of its reactors, solar panels, wind turbines and
  power cells, and corrected when they stay wrong. Only the joining player needs it; it does nothing for the host
  or in single-player.
- **Pipe Thin** connects at its far end.
- **Holo text**: text screens and kiosks show letters instead of squares. Needs a font file built with
  `tools/holo_atlas.py` from an older install of the game; without it this fix does nothing.
- **Life support recheck**: a life support stuck on "leaking" checks its room again from time to time.
- **NPC avoidance**: NPC ships stop spinning in place and steer around blocks.
- **NPC light cruise**: NPC ships don't get stuck at 0 m/s in light cruise.
- **Glass panes**: touching glass blocks draw as one clear pane.
- **Galaxy sky**: stars all around the sky, instead of half the sky empty near the top or bottom of the galaxy.
- **Repair Dock**: a base block that repairs the ship in its shipyard for plates.
- Tools: a **Leak finder** for rooms, and a live **NPC ships** list.

## Install

Needs [EvoCore](https://github.com/JustNeutral0/StarshipEVO-EvoCore) 0.9.81 or newer (install it first) and
[Starship Uplink](https://github.com/MadTheDog/Starship-Uplink).

**First, the game build:** these mods are built for the **Unity 6 beta** of Starship EVO, not the default build. In Steam, right-click Starship EVO, open **Properties > Betas**, pick **unity6**, and let it update. The mods don't work on the default build (character creation breaks, for one).

1. In Uplink, choose **Add from GitHub** and enter `JustNeutral0/StarshipEVO-EvoFixes`.
2. Pick the `justneutral.evofixes-<version>.zip` asset, then install it into your profile.
3. Launch the game through Uplink.

## Testing the multiplayer power fix

1. The player who joins installs EvoCore, EvoFixes and (to see the switch) Mod Settings.
2. Join a friend's game and spawn a ship that has reactors from the blueprint menu.
3. Check the ship's power: it should never be negative, and it should match what the host sees, also after
   switching reactors off and on.
4. Send `BepInEx\LogOutput.log` from the game folder. A line starting with `[Info   :PowerRecount]` means the fix
   stepped in.

## Licence

MIT. Source is in this repository.
