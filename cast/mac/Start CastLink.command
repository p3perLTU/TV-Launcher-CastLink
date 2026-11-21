#!/bin/bash
# Double-click in Finder: CastLink in the terminal with a virtual second screen for the iPad.
# (The app with its window is build/CastLink.app; for other options: ./castlink-mac --help)
cd "$(dirname "$0")"
[ -x ./castlink-mac ] || ./build.sh || { read -n 1 -s -r -p "Build failed. Press any key."; exit 1; }
./castlink-mac --virtual "$@"
