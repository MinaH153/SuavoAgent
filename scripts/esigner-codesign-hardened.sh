#!/usr/bin/env bash
set -euo pipefail
umask 077

readonly TOOL_VERSION="1.3.0"
readonly TOOL_URL="https://github.com/SSLcom/CodeSignTool/releases/download/v1.3.0/CodeSignTool-v1.3.0.zip"
readonly TOOL_SHA256="359782cee5c709b172610e2abd8cb49445bfadd26f44073ca18600c585b91b8d"

fail() {
  printf 'eSigner signing refused: %s\n' "$1" >&2
  exit 1
}

if [[ $# -lt 2 ]]; then
  fail "usage: $0 <output-directory> <input-file> [input-file ...]"
fi

for name in ES_USERNAME ES_PASSWORD ES_CREDENTIAL_ID ES_TOTP_SECRET; do
  [[ -n "${!name:-}" ]] || fail "missing required protected credential $name"
done

for command in curl java sha256sum unzip; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

java_settings="$(java -XshowSettings:properties -version 2>&1)"
java_vendor="$(sed -n 's/^[[:space:]]*java.vendor = //p' <<<"$java_settings")"
java_runtime="$(sed -n 's/^[[:space:]]*java.runtime.version = //p' <<<"$java_settings")"
[[ "$java_vendor" == "Eclipse Adoptium" ]] || fail "Java vendor must be Eclipse Adoptium"
[[ "$java_runtime" == "11.0.31+11" ]] || fail "Java runtime must be exact Temurin 11.0.31+11"

output_dir="$1"
shift
[[ -d "$output_dir" && ! -L "$output_dir" ]] || fail "output directory must be a regular directory"

declare -a inputs=()
declare -A output_names=()
for input in "$@"; do
  [[ -f "$input" && ! -L "$input" ]] || fail "input must be a regular non-link file: $input"
  name="$(basename -- "$input")"
  [[ -z "${output_names[$name]:-}" ]] || fail "duplicate output basename: $name"
  [[ ! -e "$output_dir/$name" && ! -L "$output_dir/$name" ]] || fail "signed output already exists: $name"
  output_names[$name]=1
  inputs+=("$input")
done

temporary_root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/suavoagent-esigner.XXXXXXXX")"
trap 'rm -rf -- "$temporary_root"' EXIT
archive="$temporary_root/CodeSignTool-v${TOOL_VERSION}.zip"
curl --fail --location --max-redirs 3 --proto '=https' --tlsv1.2 \
  --silent --show-error --output "$archive" "$TOOL_URL"
printf '%s  %s\n' "$TOOL_SHA256" "$archive" | sha256sum --check --status -
unzip -q "$archive" -d "$temporary_root/tool"
jar="$temporary_root/tool/jar/code_sign_tool-${TOOL_VERSION}.jar"
[[ -f "$jar" && ! -L "$jar" ]] || fail "verified CodeSignTool archive has an invalid layout"

for input in "${inputs[@]}"; do
  args=(
    java -jar "$jar" sign
    -username "$ES_USERNAME"
    -password "$ES_PASSWORD"
    -credential_id "$ES_CREDENTIAL_ID"
    -totp_secret "$ES_TOTP_SECRET"
    -input_file_path "$input"
    -output_dir_path "$output_dir"
    -malware_block=true
  )
  "${args[@]}"
  signed="$output_dir/$(basename -- "$input")"
  [[ -s "$signed" && ! -L "$signed" ]] || fail "CodeSignTool did not produce the exact signed output"
done
