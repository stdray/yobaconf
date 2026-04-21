#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TARGET="Default"
CONFIGURATION="Release"
EXTRA_ARGS=()

for ARG in "$@"; do
	case $ARG in
		--target=*)
			TARGET="${ARG#*=}"
			;;
		--configuration=*)
			CONFIGURATION="${ARG#*=}"
			;;
		*)
			EXTRA_ARGS+=("$ARG")
			;;
	esac
done

dotnet tool restore

if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
	dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION" "${EXTRA_ARGS[@]}"
else
	dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION"
fi
