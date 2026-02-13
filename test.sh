#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="Release"
FILTER=""
TEST_PROJECT="NovaTerminal.Tests/NovaTerminal.Tests.csproj"
NO_RESTORE=false
EXTRA_TEST_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    --filter)
      FILTER="${2:-}"
      shift 2
      ;;
    --test-project)
      TEST_PROJECT="${2:-}"
      shift 2
      ;;
    --no-restore)
      NO_RESTORE=true
      shift
      ;;
    --)
      shift
      EXTRA_TEST_ARGS+=("$@")
      break
      ;;
    -h|--help)
      cat <<'EOF'
Usage: ./test.sh [options] [-- <extra dotnet test args>]

Options:
  -c, --configuration <CONFIG>   Build/test configuration (default: Release)
  --filter <EXPR>                dotnet test filter expression
  --test-project <PATH>          Test project path (default: NovaTerminal.Tests/NovaTerminal.Tests.csproj)
  --no-restore                   Skip restore for both build and test
EOF
      exit 0
      ;;
    *)
      EXTRA_TEST_ARGS+=("$1")
      shift
      ;;
  esac
done

echo "Building NovaTerminal (${CONFIGURATION})..."
BUILD_ARGS=(build "NovaTerminal/NovaTerminal.csproj" -c "${CONFIGURATION}")
if [[ "${NO_RESTORE}" == "true" ]]; then
  BUILD_ARGS+=(--no-restore)
fi
dotnet "${BUILD_ARGS[@]}"

echo "Running tests with --no-build (${CONFIGURATION})..."
TEST_ARGS=(test "${TEST_PROJECT}" -c "${CONFIGURATION}" --no-build)
if [[ "${NO_RESTORE}" == "true" ]]; then
  TEST_ARGS+=(--no-restore)
fi
if [[ -n "${FILTER}" ]]; then
  TEST_ARGS+=(--filter "${FILTER}")
fi
if [[ ${#EXTRA_TEST_ARGS[@]} -gt 0 ]]; then
  TEST_ARGS+=("${EXTRA_TEST_ARGS[@]}")
fi
dotnet "${TEST_ARGS[@]}"
