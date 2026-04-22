# Authenticode signing runbook

SuavoAgent binaries are signed with an **SSL.com EV Code Signing Certificate**
bound to a **Yubikey FIPS 140-2 hardware token**. The Yubikey must be
physically plugged into a self-hosted GitHub Actions runner at release time —
it cannot leave that machine. EV certs cannot be exported as PFX.

The release and hotfix workflows are pre-wired for signing, but **gated off
by default** via the `SIGNING_ENABLED` repository variable. Flip the gate
once the hardware has arrived and the runner is configured (see below).

## State when this doc was written (2026-04-22)

- SSL.com order `co-861kueeu2a3` — **pending validation**
- Legal entity on cert: `CN=MKM TECHNOLOGIES LLC, O=Suavo (MKM TECHNOLOGIES LLC)`
- Validation SLA: 3–5 business days → Yubikey FedEx ships 2–3 business days
  after validation completes
- Delivery address: 5310 Fountain Grass Avenue, Bakersfield, CA 93313
- Until the Yubikey arrives, `SIGNING_ENABLED` is unset (or explicitly `false`)
  and the `sign_passthrough` job runs. Releases carry the usual SmartScreen
  "publisher unverified" warning.

## Activation checklist (when Yubikey arrives)

### 1. Install prerequisites on the signing machine

The signing machine must be running Windows 10/11 x64 with:

- **Windows 10 SDK** — provides `signtool.exe` in
  `C:\Program Files (x86)\Windows Kits\10\bin\<sdk-version>\x64\`.
  If multiple SDK versions are installed, the workflow picks the newest.
- **Yubikey Manager + minidriver** — from <https://www.yubico.com/support/download/>
  so the Yubikey's certificate appears in Windows' `Cert:\CurrentUser\My` store.
- **SSL.com eSigner bundle** (shipped with the Yubikey) — installs the
  SafeNet Authentication Client + drivers that signtool uses under the hood.
- **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8`.
- **Git for Windows** — needed by `actions/checkout`.

### 2. Import the cert and grab its thumbprint

Plug the Yubikey in, then in an admin PowerShell:

```powershell
# Lists certs on the token. Copy the thumbprint of the EV cert
# (Subject should include "MKM TECHNOLOGIES LLC").
Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like '*MKM TECHNOLOGIES LLC*' } | Format-List Subject, Thumbprint, NotAfter
```

Test-sign a throwaway EXE to confirm the Yubikey PIN flow works interactively:

```powershell
Copy-Item C:\Windows\System32\notepad.exe $env:TEMP\sign-test.exe
& "C:\Program Files (x86)\Windows Kits\10\bin\<sdk>\x64\signtool.exe" sign /fd SHA256 /tr http://ts.ssl.com /td SHA256 /sha1 <THUMBPRINT> $env:TEMP\sign-test.exe
& "C:\Program Files (x86)\Windows Kits\10\bin\<sdk>\x64\signtool.exe" verify /pa /v $env:TEMP\sign-test.exe
```

You should be prompted for the Yubikey PIN on `sign`, and `verify` should
print `Successfully verified`.

### 3. Register the runner with GitHub

On github.com → **Settings → Actions → Runners → New self-hosted runner** for
the `MinaH153/SuavoAgent` repo. Pick the Windows x64 installer. When the
runner's `config.cmd` asks for labels, add:

```
self-hosted,windows,yubikey
```

(The workflows target exactly `[self-hosted, windows, yubikey]`.)

Install it as a Windows service so it survives reboots:

```powershell
.\svc.sh install  # run from the runner's folder
.\svc.sh start
```

**Runner user account**: must be a local Administrator and must have
interactive access to the Yubikey cert store. If you installed the Yubikey
driver under a different account, repeat the install under the runner account
or move the cert to `Cert:\LocalMachine\My`.

### 4. Configure repo secrets + variables

On github.com → **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNING_CERT_THUMBPRINT` | Thumbprint from step 2 (no spaces, no colons) |
| Variable | `SIGNING_TIMESTAMP_URL` | `http://ts.ssl.com` (optional — defaults to this if unset) |
| Variable | `SIGNING_ENABLED` | `true` |

The existing `SIGNING_KEY_PEM` secret stays as-is — it signs `checksums.sha256`
and the OTA update manifest, not the binaries themselves.

### 5. Smoke test with a throwaway tag

```bash
git tag v3.13.7-signed-smoke
git push origin v3.13.7-signed-smoke
```

Watch the Actions run. The `build` job finishes on ubuntu, `sign_windows`
picks up on your runner (pops a Yubikey PIN prompt), `sign_passthrough` is
skipped, `release` publishes. Download `SuavoAgent.Core.exe` from the release
and right-click → Properties → Digital Signatures. You should see
`Suavo (MKM Technologies LLC)` with a valid timestamp.

If smoke-test passes, delete the tag + release:

```bash
git push origin :refs/tags/v3.13.7-signed-smoke
gh release delete v3.13.7-signed-smoke --yes
```

### 6. Verify on a fresh Windows machine

Test that the SmartScreen warning is gone by downloading the `.cmd` installer
from a pharmacy signup page and running it on a Windows machine that has
never seen the agent. You should see a UAC prompt listing
`Verified publisher: MKM Technologies LLC` instead of `Publisher unknown`.

## Troubleshooting

**`signtool failed: 0x800B0109`** — The timestamp server rejected the request.
Swap `SIGNING_TIMESTAMP_URL` to `http://timestamp.digicert.com` or
`http://timestamp.sectigo.com` and retry.

**`signtool failed: 0x80092009`** — signtool cannot find the cert with the
given thumbprint. Re-check the thumbprint has no spaces/colons and that the
Yubikey is plugged in + unlocked.

**`signtool failed: 0x80090016`** — Keyset does not exist. The Yubikey
minidriver isn't installed under the runner's user account. Reinstall
Yubikey Manager while logged in as the runner account.

**Runner never picks up the job** — Confirm the runner shows Idle/green at
github.com → Settings → Actions → Runners. Confirm its labels include
`self-hosted`, `windows`, and `yubikey` exactly.

**`SmartScreen` still warns** after signing — Expected for the first ~30
days with a brand-new EV cert. Windows Defender builds reputation on signed
EXEs over downloads. It typically goes away once ~300 unique machines have
executed the binary. EV cert removes the warning *sooner* than a standard
OV cert but doesn't eliminate the reputation warmup entirely.

## Rollback

To disable signing without reverting the workflows:

```
SIGNING_ENABLED = false   # or delete the variable
```

Next release: `sign_passthrough` runs, releases ship unsigned exactly like
they do today.

## Future: Azure Trusted Signing migration

Azure Trusted Signing is $9.99/month for unlimited signatures but requires a
**3-year-old verified organization**. MKM Technologies LLC was filed
2026-03-23, so it's ineligible until approximately **March 2029**. At renewal
time (April 2027) the choice is:

1. Renew SSL.com EV cert (~$349) + reuse existing Yubikey — no downtime.
2. Switch to Certum (Poland) with SimplySign cloud HSM (~$200) — same root
   store trust; no Yubikey dependency, workflows would need a cloud-signing
   shim.
3. Wait until 2029 and migrate to Azure Trusted Signing — cheapest long-term.

## Related files

- `.github/workflows/release.yml` — tag-triggered release pipeline
- `.github/workflows/hotfix.yml` — manual-dispatch hotfix pipeline
- `Directory.Build.props` — PE metadata (Company, Copyright, Version)
- `bootstrap.ps1` — client installer; verifies `checksums.sha256` signature
  (separate ECDSA P-256 key, `SIGNING_KEY_PEM`)
- Memory: `session-2026-04-21-installer-and-cert-order.md`,
  `session-2026-04-22-ssl-cert-validation-queued.md`
