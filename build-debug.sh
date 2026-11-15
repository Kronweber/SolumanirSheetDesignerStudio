#!/bin/sh
cd "$(dirname "$0")" || exit 1
dotnet build -c Debug "$@"
