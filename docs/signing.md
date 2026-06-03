# Authenticode signing runbook

SuavoAgent binaries are signed with an **SSL.com EV Code Signing Certificate**
bound to **SSL.com eSigner cloud HSM** (no physical token). The cert lives in
SSL.com's FIPS 140-2 validated cloud key vault; signing happens via the
`sslcom/actions-codesigner` GitHub Action in CI. No self-hosted runner is
required.

The release and hotfix workflows **fail closed** unless `SIGNING_ENABLED=true`
and all four `ES_*` secrets are configured. Flip the gate only once the
secrets are populated and a smoke tag has signed successfully.

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

The existing `SIGNING_KEY_PEM` secret stays as-is — it signs `checksums.sha256`
and the OTA update manifest, not the binaries themselves.

The deprecated `SIGNING_CERT_THUMBPRINT` and `SIGNING_TIMESTAMP_URL`
variables/secrets are no longer referenced by either workflow; safe to delete.

### 4. Smoke test with a throwaway tag

```bash
git tag v3.13.99-esigner-smoke
git push origin v3.13.99-esigner-smoke
```

Watch the Actions run. Expected sequence:

- `release-signing-preflight` (ubuntu) — verifies all four `ES_*` secrets are
  set, fails fast if any are missing
- `bootstrap-windows-smoke` (windows) — parse-checks `bootstrap.ps1` under PS 5.1
- `build` (ubuntu) — `dotnet publish` for each of the 5 binaries
- `sign_windows` (ubuntu) — 5 sequential calls to `sslcom/actions-codesigner@develop`,
  one per binary; each call talks to SSL.com's cloud HSM over HTTPS and signs
  the binary in-place
- `windows-release-smoke` (windows) — expands the zip and asserts every binary
  passes `Get-AuthenticodeSignature -RequireAuthenticodeSignature`
- `release` (ubuntu) — generates checksums, signs the manifest, publishes the
  GitHub Release

Download `SuavoAgent.Core.exe` from the release → Properties → Digital
Signatures. You should see `MKM Technologies LLC` with a valid timestamp.

If the smoke run is green, delete the tag + release:

```bash
git push origin :refs/tags/v3.13.99-esigner-smoke
gh release delete v3.13.99-esigner-smoke --yes
```

### 5. Verify SmartScreen on a fresh Windows machine

Download the `.cmd` installer from a pharmacy signup page and run it on a
Windows machine that has never seen the agent. The UAC prompt should read
`Verified publisher: MKM Technologies LLC` instead of `Publisher unknown`.

## Local signing on a Windows dev box (optional)

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
  remains, and only fires on Windows boxes with CKA installed)
- `Directory.Build.props` — PE metadata (Company, Copyright, Version)
- `bootstrap.ps1` — client installer; verifies `checksums.sha256` signature
  (separate ECDSA P-256 key, `SIGNING_KEY_PEM`)
- Memory: `ssl-com-ev-cert-validation-submitted.md`,
  `session-end-2026-05-15-mesh-end-to-end-shipped.md`
