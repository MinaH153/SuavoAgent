# Authenticode signing runbook

> **INTERNAL RELEASE ENGINEERING.** Commands in this file are for controlled CI
> and certificate operations, never for a customer workstation. The approved
> customer lifecycle is `docs/sales/windows-agent-lifecycle.md`.

SuavoAgent binaries are signed with an **SSL.com EV Code Signing Certificate**
bound to **SSL.com eSigner cloud HSM** (no physical token). The cert lives in
SSL.com's FIPS 140-2 validated cloud key vault; signing happens via the
`sslcom/actions-codesigner` GitHub Action in CI. No self-hosted runner is
required.

The release and hotfix workflows **fail closed** unless their protected
`suavoagent-production-signing` environment has approved reviewers, all four
`ES_*` secrets, the exact Authenticode signer-certificate SHA-256 allowlist,
and the OIDC/AWS KMS configuration for the non-exportable `ota-update-v1`
key. It also requires recorded WiX Open Source Maintenance Fee EULA acceptance
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

**Pre-sign malware scan (`malware_block`):** consider setting
`malware_block: 'true'` on the `sslcom/actions-codesigner` steps so signing
*refuses* on malware detection — this protects the cert from **negative**
reputation. Not enabled here because it can block a release on a heuristic
false-positive (installers sometimes trip these); **smoke-test on a throwaway
tag before enabling** in `release.yml` / `hotfix.yml`.

**The signing action is pinned to a commit SHA**, not `@develop`. A moving
branch that signs your binaries is a supply-chain risk that could poison the
cert's reputation org-wide. To update: review upstream changes, then bump the
SHA in both `release.yml` and `hotfix.yml` (the repo publishes no release tags,
so a SHA is the only immutable pin).

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

### 3. Configure GitHub Actions secrets

On GitHub → **MinaH153/SuavoAgent → Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `ES_USERNAME` | SSL.com account email |
| Secret | `ES_PASSWORD` | SSL.com account password |
| Secret | `ES_CREDENTIAL_ID` | GUID from step 2 |
| Secret | `ES_TOTP_SECRET` | Base32 string from step 1 |
| Variable | `SIGNING_ENABLED` | `true` |
| Variable | `AUTHENTICODE_SIGNER_SHA256` | Exact 64-hex SHA-256 digest of each approved signer certificate, comma-separated during rotation |
| Variable | `AWS_SIGNING_ROLE_ARN` | Least-privilege OIDC role allowed only to get/sign with the OTA KMS key |
| Variable | `AWS_SIGNING_REGION` | Region containing the OTA KMS key |
| Variable | `OTA_KMS_KEY_ID` | Non-exportable P-256 `ota-update-v1` KMS key ARN |
| Variable | `OTA_KMS_PUBLIC_KEY_DER_BASE64` | Reviewed SPKI DER that exactly matches the runtime-pinned OTA key |
| Variable | `WIX_OSMF_EULA_ACCEPTED` | Exactly `true` only after the legal owner records acceptance of the WiX 7 OSMF EULA |
| Variable | `VC_REDIST_X64_URL` | HTTPS URL for the reviewed Microsoft VC++ 14.44.35211.0 x64 prerequisite whose SHA-256 is pinned in the workflow |

Delete the retired `SIGNING_KEY_PEM` secret after confirming no older workflow
references it. Exportable PEM release signing is forbidden. Brain model and
brain native manifests use two additional, independent non-exportable roots;
neither may reuse the OTA key or each other.

The deprecated `SIGNING_CERT_THUMBPRINT` and `SIGNING_TIMESTAMP_URL`
variables/secrets are no longer referenced by either workflow; safe to delete.

### 4. Smoke test with a throwaway tag

```bash
git tag v3.13.99-esigner-smoke
git push origin v3.13.99-esigner-smoke
```

Watch the Actions run. Expected sequence:

- `release-signing-preflight` (ubuntu) — waits for protected-environment review
  and verifies eSigner, exact signer digest, OIDC role, and KMS inputs
- `production-shell-boundary` (ubuntu) — rejects a script-based production or
  customer lifecycle
- `build` (ubuntu) — builds, tests, and publishes the declared Windows cohort
- `sign_windows` (ubuntu) — 5 sequential calls to the commit-pinned SSL.com
  code-signing action, one per binary; each call talks to SSL.com's cloud HSM
  over HTTPS and signs the binary in-place
- `build_msi` / `sign_msi` — builds the WiX MSI from the immutable signed
  five-binary cohort, then signs the MSI through the protected publisher key
- `build_bundle` / `sign_bundle` — embeds that already-signed MSI and the exact
  hash-pinned VC++ runtime in a native Burn installer without rebuilding the MSI
- `windows-release-smoke` (windows) — verifies the signed bundle and MSI,
  silently installs the MSI, proves all required services were registered,
  uninstalls it, scans with Defender, and checks the internal signed cohort
- `release` (ubuntu) — assumes the least-privilege AWS role through OIDC, asks
  KMS to sign checksums/OTA bytes, emits provenance attestations, and publishes

Download `SuavoAgent.Core.exe` from the release → Properties → Digital
Signatures. You should see `MKM Technologies LLC` with a valid timestamp.

If the smoke run is green, delete the tag + release:

```bash
git push origin :refs/tags/v3.13.99-esigner-smoke
gh release delete v3.13.99-esigner-smoke --yes
```

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

- `.github/workflows/release.yml` — tag-triggered release pipeline
- `.github/workflows/hotfix.yml` — manual-dispatch hotfix pipeline
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
