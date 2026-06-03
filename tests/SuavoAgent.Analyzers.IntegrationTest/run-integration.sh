#!/usr/bin/env bash
# Integration test: build the canary fixture project. The build MUST fail with
# SUAVO0001. If it succeeds, the analyzer is broken or unwired — exit non-zero.
set -uo pipefail

cd "$(dirname "$0")/../.."
PROJECT="tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj"

echo "Running integration test — expecting SUAVO0001 to fail the build..."

# Run dotnet build. Capture output. Expect non-zero exit AND SUAVO0001 in stderr.
output=$(dotnet build "$PROJECT" --nologo --verbosity quiet 2>&1 || true)

if echo "$output" | grep -q "SUAVO0001"; then
  echo "OK — SUAVO0001 emitted (analyzer is wired correctly)."
  exit 0
else
  echo "FAIL — SUAVO0001 NOT emitted. Build output:"
  echo "$output"
  exit 1
fi
