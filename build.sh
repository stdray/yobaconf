#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TARGET="Default"
CONFIGURATION="Release"
QUIET=0
EXTRA_ARGS=()

for ARG in "$@"; do
	case $ARG in
		--target=*)
			TARGET="${ARG#*=}"
			;;
		--configuration=*)
			CONFIGURATION="${ARG#*=}"
			;;
		--quiet)
			QUIET=1
			;;
		*)
			EXTRA_ARGS+=("$ARG")
			;;
	esac
done

dotnet tool restore >/dev/null

# --quiet: filter stdout/stderr through grep to surface only actionable lines
# (test totals, task durations, error/warning summaries). Full output is still
# produced by Cake; we just strip noise for Claude/terminal consumption.
# PIPESTATUS[0] preserves Cake's exit code despite the pipe.
if [ "$QUIET" -eq 1 ]; then
	export NO_COLOR=1
	export DOTNET_NOLOGO=1
	FILTER_PATTERN='(Passed!|Failed!|Skipped:|Succeeded$|^\s*Succeeded|error [A-Z]+[0-9]+:|[0-9]+ Error\(s\)|[0-9]+ Warning\(s\)|Time Elapsed|Duration:|Total:|^\s*Task\s+Duration|BLOCKED:|Task:\s+\S+\s+Duration)'
	set +o pipefail
	if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
		dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION" --verbosity=minimal "${EXTRA_ARGS[@]}" 2>&1 | grep -E "$FILTER_PATTERN" || true
	else
		dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION" --verbosity=minimal 2>&1 | grep -E "$FILTER_PATTERN" || true
	fi
	exit "${PIPESTATUS[0]}"
fi

if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
	dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION" "${EXTRA_ARGS[@]}"
else
	dotnet cake "$SCRIPT_DIR/build.cake" --target="$TARGET" --configuration="$CONFIGURATION"
fi
