# Third-Party Notices

The complete, generated Windows runtime notice bundle is checked in at
[`src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt`](src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt).
It is embedded in `SuavoSetup.exe`, available through the setup footer, and
packaged beside release binaries.

Exact package versions, declared licenses, retained license-file hashes, the
.NET runtime legal bundle, and external-asset provenance are recorded in
[`legal/THIRD-PARTY-PROVENANCE.json`](legal/THIRD-PARTY-PROVENANCE.json).

Regenerate after any dependency or external-asset change:

```sh
python3 scripts/generate-release-legal-bundle.py
python3 scripts/generate-release-legal-bundle.py --check
```

Production release validation additionally uses `--require-release-eligible`.
That gate intentionally fails while any dependency license is unknown or any
external model/native/OCR cohort lacks immutable source, license bundle, SBOM,
and provenance attestation.
