#!/bin/sh
cd "$(dirname "$0")" || exit 1
rm -rf bin src/*/bin src/*/obj tests/*/bin tests/*/obj
