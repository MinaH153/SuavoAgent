#!/bin/bash -p
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
unset BASH_ENV ENV CDPATH
unset GIT_DIR GIT_WORK_TREE GIT_CONFIG GIT_CONFIG_COUNT GIT_CONFIG_KEY_0 GIT_CONFIG_VALUE_0
unset GIT_CONFIG_PARAMETERS GIT_INDEX_FILE GIT_COMMON_DIR GIT_OBJECT_DIRECTORY
unset GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_NAMESPACE GIT_REPLACE_REF_BASE
unset GIT_SSH GIT_SSH_COMMAND GIT_ASKPASS SSH_ASKPASS
export GIT_NO_REPLACE_OBJECTS=1

if [[ $# -ne 6 ]]; then
  echo "usage: sign-ota-v1-bridge-local.sh <v1-private-key.pem> <unpacked-stage-dir> <v2-handoff-descriptor.json> <v2-handoff-descriptor.sig> <response.json> <response.b64>" >&2
  exit 64
fi

script_dir="$(cd -- "$(/usr/bin/dirname -- "$0")" && pwd -P)"
repo_root="$(cd -- "$script_dir/.." && pwd -P)"

fail() {
  echo "$1" >&2
  exit 1
}

sterile_git() {
  local repository="$1"
  shift
  /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/nonexistent LC_ALL=C \
    GIT_CONFIG=/dev/null GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 \
    GIT_TERMINAL_PROMPT=0 GIT_OPTIONAL_LOCKS=0 GIT_NO_REPLACE_OBJECTS=1 \
    /usr/bin/git -C "$repository" -c core.fsmonitor=false "$@"
}

scoped_git_config() {
  local scope="$1"
  shift
  case "$scope" in
    --local)
      /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/nonexistent LC_ALL=C \
        GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_NO_REPLACE_OBJECTS=1 \
        /usr/bin/git -C "$repo_root" config --local --no-includes "$@"
      ;;
    --global)
      /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME="${HOME:-/nonexistent}" LC_ALL=C \
        GIT_CONFIG_NOSYSTEM=1 GIT_NO_REPLACE_OBJECTS=1 \
        /usr/bin/git -C "$repo_root" config --global --no-includes "$@"
      ;;
    --system)
      /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/nonexistent LC_ALL=C \
        GIT_CONFIG_GLOBAL=/dev/null GIT_NO_REPLACE_OBJECTS=1 \
        /usr/bin/git -C "$repo_root" config --system --no-includes "$@"
      ;;
  esac
}

resolve_leaf_path() {
  local path="$1"
  local parent
  local leaf
  parent="$(/usr/bin/dirname -- "$path")"
  leaf="$(/usr/bin/basename -- "$path")"
  [[ -n "$leaf" && "$leaf" != "." && "$leaf" != ".." ]] || return 1
  parent="$(cd -- "$parent" && pwd -P)" || return 1
  printf '%s/%s\n' "$parent" "$leaf"
}

is_inside_repo() {
  [[ "$1" == "$repo_root" || "$1" == "$repo_root/"* ]]
}

if ! git_root="$(sterile_git "$repo_root" rev-parse --show-toplevel 2>/dev/null)"; then
  fail "local signing source is not an exact Git checkout"
fi
if ! git_root="$(cd -- "$git_root" && pwd -P)" || [[ "$git_root" != "$repo_root" ]]; then
  fail "local signing script is not in the exact repository root"
fi

if ! origin="$(scoped_git_config --local --get-all remote.origin.url 2>/dev/null)"; then
  fail "local signing source has no approved origin"
fi
case "$origin" in
  "https://github.com/MinaH153/SuavoAgent.git"|\
  "git@github.com:MinaH153/SuavoAgent.git"|\
  "ssh://git@github.com/MinaH153/SuavoAgent.git") ;;
  *) fail "local signing source origin is not MinaH153/SuavoAgent" ;;
esac
for scope in --local --global --system; do
  if transport_rewrites="$(scoped_git_config "$scope" --get-regexp '^(url\..*\.(insteadof|pushinsteadof)|remote\.origin\.(uploadpack|receivepack)|core\.sshcommand)$' 2>/dev/null)"; then
    [[ -z "$transport_rewrites" ]] || fail "Git transport rewrites are forbidden for v1 signing"
  fi
done

if ! replacement_refs="$(sterile_git "$repo_root" for-each-ref --format='%(refname)' refs/replace/)"; then
  fail "local signing replacement refs could not be verified"
fi
[[ -z "$replacement_refs" ]] || fail "Git replacement refs are forbidden for v1 signing"
sterile_git "$repo_root" diff-index --cached --quiet HEAD -- || \
  fail "local signing source index must exactly equal HEAD"
if ! untracked_source="$(sterile_git "$repo_root" ls-files --others --exclude-standard --)"; then
  fail "local signing untracked source could not be verified"
fi
[[ -z "$untracked_source" ]] || fail "local signing source must be completely clean, including untracked files"
if ! ignored_scripts="$(sterile_git "$repo_root" ls-files --others --ignored --exclude-standard -- scripts)"; then
  fail "local signing source ignored-file status could not be verified"
fi
if [[ -n "$ignored_scripts" ]]; then
  fail "local signing scripts contain ignored untracked files"
fi

verify_head_bytes() {
  local relative="$1"
  local index_entry
  [[ -f "$repo_root/$relative" && ! -L "$repo_root/$relative" ]] || return 1
  index_entry="$(sterile_git "$repo_root" ls-files -v -- "$relative")" || return 1
  [[ "$index_entry" == "H $relative" ]] || return 1
  sterile_git "$repo_root" show "HEAD:$relative" | \
    /usr/bin/cmp -s -- "$repo_root/$relative" -
}

verify_tracked_worktree() {
  local record
  local metadata
  local mode
  local object_id
  local stage
  local relative
  local actual_id
  local listing
  local count=0
  listing="$(/usr/bin/mktemp /tmp/suavo-v1-bridge-index.XXXXXX)" || return 1
  if ! sterile_git "$repo_root" ls-files -s -z > "$listing"; then
    /bin/rm -f -- "$listing"
    return 1
  fi
  while IFS= read -r -d '' record; do
    [[ "$record" == *$'\t'* ]] || { /bin/rm -f -- "$listing"; return 1; }
    metadata="${record%%$'\t'*}"
    relative="${record#*$'\t'}"
    read -r mode object_id stage <<< "$metadata"
    [[ "$stage" == "0" && ( "$mode" == "100644" || "$mode" == "100755" ) ]] || \
      { /bin/rm -f -- "$listing"; return 1; }
    [[ -f "$repo_root/$relative" && ! -L "$repo_root/$relative" ]] || \
      { /bin/rm -f -- "$listing"; return 1; }
    if [[ "$mode" == "100755" ]]; then
      [[ -x "$repo_root/$relative" ]] || { /bin/rm -f -- "$listing"; return 1; }
    else
      [[ ! -x "$repo_root/$relative" ]] || { /bin/rm -f -- "$listing"; return 1; }
    fi
    actual_id="$(sterile_git "$repo_root" hash-object --no-filters -- "$relative")" || \
      { /bin/rm -f -- "$listing"; return 1; }
    [[ "$actual_id" == "$object_id" ]] || { /bin/rm -f -- "$listing"; return 1; }
    count=$((count + 1))
  done < "$listing"
  /bin/rm -f -- "$listing"
  [[ "$count" -gt 0 ]]
}
[[ -x "$repo_root/scripts/sign-ota-v1-bridge-local.sh" ]] || \
  fail "local signing wrapper must retain its executable mode"
for relative in \
  scripts/sign-ota-v1-bridge-local.sh \
  scripts/v1_bridge_cli.py \
  scripts/v1_bridge_release.py \
  scripts/v1_bridge_handoff.py \
  scripts/v1_bridge_file_io.py \
  scripts/v1_bridge_crypto.py \
  scripts/v1_bridge_source_guard.py \
  scripts/ota_update_trust_roots.py \
  scripts/ecdsa_der_to_p1363.py \
  security/ota-update-trust-roots.json; do
  verify_head_bytes "$relative" || fail "local signing input differs from its exact HEAD blob: $relative"
done
verify_tracked_worktree || fail "local signing source has tracked worktree modifications"

if ! key_path="$(resolve_leaf_path "$1")"; then
  fail "v1 bridge key path could not be resolved"
fi
if is_inside_repo "$key_path"; then
  fail "v1 bridge key must be outside the source repository"
fi

if [[ ! -d "$2" || -L "$2" ]]; then
  fail "unpacked stage directory must be a regular non-link directory"
fi
if ! stage_dir="$(cd -- "$2" && pwd -P)"; then
  fail "unpacked stage directory could not be resolved"
fi
case "$stage_dir/" in
  "$repo_root/"*) fail "unpacked stage directory must be outside the source repository" ;;
esac
request_path="$stage_dir/bridge-signing-request.json"
[[ -f "$request_path" && ! -L "$request_path" ]] || fail "bridge signing request must be a regular non-link file"
request_sha="$(/usr/bin/plutil -extract sourceSha raw -o - "$request_path" 2>/dev/null)" || \
  fail "bridge signing request source SHA could not be read"
[[ "$request_sha" =~ ^[0-9a-f]{40}$ ]] || fail "bridge signing request source SHA is malformed"
head_sha="$(sterile_git "$repo_root" rev-parse HEAD)"
[[ "$head_sha" == "$request_sha" ]] || fail "local signing HEAD must equal the staged request source SHA"
authority_line="$(sterile_git / ls-remote https://github.com/MinaH153/SuavoAgent.git refs/heads/main)" || \
  fail "could not query the fixed GitHub main authority before v1 signing"
[[ "$authority_line" =~ ^([0-9a-f]{40})$'\t'refs/heads/main$ ]] || \
  fail "fixed GitHub main authority returned a malformed identity"
[[ "$head_sha" == "${BASH_REMATCH[1]}" ]] || fail "local signing HEAD must equal authoritative GitHub main"

if ! handoff_descriptor="$(resolve_leaf_path "$3")" || \
   [[ ! -f "$handoff_descriptor" || -L "$handoff_descriptor" ]]; then
  fail "v2 handoff descriptor must be a regular non-link file"
fi
if ! handoff_signature="$(resolve_leaf_path "$4")" || \
   [[ ! -f "$handoff_signature" || -L "$handoff_signature" ]]; then
  fail "v2 handoff signature must be a regular non-link file"
fi
if is_inside_repo "$handoff_descriptor" || is_inside_repo "$handoff_signature"; then
  fail "v2 handoff inputs must be outside the source repository"
fi
case "$handoff_descriptor" in "$stage_dir/"*) fail "v2 handoff descriptor must be downloaded separately from the request artifact" ;; esac
case "$handoff_signature" in "$stage_dir/"*) fail "v2 handoff signature must be downloaded separately from the request artifact" ;; esac

if ! response_json="$(resolve_leaf_path "$5")"; then
  fail "response JSON path could not be resolved"
fi
if ! response_b64="$(resolve_leaf_path "$6")"; then
  fail "response Base64 path could not be resolved"
fi
if is_inside_repo "$response_json" || is_inside_repo "$response_b64"; then
  fail "response outputs must be outside the source repository"
fi

python_bin=""
for candidate in /opt/homebrew/bin/python3 /usr/local/bin/python3; do
  if [[ -x "$candidate" ]] && /usr/bin/env -i \
    PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/nonexistent LC_ALL=C \
    "$candidate" -I -S -B -c \
    'import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)' \
    >/dev/null 2>&1; then
    python_bin="$candidate"
    break
  fi
done
if [[ -z "$python_bin" ]]; then
  fail "local bridge signing requires an approved fixed-path Python 3.10 or newer"
fi

exec /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/nonexistent LC_ALL=C \
  SUAVO_V1_BRIDGE_ISOLATED_BOOTSTRAP=v1-bridge-clean-isolated-v1 \
  "$python_bin" -I -S -B -c '
import runpy
import os
import sys

scripts_path = os.path.realpath(sys.argv[1])
base_prefix = os.path.realpath(sys.base_prefix)
major, minor = sys.version_info[:2]
stdlib_paths = [
    os.path.join(base_prefix, "lib", f"python{major}{minor}.zip"),
    os.path.join(base_prefix, "lib", f"python{major}.{minor}"),
    os.path.join(base_prefix, "lib", f"python{major}.{minor}", "lib-dynload"),
]
if [os.path.realpath(entry) if entry else "" for entry in sys.path] != stdlib_paths:
    raise SystemExit("isolated Python path is not the exact reviewed stdlib tuple")
cli_path = scripts_path + "/v1_bridge_cli.py"
sys.path[:] = [*stdlib_paths, scripts_path]
sys.argv = [cli_path, *sys.argv[2:]]
runpy.run_path(cli_path, run_name="__main__")
' "$script_dir" local-sign \
  --key "$key_path" \
  --stage-dir "$stage_dir" \
  --descriptor "$handoff_descriptor" \
  --descriptor-signature "$handoff_signature" \
  --source-root "$repo_root" \
  --response-json "$response_json" \
  --response-b64 "$response_b64"
