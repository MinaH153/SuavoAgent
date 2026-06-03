# Runbook: Mesh dispatch token rotation

**Cadence:**
- HMAC signing secret (`MESH_DISPATCH_SIGNING_SECRET`) — **quarterly**
- GitHub App private key — **annual** (or immediate on suspected compromise)
- Supabase service-role JWT — **per Supabase project lifecycle** (rotates with
  database password resets; coordinate with the cloud rotation runbook)

**Codex lineage:** Comp 3 design § "Permissions" + § "Rotation runbook"
(round-3 HIGH "policy-only rotation is theater" guidance).

**Owner:** weekly on-call engineer. Schedule via the team calendar; rotate
proactively, never reactively.

---

## Why rotation matters

Each secret protects a different boundary:

| Secret | What it gates |
|---|---|
| `MESH_DISPATCH_SIGNING_SECRET` | The HMAC verification in `mesh-fingerprint-issue-manager.yml` — without it, a compromised PAT could forge `repository_dispatch` events to trigger arbitrary GH issue actions. |
| GitHub App private key | The App's installation token (auto-rotated every 1h by GH, but the underlying private key signs the token requests). Compromise grants the holder `Contents: write` + `Issues: write` on `MinaH153/SuavoAgent`. |
| `SUPABASE_SERVICE_ROLE_KEY` | Service-role JWT that bypasses RLS. Compromise grants unrestricted DB write. |

A stolen `MESH_DISPATCH_SIGNING_SECRET` + a stolen PAT together could
script arbitrary GH issue create/comment/close on the SuavoAgent repo.
A stolen service-role JWT could mutate the `fingerprint_issue_*` tables
directly, bypassing the SECURITY DEFINER RPCs.

---

## Quarterly rotation: `MESH_DISPATCH_SIGNING_SECRET`

The HMAC secret is shared between (a) the cloud `mesh-sentry-ingest`
Edge Function (the signer) and (b) the GitHub Actions workflow (the
verifier). Both ends must update atomically — there is no per-message
key rotation, just shared-secret cutover.

### Pre-rotation prep

1. Verify no `mesh-fingerprint` dispatches are in flight:
   ```sql
   SELECT count(*) FROM public.fingerprint_issue_jobs
    WHERE dispatch_lease_until > NOW();
   ```
   If non-zero, wait for the dispatcher's longest TTL (30 min for
   `create`) before proceeding. Otherwise the in-flight workflow's HMAC
   will mismatch on completion → forced into the `MeshStaleCompletion`
   path.

2. Generate the new secret (32 bytes, base64):
   ```bash
   new_secret=$(openssl rand -base64 32)
   echo "New MESH_DISPATCH_SIGNING_SECRET: ${new_secret}"
   ```

3. Stage it in BOTH ends before cutover:
   - **Vault** (cloud signer): write to a new versioned key
     `mesh-dispatch-signing-secret-v$(date +%Y%m%d)`. Keep the previous
     version readable for the cutover window.
   - **GitHub Actions**: add as a NEW repo secret
     `MESH_DISPATCH_SIGNING_SECRET_NEW` (DO NOT overwrite the live one
     yet).

### Cutover

4. Update the cloud Edge Function to sign with the NEW key for every
   future dispatch. Old in-flight dispatches will still verify against
   the OLD key, but new dispatches use the new key.

   Brief window where workflow has both secrets configured — use a
   two-step verification in the workflow:

   ```bash
   # In the workflow's HMAC verify step:
   expected_new=$(printf '%s' "$payload" | openssl dgst -sha256 -hmac "$DISPATCH_SECRET_NEW" -binary | base64 -w0)
   expected_old=$(printf '%s' "$payload" | openssl dgst -sha256 -hmac "$DISPATCH_SECRET_OLD" -binary | base64 -w0)
   if [ "$DISPATCH_HMAC" != "$expected_new" ] && [ "$DISPATCH_HMAC" != "$expected_old" ]; then
     # reject
   fi
   ```

   (Edit `mesh-fingerprint-issue-manager.yml` with this dual-check
   pattern only during the cutover window. Delete the OLD-key fallback
   after step 6.)

### Post-cutover

5. Wait 35 min (longest dispatch_lease_until = 30 min, + 5 min slack)
   to ensure all old dispatches have been processed.

6. Rename `MESH_DISPATCH_SIGNING_SECRET_NEW` → `MESH_DISPATCH_SIGNING_SECRET`
   (overwrite). Delete the OLD-key fallback from the workflow yaml.

7. Delete the OLD key from Vault. The cloud Edge Function should now
   sign exclusively with the new key.

8. Verify rotation with a synthetic dispatch:
   ```bash
   # From the cloud Edge Function's deploy environment:
   supabase functions invoke mesh-sentry-ingest --body \
     '{"action":"triggered","data":{"event":{...synthetic test event...}}}'
   ```
   Watch `mesh-fingerprint-issue-manager.yml` run + complete cleanly.

### Audit log

After every rotation, append to `docs/audit/mesh-secret-rotations.md` (or
your equivalent audit doc):

```
2026-Q3 rotation completed YYYY-MM-DD by @<oncall>
  MESH_DISPATCH_SIGNING_SECRET: rotated from v20260513 → v20260813
  Synthetic dispatch verified: workflow run #<N>
```

---

## Annual rotation: GitHub App private key

The GH App that owns the `Contents: write` + `Issues: write` permissions
on the SuavoAgent repo. The App's installation token is auto-rotated
every 1h, but the underlying private key (PEM, used to SIGN token
requests) rotates annually OR immediately on suspected compromise.

1. In GitHub → Settings → Developer settings → GitHub Apps → `<app name>`:
   "Generate a private key" → download the new `.pem`.

2. Keep both keys active for a 24h window — GH supports multiple
   active keys per App. The Edge Function picks whichever Vault has.

3. Upload the new PEM to Vault under a versioned key
   `gh-app-private-key-v$(date +%Y)`.

4. Update the Edge Function to read from the new Vault key.

5. After 24h, delete the old PEM via the App's settings.

6. Audit log entry.

### Emergency rotation (suspected compromise)

Skip the 24h grace window. Generate new key → Vault → Edge Function deploy
→ delete old key from GitHub App → cycle ALL in-flight installation
tokens via the App's API:

```bash
gh api -X POST "/app/installations/${INSTALLATION_ID}/access_tokens" \
  --header "Authorization: Bearer $(generate_jwt_with_old_key)" \
  --header "Accept: application/vnd.github+json"
# Then revoke that token immediately to force re-issue with new key.
```

---

## Service-role JWT rotation

Coordinate with the Supabase password-reset runbook (separate doc). The
service-role JWT is regenerated whenever the database password is
rotated. Two-step cutover similar to the HMAC secret:

1. Generate new JWT in Supabase dashboard.
2. Add as `SUPABASE_SERVICE_ROLE_KEY_NEW` in GH Actions secrets.
3. Deploy a dual-check workflow that tries `_NEW` first, falls back to
   `_OLD` on `401 Unauthorized`.
4. Wait 30 min.
5. Promote `_NEW` → `SUPABASE_SERVICE_ROLE_KEY`. Delete `_OLD`.

---

## Verification after every rotation

Post-rotation smoke MUST include:

1. **Synthetic dispatch from the cloud**: send a contrived Sentry-shape
   event through `mesh-sentry-ingest` → confirm a fresh
   `mesh-fingerprint-issue-manager.yml` run + completes cleanly.

2. **HMAC mismatch path**: forge a dispatch with the OLD key (or random
   bytes) → confirm the workflow rejects with
   `::error::HMAC mismatch` and records failure via
   `record_fingerprint_issue_job_failure`.

3. **Stale `signed_at` path**: forge a dispatch with `signed_at` 10 min
   in the past → confirm rejection + failure record.

If any verification step fails, the rotation is INCOMPLETE — roll back
to the prior key configuration in BOTH ends and investigate.

---

## See also

- `docs/runbooks/mesh-stale-completion.md` — re-claim race recovery
- `docs/runbooks/mesh-dead-job.md` — `failure_count >= 5` recovery
- Comp 3 design § "Permissions" — full permission model
