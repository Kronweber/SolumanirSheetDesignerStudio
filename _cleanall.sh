#!/bin/sh
cd "$(dirname "$0")" || exit 1
rm -rf bin app/*/*/bin app/*/*/obj
sh ./_restoreProjects.sh
