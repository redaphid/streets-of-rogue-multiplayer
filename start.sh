#!/usr/bin/env bash
# Launch the modded Streets of Rogue OUTSIDE of Steam.
#
# Why: Steam Input (and Big Picture) can take an *exclusive* grab on a
# controller — the game process then never sees a single event from it, so
# your gamepad does nothing in-game even though Steam shows it working. This
# starts the game directly inside the Steam flatpak sandbox (so BepInEx's
# doorstop still finds the right glibc) but with no Steam Input layer in the
# way, leaving every /dev/input device free for the game to read.
#
# Usage:  ./start.sh
#
# Trade-off: no Steam overlay. To go back to launching via Steam, just press
# Play in Steam as usual (make sure the game's Launch Options are set — see
# docs/WIZARD.md / README.md).
set -euo pipefail

exec flatpak run --command=sh com.valvesoftware.Steam -c '
  cd "$HOME/.local/share/Steam/steamapps/common/Streets of Rogue" || exit 1
  exec ./run_bepinex.sh
'
