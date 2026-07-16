using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.State;

internal sealed record PersistedRelease1InstallUpload(
    string InstallReceiptSha256,
    string RequestJson,
    string RequestSha256,
    SignedRelease1InstallReceipt Request,
    string InstalledReleaseTag,
    string InstalledSourceSha);

public sealed partial class AgentStateDb
{
    internal PersistedRelease1InstallUpload GetOrCreateRelease1InstallUpload(
        SignedRelease1InstallReceipt request,
        string installReceiptSha256)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLowerSha256(installReceiptSha256, "install receipt digest");
        var requestBytes = Release1ConvergenceContract.CanonicalBytes(request);
        ValidateBoundedRequest(requestBytes);
        var requestJson = Encoding.UTF8.GetString(requestBytes);
        var requestSha256 = Sha256(requestBytes);
        if (!Release1FixedHexEquals(
                installReceiptSha256,
                Release1ConvergenceContract.CanonicalSha256(
                    request.InstallReceipt)))
            throw new InvalidOperationException(
                "Release 1 install upload digest is invalid.");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRelease1InstallUpload(
                transaction,
                installReceiptSha256);
            if (existing is not null)
            {
                transaction.Commit();
                if (!Release1FixedTextEquals(existing.RequestJson, requestJson) ||
                    !Release1FixedHexEquals(
                        existing.RequestSha256,
                        requestSha256))
                    throw new InvalidOperationException(
                        "Release 1 install upload replay conflict.");
                return existing;
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_install_receipt_uploads (
                    install_receipt_sha256, request_json, request_sha256,
                    installed_release_tag, installed_source_sha, created_at_utc
                ) VALUES (
                    @installReceiptSha256, @requestJson, @requestSha256,
                    @installedReleaseTag, @installedSourceSha, @createdAtUtc
                )
                """);
            insert.Parameters.AddWithValue(
                "@installReceiptSha256",
                installReceiptSha256);
            insert.Parameters.AddWithValue("@requestJson", requestJson);
            insert.Parameters.AddWithValue("@requestSha256", requestSha256);
            insert.Parameters.AddWithValue(
                "@installedReleaseTag",
                request.InstallReceipt.InstalledReleaseTag);
            insert.Parameters.AddWithValue(
                "@installedSourceSha",
                request.InstallReceipt.InstalledSourceSha);
            insert.Parameters.AddWithValue(
                "@createdAtUtc",
                Release1ConvergenceContract.ExactUtc(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(
                installReceiptSha256,
                requestJson,
                requestSha256,
                request,
                request.InstallReceipt.InstalledReleaseTag,
                request.InstallReceipt.InstalledSourceSha);
        }
    }

    internal bool HasRelease1InstallDelivery(string installReceiptSha256)
    {
        ValidateLowerSha256(installReceiptSha256, "install receipt digest");
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM release1_install_receipt_deliveries
                 WHERE install_receipt_sha256 = @installReceiptSha256
                 LIMIT 1
                """;
            command.Parameters.AddWithValue(
                "@installReceiptSha256",
                installReceiptSha256);
            return command.ExecuteScalar() is not null;
        }
    }

    internal void RecordRelease1InstallDelivery(
        PersistedRelease1InstallUpload upload,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(upload);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            using var select = CreateCommand(transaction, """
                SELECT request_sha256
                  FROM release1_install_receipt_deliveries
                 WHERE install_receipt_sha256 = @installReceiptSha256
                 LIMIT 1
                """);
            select.Parameters.AddWithValue(
                "@installReceiptSha256",
                upload.InstallReceiptSha256);
            var existing = select.ExecuteScalar() as string;
            if (existing is not null)
            {
                transaction.Commit();
                if (!Release1FixedHexEquals(existing, upload.RequestSha256))
                    throw new InvalidOperationException(
                        "Release 1 install delivery replay conflict.");
                return;
            }

            using var evidence = CreateCommand(transaction, """
                SELECT 1 FROM release1_install_receipt_uploads
                 WHERE install_receipt_sha256 = @installReceiptSha256
                   AND request_sha256 = @requestSha256
                 LIMIT 1
                """);
            evidence.Parameters.AddWithValue(
                "@installReceiptSha256",
                upload.InstallReceiptSha256);
            evidence.Parameters.AddWithValue(
                "@requestSha256",
                upload.RequestSha256);
            if (evidence.ExecuteScalar() is null)
                throw new InvalidOperationException(
                    "Release 1 install upload evidence is missing.");

            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_install_receipt_deliveries (
                    install_receipt_sha256, request_sha256, accepted_at_utc
                ) VALUES (
                    @installReceiptSha256, @requestSha256, @acceptedAtUtc
                )
                """);
            insert.Parameters.AddWithValue(
                "@installReceiptSha256",
                upload.InstallReceiptSha256);
            insert.Parameters.AddWithValue(
                "@requestSha256",
                upload.RequestSha256);
            insert.Parameters.AddWithValue(
                "@acceptedAtUtc",
                Release1ConvergenceContract.ExactUtc(acceptedAt));
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private PersistedRelease1InstallUpload? ReadRelease1InstallUpload(
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string installReceiptSha256)
    {
        using var select = CreateCommand(transaction, """
            SELECT request_json, request_sha256,
                   installed_release_tag, installed_source_sha
              FROM release1_install_receipt_uploads
             WHERE install_receipt_sha256 = @installReceiptSha256
             LIMIT 1
            """);
        select.Parameters.AddWithValue(
            "@installReceiptSha256",
            installReceiptSha256);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;
        var requestJson = reader.GetString(0);
        var requestSha256 = reader.GetString(1);
        var installedReleaseTag = reader.GetString(2);
        var installedSourceSha = reader.GetString(3);
        var request = DeserializeExactCanonical<SignedRelease1InstallReceipt>(
            requestJson);
        if (!Release1FixedHexEquals(
                Sha256(Encoding.UTF8.GetBytes(requestJson)),
                requestSha256) ||
            !Release1FixedHexEquals(
                Release1ConvergenceContract.CanonicalSha256(
                    request.InstallReceipt),
                installReceiptSha256) ||
            !string.Equals(
                request.InstallReceipt.InstalledReleaseTag,
                installedReleaseTag,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.InstallReceipt.InstalledSourceSha,
                installedSourceSha,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Release 1 install upload storage binding is invalid.");
        return new(
            installReceiptSha256,
            requestJson,
            requestSha256,
            request,
            installedReleaseTag,
            installedSourceSha);
    }
}
