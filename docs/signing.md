# Authenticode signing runbook

> **INTERNAL RELEASE ENGINEERING.** Commands in this file are for controlled CI
> and certificate operations, never for a customer workstation. The approved
> customer lifecycle is `docs/sales/windows-agent-lifecycle.md`.

SuavoAgent binaries are signed with an **SSL.com EV Code Signing Certificate**
bound to **SSL.com eSigner cloud HSM** (no physical token). The cert lives in
SSL.com's FIPS 140-2 validated cloud key vault. CI uses exact Temurin
`11.0.31+11` plus the official CodeSignTool v1.3.0 archive at SHA-256
`359782cee5c709b172610e2abd8cb49445bfadd26f44073ca18600c585b91b8d`.
The repository wrapper invokes the verified JAR with a Bash argument array; it
does not use the deprecated Docker action or rebuild a secret-bearing shell
command. No self-hosted runner is required.

The release and hotfix workflows **fail closed** unless their protected
`suavoagent-production-signing` environment has approved reviewers, all four
`ES_*` secrets, the exact Authenticode signer-certificate SHA-256 allowlist,
and the exact OIDC/AWS KMS configuration for the non-exportable
`ota-update-v2` key. The legacy exportable `ota-update-v1` key is not in AWS;
it is accepted only by the bounded one-time Release 1 bridge ceremony below.
The workflows also require recorded WiX Open Source Maintenance Fee EULA acceptance
and the reviewed VC++ prerequisite URL. Flip the gate only after that
environment is configured and reviewed.

## SmartScreen reputation & certificate continuity — READ THIS

**EV no longer grants instant SmartScreen trust.** Microsoft removed that
behavior ~March 2024 ([MS Learn, updated 2026-05-04](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).
A valid EV signature only makes the **publisher name** show in the prompt — it
does **not** skip the "Windows protected your PC / unrecognized app" warning.
SmartScreen reputation now accrues **per signing certificate, over real-world
download volume** ("hundreds of clean installs from a wide audience," several
weeks — Microsoft's own wording). Internal testing barely counts. **A new cert
with low volume showing the warning is EXPECTED, not a bug** — don't go hunting
for a signing misconfiguration.

**🔒 NEVER reissue or renew this certificate early.** Reputation is bound to the
**certificate thumbprint**. Renewing/reissuing — even same org, same CA — is a
**new publisher identity to SmartScreen and resets all accrued reputation to
zero, with no transfer path.** The continuity of *this* cert (order
`co-861kueeu2a3`, valid to 2027-05-15) is the reputation asset. Therefore:

- Sign **every** release with this same cert. The cert is a reputation bank
  account that only compounds while it's the same thumbprint.
- Renew **deliberately near expiry**, accepting the reputation reset — never
  reissue casually.
- Never ship an unsigned binary, and never modify a binary after signing (both
  break the trust chain / the signature).

**Do NOT switch to Azure Trusted Signing to "fix" SmartScreen** — it uses the
same reputation model (no instant trust) and switching would discard the
reputation this cert is building. Reconsider only at a natural renewal.

**Free shot:** file a WDSI software-developer submission for the signed
`SuavoAgent-Setup.exe` bundle at
<https://www.microsoft.com/en-us/wdsi/filesubmission> (low effort, uncertain
payoff; re-submit per new version/hash).

**Pre-sign malware scan (`malware_block`):** the hardened wrapper requires this
for every executable, MSI, and Burn signature. A heuristic false positive blocks
publication and must be investigated; do not bypass the gate.

**The signer supply chain is immutable.** The Java setup action is pinned to a
commit SHA and verifies the Temurin package signature. CodeSignTool uses an
exact GitHub release URL plus the reviewed archive digest. To update either,
review the upstream source and archive first, then change the pin and tests
together in all three workflows.

## State when this doc was written (2026-05-16)

- SSL.com order `co-861kueeu2a3` — **issued + eSigner active** (Apr 21, 2026 →
  May 15, 2027)
- Legal entity on cert: `CN=MKM TECHNOLOGIES LLC, O=Suavo (MKM TECHNOLOGIES LLC)`
- Original plan was Yubikey FIPS hardware token; SSL.com enrolled the order in
  eSigner cloud signing instead, which is strictly better for CI: no USB token,
  no self-hosted runner, no SmartCard service. The original Yubikey workflow is
  preserved in git history (`.github/workflows/release.yml` at `ccf0ab1` and
  earlier) if a future migration back to hardware is ever needed.

## Activation checklist

### 1. Enroll the certificate with eSigner (~10 min, one-shot)

Log into <https://secure.ssl.com>. Open order `co-861kueeu2a3`. On the order
detail page find the **eSigner Cloud Signing Enrollment** section.

1. **Second factor authentication** → select **OTP APP** (do NOT pick SMS — the
   CI needs the raw TOTP secret, which SMS doesn't expose)
2. **Create a 4-digit PIN** → save to 1Password as "SSL.com eSigner PIN"
   (separate from the SSL.com account password; cannot be recovered if lost)
3. Click **create OTP and issue certificate**
4. A QR code appears **once**. Before navigating away:
   - Scan with Authy or Google Authenticator on your phone (normal 2FA)
   - **Also extract the raw secret string**. The QR encodes
     `otpauth://totp/SSL.com:<user>?secret=BASE32STRING&issuer=SSL.com`.
     Either click "show key" / "manual entry" if SSL.com offers it, or
     screenshot the QR and decode it with any QR reader. Save the `secret=`
     value as "SSL.com eSigner TOTP secret" in 1Password.

If you miss the QR, you can reset it via the "view/reset eSigner QR code"
flow on SSL.com — but this invalidates the previous OTP setup.

### 2. Get the credential ID (~5 min)

Download CodeSignTool for macOS:
<https://www.ssl.com/download/codesigntool-for-linux-and-macos/>. Unzip, then:

```bash
./CodeSignTool.sh get_credential_ids \
  -username="<your-ssl.com-email>" \
  -password="<your-ssl.com-password>"
```

The command prints a GUID. Save as "SSL.com eSigner credential ID" in
1Password.

### 3. Configure the protected GitHub environments

All production signing authority belongs only to the
`suavoagent-production-signing` environment. Configure that environment before
any bridge run:

- deployment branches: protected `main` only; do not allow `v*` tags;
- required reviewer: a person other than the dispatcher;
- prevent self-review: enabled;
- administrator bypass: disabled; and
- repository rules: immutable releases plus a stable
  `vMAJOR.MINOR.PATCH` tag ruleset with **restrict updates** and
  **restrict deletions** enabled, **restrict creations** disabled, and no bypass.
  The workflow must be able to create the exact new tag for its draft, but no
  actor may move or delete it during validation.

Create the four eSigner secrets and all variables below **at environment scope**,
not repository scope:

| Kind | Name | Required value |
| --- | --- | --- |
| Secret | `ES_USERNAME` | SSL.com account email |
| Secret | `ES_PASSWORD` | SSL.com account password |
| Secret | `ES_CREDENTIAL_ID` | GUID from step 2 |
| Secret | `ES_TOTP_SECRET` | Base32 string from step 1 |
| Variable | `SIGNING_ENABLED` | Exact lowercase `true` |
| Variable | `AUTHENTICODE_SIGNER_SHA256` | Exact 64-hex SHA-256 digest of each approved signer certificate; comma-separated only during reviewed rotation |
| Variable | `AWS_SIGNING_ROLE_ARN` | `arn:aws:iam::855763870758:role/github-actions/SuavoAgentProductionOtaSigningV2` |
| Variable | `AWS_SIGNING_REGION` | `us-east-1` |
| Variable | `OTA_KMS_KEY_ID` | `arn:aws:kms:us-east-1:855763870758:key/44bd84dc-8f6d-4692-b8ba-40a026db0331` |
| Variable | `OTA_KMS_PUBLIC_KEY_DER_BASE64` | Reviewed Base64 SPKI DER for that exact key; it must match source-controlled `ota-update-v2` |
| Variable | `OTA_FULL_COHORT_MANIFEST` | Leave unset through Release 1 and convergence; set to exact lowercase `true` only for normal v2 activation |
| Variable | `WIX_OSMF_EULA_ACCEPTED` | Exact lowercase `true` only after the legal owner records acceptance |
| Variable | `VC_REDIST_X64_URL` | Reviewed HTTPS URL for the SHA-256-pinned VC++ 14.44.35211.0 x64 prerequisite |

The four KMS variables are required **before the Release 1 authorizer** because
v2 signs the handoff descriptor. That does not activate v2 OTA signing: keep
source `signingKeyId` on `ota-update-v1` and keep
`OTA_FULL_COHORT_MANIFEST` unset until convergence is complete.

After the environment values are verified, delete every repository-level copy
of `ES_USERNAME`, `ES_PASSWORD`, `ES_CREDENTIAL_ID`, and `ES_TOTP_SECRET` and
delete repository secret `SIGNING_KEY_PEM`. An exportable OTA private key in
GitHub is forbidden. Also remove deprecated `SIGNING_CERT_THUMBPRINT` and
`SIGNING_TIMESTAMP_URL` values if present. Brain model and native-manifest keys
remain separate and may not reuse either OTA root.

Create `suavoagent-v1-bridge-finalization` as a second, main-only protected
environment with the same independent-review, no-bypass, and no-self-review
controls. It must contain **no** eSigner secret, AWS variable, OIDC authority,
or other signing credential.

### AWS KMS v2 root and GitHub OIDC role: update-only

Stack `SuavoAgentProductionOtaSigningV2` already owns all five production
resources: `GitHubActionsOidcProvider`, `GitHubProductionSigningRole`,
`OtaSigningKey`, `OtaSigningKeyAlias`, and
`GitHubProductionSigningRoleInlinePolicy`. The existing key is the exact ARN
above and the existing alias is
`alias/suavoagent-production-ota-update-v2`.

`infrastructure/aws/suavoagent-production-signing-v2.template.json` is only an
**in-place update template for that existing stack**. Never create a new stack,
never import a replacement resource, and never update the live stack from a
role-only or otherwise resource-removing template. Removing a logical resource
can delete the OIDC provider or alias and detach the sole signing root. The
template deliberately preserves the exact live logical IDs, OIDC provider,
KMS key properties and policy, alias target, role policy, outputs, and the
key's `DeletionPolicy: Retain` plus `UpdateReplacePolicy: Retain`.

Before any future AWS change, create a reviewed CloudFormation change set
against the existing stack in account `855763870758`, region `us-east-1`. Stop
unless the change set proves:

- no resource is added, removed, imported, or replaced;
- `OtaSigningKey` retains logical and physical identity, exact ARN, P-256
  `SIGN_VERIFY`/`AWS_KMS` properties, key policy, and both retention policies;
- `OtaSigningKeyAlias` still targets `Ref: OtaSigningKey`;
- the OIDC provider and inline KMS use policy are unchanged; and
- only the role description and three exact trust statements change.

The role trust has three independent statements, never a caller/reusable-workflow
cross product: bridge authorizer → `production-signing.yml@main`, Release →
`production-release-signing.yml@main`, and Hotfix → the same normal signer.
Every statement additionally binds the exact repository IDs, protected `main`,
and `suavoagent-production-signing` environment. The role and KMS key policy
allow only `kms:GetPublicKey` and `kms:Sign`; signing is `RAW` +
`ECDSA_SHA_256`. After a reviewed update, independently fetch the exact KMS
public key and compare its SPKI bytes with the pinned source root and
`OTA_KMS_PUBLIC_KEY_DER_BASE64`. This work did not create or execute a live
change set.

### OTA root rotation: exact v1-to-v2 ceremony

The reviewed public roots live in `security/ota-update-trust-roots.json`.
Public keys are not secrets. The v2 private key remains non-exportable in KMS;
no OTA private key may enter this repository, a workflow secret, or an artifact.
The owner-only local v1 PEM is a bounded Release 1 exception and must remain
mode `0600`, outside the repository, and on the owner machine.

Run the rotation in this order and stop on any mismatch:

1. Merge the complete bridge, normal-release, runtime dual-root, and test changes
   to protected `main`.
2. Apply the two GitHub environment protections described above.
3. Move `ES_*` values to environment scope and delete repository copies plus
   `SIGNING_KEY_PEM`.
4. Review an **in-place** CloudFormation change set for the existing stack,
   preserving every resource and the exact KMS physical key. Do not deploy from
   this runbook without that independent review.
5. Enable immutable releases and the stable-tag ruleset: restrict updates and
   deletions, allow creation, and configure no bypass.
6. Set the four exact KMS environment variables needed by the authorizer. Keep
   source `signingKeyId` on `ota-update-v1` and
   `OTA_FULL_COHORT_MANIFEST` unset.
7. Execute stage → v2 authorization → owner-local v1 signing → finalization.
8. Reinstall Release 1 on the exact registered fleet and collect the signed
   install receipts, close and v2-sign the inventory epoch, then force the
   post-freeze restart and v1 no-op rehearsal on every host. Only after the
   exact evidence set verifies may the owner sign the convergence claim with
   historic v1 and activate v2 in a separate reviewed commit.

The bridge additionally pins the installed v3.92.1 v1 SPKI SHA-256 to
`b3f5ddda0654713de31e6cbe3ae3b49ed53575d0938d4149779361c6d739e970`
and v2 SPKI SHA-256 to
`6e4092980b1185627200476806d5063c43df77e5ac000b6b6ba72df89eb1406f`.
If the local v1 key does not derive the exact v1 root, stop; strict OTA
continuity cannot be recovered by substituting another key.

#### Release 1: authenticated handoff and v1 publication

Fetch the current authoritative `main`, choose an unused stable version, and
dispatch staging. Staging produces only the exact immutable request artifact;
it cannot assume AWS, tag, attest, or publish.

```bash
git fetch --no-tags origin main
SHA="$(git rev-parse origin/main)"
VERSION="vMAJOR.MINOR.PATCH"
gh workflow run v1-bridge-stage.yml --ref main \
  -f source_sha="$SHA" -f version="$VERSION"
```

After staging succeeds, record its numeric run ID and attempt. Dispatch the
separate protected authorizer, then record its run ID and attempt. The authorizer
revalidates current protected `main`, authenticates the exact request-artifact
digest, and uses v2 only to sign the handoff descriptor.

```bash
gh workflow run v1-bridge-authorize.yml --ref main \
  -f stage_run_id="STAGE_RUN_ID"

gh run download "STAGE_RUN_ID" \
  -n "suavoagent-v1-bridge-request-STAGE_RUN_ID-STAGE_RUN_ATTEMPT" \
  -D ../v1-bridge-stage
gh run download "AUTHORIZATION_RUN_ID" \
  -n "suavoagent-v1-bridge-descriptor-STAGE_RUN_ID-STAGE_RUN_ATTEMPT-authorization-AUTHORIZATION_RUN_ID-AUTHORIZATION_RUN_ATTEMPT" \
  -D ../v1-bridge-authorization
```

On the owner machine, use a completely clean checkout whose `HEAD` still equals
the current authoritative `main`. The six-argument wrapper verifies the exact
v2-authenticated descriptor and request bytes before using v1. Inputs and
outputs must be outside the repository.

```bash
git switch --detach "$SHA"
scripts/sign-ota-v1-bridge-local.sh ~/.suavo/signing-key.pem \
  ../v1-bridge-stage \
  ../v1-bridge-authorization/bridge-handoff-descriptor.json \
  ../v1-bridge-authorization/bridge-handoff-descriptor.sig \
  ../v1-bridge-response.json ../v1-bridge-response.b64
```

If `main` advances at any point, discard the artifacts and rerun **both** stage
and authorization at the new current tip. Do not sign an old request.

Finally, dispatch with both exact run IDs. The finalizer has no KMS/eSigner
authority. It checks both successful run attempts and artifacts, verifies the
v2 descriptor, verifies both Release 1 signatures specifically under v1,
re-smokes Windows, rechecks current protected `main`, creates a draft targeted
to the exact source, validates its assets, rechecks `main` again, then publishes.

```bash
gh workflow run v1-bridge-finalize.yml --ref main \
  -f stage_run_id="STAGE_RUN_ID" \
  -f authorization_run_id="AUTHORIZATION_RUN_ID" \
  -f response_b64="$(tr -d '\r\n' < ../v1-bridge-response.b64)"
```

The published manifest and `checksums.sha256` must verify under v1, and Core,
Broker, Helper, Watchdog, Setup/Maintenance, and the maintenance receipt path
must all contain the dual-root verifier.

#### Mandatory fleet migration and convergence

**Mandatory fleet migration:** install Release 1 through its signed graphical
Burn bundle or MSI on every registered host. A legacy 11-field OTA may replace
four executables without replacing the old maintenance authority; therefore
11-field OTA success is **not** trust convergence. Every host must prove a full
installer reinstall, the exact Release 1 identity and release bindings, an
observed restart, a successful v1-signed no-op rehearsal, and PHI-negative
evidence. Record that as signed, PHI-negative convergence evidence.
If even one registered host cannot complete that path, stop and design an
additional v1-authorized strict-upgrade migration release; do not activate v2.

The source gate recognizes exactly three states:

| Phase | Selected root | Source-controlled convergence artifacts |
| --- | --- | --- |
| `bridge-v1` | `ota-update-v1` | None of the five artifacts |
| `convergence-v1` | `ota-update-v1` | Exact inventory JSON, inventory signature, and evidence JSON; claim outputs absent |
| `normal-v2` | `ota-update-v2` | All three inputs plus the claim JSON and historic-v1 claim signature |

CI uses `--trust-phase auto` and rejects every mixed or partial state. Release 1
contains no claim or provisional evidence.

The convergence inputs are closed and cryptographically separated:

- inventory schema 3 is an authoritative, PHI-negative snapshot signed by v2.
  It carries a positive fleet-registry epoch, exact issue and expiry timestamps,
  `enrollmentClosed: true`, the registered-host count, and the digest of the
  exact sorted host set. That set digest is SHA-256 over canonical JSON of the
  sorted host-digest array, including the contract's single trailing LF. Its
  validity window may not exceed seven days. Each
  `registeredHosts` entry binds one opaque host digest to two distinct enrolled
  P-256 authorities: the ordinary device key and the SYSTEM-only maintenance
  key. Both key IDs are the raw lowercase 64-hex SHA-256 digest of their exact
  SPKI DER and every key is unique across the snapshot. `releaseBindings` bind
  the exact Burn, MSI, Core, Broker, Helper, Watchdog, Maintenance, release
  receipt, checksum, checksum-signature, update-manifest, and
  update-manifest-signature digests;
- evidence schema 4 contains the exact same sorted host set. Each machine wraps
  a closed schema-2 attestation signed by its enrolled device key with an exact
  unpadded 64-byte P1363 Base64Url signature. The attestation nests and hashes
  an exact full-install receipt, restart receipt, and v1 no-op receipt. The
  install receipt is independently P1363-signed by the enrolled maintenance key
  and binds the installer, complete five-binary Release 1 cohort, install
  transaction, install time, and install boot. The restart receipt binds that
  install-receipt hash, must be observed after the inventory epoch is closed,
  and proves a different post-restart boot ID. The no-op
  receipt binds the inventory/install/restart hashes and embeds the exact
  canonical Release 1 manifest plus its lowercase P1363 signature, which is
  verified specifically under historic v1. Legacy booleans and arbitrary
  receipt digests are not accepted; and
- claim schema 4 binds the inventory bytes and signature path, evidence bytes,
  epoch, validity window, exact host-set digest, counts, and Release 1 identity,
  then is signed specifically by historic v1. Historic-v1 claim creation must
  occur after all machine evidence and within the signed inventory validity
  window. Normal post-claim verification remains durable after that window
  expires; expiry prevents creating a new claim, not replaying the already
  signed claim as a permanent source-controlled trust fact.

The existing non-exportable TPM device-key enrollment path is the machine
attestation authority; do not create a parallel software key or prefixed key-ID
format. The convergence contract also uses a distinct SYSTEM-only TPM
maintenance key for the installer receipt.

**Current blocker:** the wire contracts, offline verifier, and TPM key primitives
do not themselves establish the authoritative fleet. Production still needs a
reviewed fleet-registry exporter/campaign authority that closes one epoch,
creates and v2-signs the exact snapshot from the enrolled keys and signed
install receipts, distributes that exact inventory to the registered hosts,
forces a post-freeze restart and v1 no-op rehearsal, collects the resulting
evidence, and publishes one duplicate-free bundle only after exact-set equality.
The raw same-version OTA no-op record is not the final nested convergence
receipt or a collector. Normal v2 release therefore remains intentionally
source-impossible until those operational authorities produce authentic files.
Do not fabricate inventory/evidence, omit an unreachable host, reuse either key
across hosts, or sign host receipts with the central v2 KMS key.

Once those systems produce authentic inputs, commit exactly these three files
while `signingKeyId` remains v1:

- `security/ota-fleet-inventory-snapshot.json`
- `security/ota-fleet-inventory-snapshot.sig`
- `security/ota-v1-bridge-convergence-evidence.json`

From a clean current-main checkout in `convergence-v1`, create the historic-v1
claim outside the repository; the expected count comes from the signed inventory
and exact evidence-set equality, not a command-line assertion.

```bash
mkdir -m 700 ../v1-convergence-output
scripts/sign-ota-v1-convergence-local.sh ~/.suavo/signing-key.pem \
  ../v1-convergence-output RELEASE1_TAG RELEASE1_SOURCE_SHA
cp ../v1-convergence-output/ota-v1-bridge-convergence-claim.{json,sig} security/
```

Review the claim, then make one activation commit that adds both claim files and
changes only source `signingKeyId` to `ota-update-v2`. CI must auto-resolve
`normal-v2` and reverify the v2 inventory envelope, every enrolled device and
maintenance signature, every nested receipt, exact host-set equality, and the
historic-v1 claim. Only after that
commit is protected `main` may the environment variable
`OTA_FULL_COHORT_MANIFEST` become exact lowercase `true`.

#### Normal Release 2 and later

Normal release and hotfix workflows are manual dispatches from exact current
protected `main`; they are not tag triggers. Do not create a disposable
production tag or release: immutable release/tag protection deliberately makes
that smoke pattern invalid. Dispatch only a real version after all gates exist:

```bash
gh workflow run release.yml --ref main -f version="vMAJOR.MINOR.PATCH"
# Emergency path only:
gh workflow run hotfix.yml --ref main \
  -f version="vMAJOR.MINOR.PATCH" -f description="REVIEWED_DESCRIPTION"
```

The caller builds and tests the exact cohort; the local reusable
`production-release-signing.yml@main` independently proves the closed caller,
current protected `main`, live Release 1, normal-v2 convergence, exact KMS root,
and 900-second step-scoped credentials. It signs, creates one exact draft,
validates assets and tag target, rechecks `main`, and only then publishes.

After Release 2, prove every converged Release 1 host accepts v2 and prove the
signed rollback still resolves. Only then destroy the legacy local v1 PEM.
Retain the v1 **public** root through the rollback horizon; later public-root
removal is a separate reviewed release.

### 5. Verify SmartScreen on a fresh Windows machine

Download `SuavoAgent-Setup.exe` from the authenticated pharmacy
dashboard on a Windows machine that has never seen the agent. Check
**Properties → Digital Signatures**, then open it. The signature must be valid
and the UAC prompt must read `Verified publisher: MKM Technologies LLC`, not
`Publisher unknown`.

Complete the native install, diagnostics, repair, update, and uninstall checks
in `docs/hardening/release-gate.md`. SmartScreen may still show a reputation
warning for a new certificate; that does not permit bypassing a missing or
invalid publisher signature.

## Local signing on a Windows dev box (optional; internal engineering only)

If you want `publish.ps1 -CertThumbprint <SHA1>` to sign locally on a Windows
dev machine without going through CI, install
**[eSigner CKA (Cloud Key Adapter)](https://www.ssl.com/download/#cka)**. CKA
registers the cloud cert in `Cert:\CurrentUser\My` so the existing
`signtool /sha1 <thumbprint>` flow in `publish.ps1` keeps working. CKA prompts
for the OTP / TOTP on each signing operation.

CKA is Windows-only — there is no macOS equivalent. On Mac dev boxes, signing
only happens in CI.

## Troubleshooting

**`Error: USER_CRED_INVALID` from CodeSignTool** — `ES_USERNAME` or
`ES_PASSWORD` is wrong, or the account is locked. Try logging into
secure.ssl.com manually.

**`Error: INVALID_TOTP` from CodeSignTool** — `ES_TOTP_SECRET` doesn't match
the QR enrolled in step 1. Re-enroll (resets the QR) and update the secret.

**`Error: CREDENTIAL_NOT_FOUND`** — `ES_CREDENTIAL_ID` GUID is wrong. Re-run
`CodeSignTool get_credential_ids` to confirm.

**`HTTP 429 Rate limit exceeded`** — SSL.com's eSigner has a per-account
signature rate limit (undocumented, anecdotally ~100/hour for standard
accounts). Spread sign calls across time, or contact SSL.com support to lift
the cap if shipping high-volume releases.

**SmartScreen still warns after signing** — Expected for the first ~30 days
with a brand-new EV cert. Windows Defender builds reputation on signed EXEs
over downloads. EV cert removes the warning sooner than an OV cert but doesn't
eliminate the reputation warmup entirely.

## Rollback

To disable signing without reverting the workflows:

```
SIGNING_ENABLED = false   # or delete the variable
```

The next release/hotfix preflight will fail closed. Do not validate Queen
from an unsigned passthrough artifact.

## Future: Azure Trusted Signing migration

Azure Trusted Signing is $9.99/month for unlimited signatures but requires a
**3-year-old verified organization**. MKM Technologies LLC was filed
2026-03-23, so it's ineligible until approximately **March 2029**. At renewal
time (Apr 2027) the choice is:

1. **Renew SSL.com EV cert** (~$349/year) — reuse eSigner cloud; zero workflow
   changes. Default choice unless a clearly better option emerges.
2. **Wait until Mar 2029 and migrate to Azure Trusted Signing** — cheapest
   long-term ($120/year vs $349/year), requires re-keying CI workflow.

Note: certs issued on or after Mar 1, 2026 are capped at 458-day validity by
CA/Browser Forum baseline requirements. The current cert (Apr 21, 2026 → May
15, 2027) is 389 days. Future renewals will also be ≤458d, so plan on
auto-renewal in the release pipeline before expiry.

## Related files

- `.github/workflows/release.yml` — manual protected-main normal release caller
- `.github/workflows/hotfix.yml` — manual protected-main emergency caller
- `.github/workflows/production-release-signing.yml` — closed normal-v2 KMS signer and draft-first publisher
- `.github/workflows/v1-bridge-stage.yml` — one-time exact-main Release 1 request staging
- `.github/workflows/v1-bridge-authorize.yml` and `production-signing.yml` — separate protected v2 handoff authorization
- `.github/workflows/v1-bridge-finalize.yml` — no-signing-authority v1 verification, re-smoke, attestation, and exact-SHA publication
- `scripts/verify-live-v1-bridge-release.sh` — prove immutable live Release 1 before normal publication
- `scripts/sign-ota-v1-convergence-local.sh` and `scripts/v1_bridge_convergence.py` — closed inventory/device-evidence validation and historic-v1 claim ceremony
- `scripts/Test-QueenShipPreflight.ps1` — local Queen build-readiness probe
  (signing checks now skipped by default; only the EV cert thumbprint check
  remains, and only fires on Windows boxes with CKA installed). This is internal
  release engineering tooling, never a customer install or support step.
- `Directory.Build.props` — PE metadata (Company, Copyright, Version)
- `installer/SuavoAgent.Msi/` and `installer/SuavoAgent.Bundle/` — customer MSI
  and native Burn installer
- `src/SuavoAgent.Setup/` — signed GUI and staged native maintenance host
- `docs/sales/windows-agent-lifecycle.md` — approved customer lifecycle
- Memory: `ssl-com-ev-cert-validation-submitted.md`,
  `session-end-2026-05-15-mesh-end-to-end-shipped.md`
