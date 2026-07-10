#!/usr/bin/env bash
# The native Linux build this script used to launch was removed from the
# Steam install (Jul 2026, Windows depot only now). Launching goes through
# Proton — see scripts/start.sh (pad-rotating split-screen launcher).
exec "$(dirname "$0")/scripts/start.sh" "$@"
