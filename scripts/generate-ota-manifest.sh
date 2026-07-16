#!/usr/bin/env bash
set -euo pipefail

# Normal production OTA manifests use the protected release/hotfix workflows
# and the non-exportable ota-update-v2 AWS KMS key through GitHub OIDC. The only
# local exception is the bounded, owner-only legacy v1 Release 1 ceremony in
# sign-ota-v1-bridge-local.sh; this generic upload fallback remains forbidden.
echo "Local OTA release signing is disabled. Use the protected SuavoAgent production release workflow." >&2
exit 2
