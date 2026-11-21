#!/bin/bash
# Double-click in Finder: builds castlink-mac (see build.sh).
cd "$(dirname "$0")"
./build.sh
echo
read -n 1 -s -r -p "Press any key to close."
