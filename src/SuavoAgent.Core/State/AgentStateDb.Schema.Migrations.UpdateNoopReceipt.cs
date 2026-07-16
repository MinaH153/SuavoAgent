namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyUpdateNoopReceiptMigration()
    {
        ApplyMigrationIfNeeded(46,
            "Device-signed exact OTA same-version no-op receipts",
            """
            CREATE TABLE update_noop_device_receipts (
                command_id TEXT PRIMARY KEY,
                envelope_nonce TEXT NOT NULL UNIQUE,
                data_hash TEXT NOT NULL CHECK(
                    length(data_hash) = 64
                    AND data_hash NOT GLOB '*[^0-9a-f]*'),
                target_version TEXT NOT NULL,
                ota_signing_key_id TEXT NOT NULL CHECK(
                    ota_signing_key_id IN ('ota-update-v1','ota-update-v2')),
                manifest_canonical TEXT NOT NULL,
                manifest_signature TEXT NOT NULL CHECK(
                    length(manifest_signature) = 128
                    AND manifest_signature NOT GLOB '*[^0-9A-Fa-f]*'),
                release_tag TEXT,
                source_sha TEXT CHECK(
                    source_sha IS NULL OR (
                        length(source_sha) = 40
                        AND source_sha NOT GLOB '*[^0-9a-f]*')),
                manifest_name TEXT,
                checksums_sha256 TEXT CHECK(
                    checksums_sha256 IS NULL OR (
                        length(checksums_sha256) = 64
                        AND checksums_sha256 NOT GLOB '*[^0-9a-f]*')),
                checksums_signature_sha256 TEXT CHECK(
                    checksums_signature_sha256 IS NULL OR (
                        length(checksums_signature_sha256) = 64
                        AND checksums_signature_sha256 NOT GLOB '*[^0-9a-f]*')),
                inventory_sha256 TEXT CHECK(
                    inventory_sha256 IS NULL OR (
                        length(inventory_sha256) = 64
                        AND inventory_sha256 NOT GLOB '*[^0-9a-f]*')),
                install_receipt_sha256 TEXT CHECK(
                    install_receipt_sha256 IS NULL OR (
                        length(install_receipt_sha256) = 64
                        AND install_receipt_sha256 NOT GLOB '*[^0-9a-f]*')),
                restart_receipt_sha256 TEXT CHECK(
                    restart_receipt_sha256 IS NULL OR (
                        length(restart_receipt_sha256) = 64
                        AND restart_receipt_sha256 NOT GLOB '*[^0-9a-f]*')),
                device_key_id TEXT NOT NULL CHECK(
                    length(device_key_id) = 64
                    AND device_key_id NOT GLOB '*[^0-9a-f]*'),
                receipt_json TEXT NOT NULL,
                device_signature TEXT NOT NULL CHECK(
                    length(device_signature) = 86
                    AND device_signature NOT GLOB '*[^A-Za-z0-9_-]*'),
                canonical_digest TEXT NOT NULL CHECK(
                    length(canonical_digest) = 64
                    AND canonical_digest NOT GLOB '*[^0-9a-f]*'),
                verified_at_utc TEXT NOT NULL,
                committed_at_utc TEXT NOT NULL,
                FOREIGN KEY(command_id) REFERENCES update_command_receipts(command_id),
                CHECK(
                    (release_tag IS NULL
                        AND source_sha IS NULL
                        AND manifest_name IS NULL
                        AND checksums_sha256 IS NULL
                        AND checksums_signature_sha256 IS NULL
                        AND inventory_sha256 IS NULL
                        AND install_receipt_sha256 IS NULL
                        AND restart_receipt_sha256 IS NULL)
                    OR
                    (release_tag IS NOT NULL
                        AND source_sha IS NOT NULL
                        AND manifest_name IS NOT NULL
                        AND checksums_sha256 IS NOT NULL
                        AND checksums_signature_sha256 IS NOT NULL
                        AND inventory_sha256 IS NOT NULL
                        AND install_receipt_sha256 IS NOT NULL
                        AND restart_receipt_sha256 IS NOT NULL))
            );
            CREATE TRIGGER update_noop_device_receipts_no_update
            BEFORE UPDATE ON update_noop_device_receipts
            BEGIN
                SELECT RAISE(ABORT, 'update_noop_device_receipts_append_only');
            END;
            CREATE TRIGGER update_noop_device_receipts_no_delete
            BEFORE DELETE ON update_noop_device_receipts
            BEGIN
                SELECT RAISE(ABORT, 'update_noop_device_receipts_append_only');
            END;
            """);
    }
}
