using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;
using SuavoAgent.Setup.Security;

namespace SuavoAgent.Setup.Maintenance;

internal static class PioneerRxApprovalBootstrapCoordinator
{
    private const int MaximumSettingsBytes = 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    internal static int Run(string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(
                args[0],
                PioneerRxApprovalBootstrapContract.BootstrapSwitch,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                args[1],
                PioneerRxApprovalBootstrapContract.RequestPathSwitch,
                StringComparison.OrdinalIgnoreCase) ||
            !PioneerRxApprovalBootstrapContract.IsExactRequestPath(args[2]) ||
            !OperatingSystem.IsWindows() || !IsLocalSystem())
            return 2;
        try
        {
            using var transaction = InstallerTransactionLock.Acquire();
            var result = RunProductionAsync(args[2], CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(result);
            return result is "submitting" or "pending" or "security_review_required" or
                "installed" or "revoked"
                ? 0
                : 4;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException or JsonException or HttpRequestException or
            InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine("pioneerrx_approval_bootstrap_failed");
            return 5;
        }
    }

    private static async Task<string> RunProductionAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                               ?? AppContext.BaseDirectory;
        if (!string.Equals(
                Path.GetFileName(Environment.ProcessPath),
                MaintenanceContract.ExecutableName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bootstrap must run from the fixed maintenance host.");
        var appSettingsPath = Path.Combine(installDirectory, "appsettings.json");
        if (!PioneerRxApprovalInstallCoordinator.ValidateProtectedInputFile(
                appSettingsPath,
                installDirectory,
                "appsettings.json"))
            throw new UnauthorizedAccessException("Installed appsettings boundary is invalid.");

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        var certificatePath = Path.Combine(
            dataDirectory,
            PioneerRxSqlCertificatePinContract.InstalledFileName);
        var consentPath = Path.Combine(dataDirectory, "consent-receipt.json");
        var config = ReadConfig(
            appSettingsPath,
            Path.Combine(dataDirectory, "credentials.dat"));
        var authorityDirectory = PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory();
        if (!PioneerRxApprovalMetadataAcl.ValidateDirectory(authorityDirectory) ||
            !PioneerRxApprovalMetadataAcl.ValidateFile(requestPath, interactiveRead: false))
            throw new UnauthorizedAccessException("PioneerRx bootstrap request ACL is invalid.");
        var request = ReadStrict<PioneerRxApprovalBootstrapRequest>(requestPath);
        ValidateBootstrapRequest(request, consentPath, now);

        Directory.CreateDirectory(authorityDirectory);
        PioneerRxApprovalMetadataAcl.ProtectDirectory(authorityDirectory);
        var statePath = PioneerRxApprovalBootstrapContract.DefaultStatePath();
        var statusPath = PioneerRxApprovalBootstrapContract.DefaultStatusPath();
        var maintenanceKeys = MaintenanceAttestationKeyProvider.CreateProduction();
        using var cloud = new PioneerRxApprovalCloudClient(config, maintenanceKeys);

        PioneerRxApprovalBootstrapState? state = null;
        if (File.Exists(statePath))
        {
            if (!PioneerRxApprovalMetadataAcl.ValidateFile(statePath, interactiveRead: false))
                throw new UnauthorizedAccessException("PioneerRx bootstrap state ACL is invalid.");
            state = ReadStrict<PioneerRxApprovalBootstrapState>(statePath);
            if (state.SchemaVersion != PioneerRxApprovalBootstrapContract.SchemaVersion ||
                !CanonicalUuid(state.ProposalId) ||
                !LowerHex64(state.ConsentReceiptSha256) ||
                !FixedHexEquals(
                    state.ConsentReceiptSha256,
                    request.ConsentReceiptSha256) ||
                state.Status is not ("submitting" or "pending" or "security_review_required") ||
                !TryUtc(state.NextPollAtUtc, out _) || !TryUtc(state.UpdatedAtUtc, out _))
                throw new InvalidDataException("PioneerRx bootstrap state is invalid.");
        }

        if (state is null)
        {
            if (!PioneerRxApprovalInstallCoordinator.ValidateProtectedInputFile(
                    certificatePath,
                    dataDirectory,
                    PioneerRxSqlCertificatePinContract.InstalledFileName))
                throw new InvalidDataException("pioneerrx_sql_certificate_boundary_invalid");
            if (!PioneerRxSqlCertificatePinContract.TryVerifyFile(
                    certificatePath,
                    ReadSqlCertificateDigest(appSettingsPath),
                    now,
                    out var certCode))
                throw new InvalidDataException(certCode);
            var discovered = PioneerRxDiscovery.Discover()
                             ?? throw new InvalidDataException("PioneerRx executable is unavailable.");
            if (!PioneerRxExecutableEvidenceReader.TryCapture(
                    discovered.PioneerExe,
                    out var evidence,
                    out var evidenceCode) || evidence is null)
                throw new InvalidDataException(evidenceCode);
            var bootstrap = await cloud.DiscoverCatalogAsync(
                evidence,
                now,
                cancellationToken).ConfigureAwait(false);
            var consentJson = ReadBoundedUtf8(consentPath, 64 * 1024);
            var proposal = PioneerRxApprovalProposalBuilder.Build(
                config,
                evidence,
                bootstrap,
                ReadSqlCertificateDigest(appSettingsPath),
                request.ApprovedBySid,
                consentJson,
                new[] { "read" },
                maintenanceKeys);

            // Persist before POST. If transport outcome is unknown, restart
            // retries this exact maintenance-signed proposal through the
            // backend's exact-replay idempotency contract.
            state = new PioneerRxApprovalBootstrapState(
                PioneerRxApprovalBootstrapContract.SchemaVersion,
                proposal.ReceiptId,
                proposal,
                request.ConsentReceiptSha256,
                "submitting",
                Utc(now),
                Utc(now));
            WriteProtected(statePath, state, interactiveRead: false);
            var submitted = await cloud.SubmitAsync(proposal, cancellationToken)
                .ConfigureAwait(false);
            var submission = TransitionSubmission(state, submitted, now);
            if (submission.NextState is null)
            {
                WriteStatus(statusPath, "rejected", proposal, now);
                DeleteRegular(requestPath);
                DeleteRegular(statePath);
                return "rejected";
            }
            state = submission.NextState;
            WriteProtected(statePath, state, interactiveRead: false);
            WriteStatus(statusPath, state.Status, proposal, now);
            return state.Status;
        }

        if (!TryUtc(state.NextPollAtUtc, out var nextPoll) || now < nextPoll)
            return state.Status;
        if (state.Status == "submitting")
        {
            // The durable state is written before POST. A crash may therefore
            // occur before any bytes leave the machine, and an HTTP timeout may
            // leave the transport outcome unknown. The backend RPC is exact-
            // replay idempotent on this maintenance-signed receipt, so retry the
            // identical proposal instead of polling forever for a row that may
            // never have been created.
            var submitted = await cloud.SubmitAsync(state.Proposal, cancellationToken)
                .ConfigureAwait(false);
            var submission = TransitionSubmission(state, submitted, now);
            if (submission.NextState is null)
            {
                WriteStatus(statusPath, "rejected", state.Proposal, now);
                DeleteRegular(requestPath);
                DeleteRegular(statePath);
                return "rejected";
            }
            state = submission.NextState;
            WriteProtected(statePath, state, interactiveRead: false);
            WriteStatus(statusPath, state.Status, state.Proposal, now);
            return state.Status;
        }
        var poll = await cloud.PollAsync(state.Proposal, now, cancellationToken)
            .ConfigureAwait(false);
        if (poll.Status is PioneerRxProposalStatus.Pending or
            PioneerRxProposalStatus.SecurityReviewRequired or PioneerRxProposalStatus.Unknown)
        {
            var label = poll.Status == PioneerRxProposalStatus.SecurityReviewRequired
                ? "security_review_required"
                : "pending";
            state = state with
            {
                Status = label,
                NextPollAtUtc = Utc(now + PollInterval),
                UpdatedAtUtc = Utc(now),
            };
            WriteProtected(statePath, state, interactiveRead: false);
            WriteStatus(statusPath, label, state.Proposal, now);
            return label;
        }
        if (poll.Status == PioneerRxProposalStatus.Rejected)
        {
            WriteStatus(statusPath, "rejected", state.Proposal, now);
            DeleteRegular(requestPath);
            DeleteRegular(statePath);
            return "rejected";
        }
        if (poll.Receipt is null || poll.Authority is null || poll.VendorCatalog is null)
            throw new InvalidDataException("Approved PioneerRx response is incomplete.");

        var commandId = CanonicalUuid(state.ProposalId)
            ? state.ProposalId
            : state.Proposal.ReceiptId;
        var payloadDigest = PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
            commandId,
            poll.Receipt,
            poll.Authority,
            poll.VendorCatalog);
        var installRequest = new PioneerRxApprovalInstallRequest(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            commandId,
            payloadDigest,
            poll.Receipt,
            poll.Authority,
            poll.VendorCatalog,
            Utc(now));
        var installRequestPath = Path.Combine(
            authorityDirectory,
            $".bootstrap-install-{commandId}.json");
        WriteProtected(installRequestPath, installRequest, interactiveRead: false);
        try
        {
            var installed = PioneerRxApprovalInstallCoordinator.Install(
                installRequestPath,
                appSettingsPath,
                certificatePath,
                authorityDirectory,
                now,
                RemoteCommandTrust.CreateProductionKeyRegistry(),
                maintenanceKeys,
                protectDirectory: PioneerRxApprovalMetadataAcl.ProtectDirectory,
                protectMetadata: PioneerRxApprovalMetadataAcl.ProtectMetadataFile,
                validateMetadata: path => PioneerRxApprovalMetadataAcl.ValidateFile(path, true),
                protectHighWater: PioneerRxApprovalMetadataAcl.ProtectHighWaterFile,
                validateHighWater: path => PioneerRxApprovalMetadataAcl.ValidateFile(path, false),
                validateAppSettings: path =>
                    PioneerRxApprovalInstallCoordinator.ValidateProtectedInputFile(
                        path,
                        installDirectory,
                        "appsettings.json"),
                validateCertificate: path =>
                    PioneerRxApprovalInstallCoordinator.ValidateProtectedInputFile(
                        path,
                        dataDirectory,
                        PioneerRxSqlCertificatePinContract.InstalledFileName));
            if (!installed.Succeeded) throw new InvalidDataException(installed.Code);
            WriteStatus(statusPath, installed.Code, poll.Receipt, now);
            DeleteRegular(requestPath);
            DeleteRegular(statePath);
            return installed.Code;
        }
        finally
        {
            DeleteRegular(installRequestPath);
        }
    }

    internal sealed record SubmissionTransition(
        string Outcome,
        PioneerRxApprovalBootstrapState? NextState);

    internal static SubmissionTransition TransitionSubmission(
        PioneerRxApprovalBootstrapState current,
        PioneerRxProposalSubmission submitted,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(submitted);
        if (submitted.Status == PioneerRxProposalStatus.Rejected)
            return new("rejected", null);
        var status = submitted.Status switch
        {
            PioneerRxProposalStatus.Pending => "pending",
            PioneerRxProposalStatus.SecurityReviewRequired => "security_review_required",
            PioneerRxProposalStatus.Unknown => "submitting",
            _ => throw new InvalidDataException("Proposal submission status is invalid."),
        };
        var proposalId = submitted.ProposalId ?? current.ProposalId;
        if (!CanonicalUuid(proposalId))
            throw new InvalidDataException("Proposal submission identity is invalid.");
        var next = current with
        {
            ProposalId = proposalId,
            Status = status,
            NextPollAtUtc = Utc(now + PollInterval),
            UpdatedAtUtc = Utc(now),
        };
        return new(status, next);
    }

    private static SetupConfig ReadConfig(string settingsPath, string credentialPath)
    {
        var store = new DpapiCredentialStore(credentialPath);
        var apiKey = store.Get(CredentialKeys.AuthKey);
        var agentId = store.Get(CredentialKeys.AgentId);
        var pharmacyId = store.Get(CredentialKeys.PharmacyId);
        var bytes = BoundedFile.ReadBytes(settingsPath, MaximumSettingsBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!Unique(document.RootElement))
                throw new InvalidDataException("Installed appsettings has duplicate properties.");
            var agent = document.RootElement.GetProperty("Agent");
            var fingerprint = RequireString(agent, "MachineFingerprint");
            var maintenanceKey = RequireString(agent, "MaintenanceAttestationKeyId");
            var cloudUrl = RequireString(agent, "CloudUrl");
            var version = RequireString(agent, "Version");
            if (string.IsNullOrWhiteSpace(apiKey) || !CanonicalUuid(agentId) ||
                !CanonicalUuid(pharmacyId) || !CanonicalUuid(fingerprint) ||
                maintenanceKey.Length != 64)
                throw new InvalidDataException("Installed PioneerRx cloud identity is invalid.");
            return new SetupConfig(
                pharmacyId!,
                apiKey!,
                cloudUrl,
                "v" + version,
                false,
                agentId!,
                MaintenanceKeyId: maintenanceKey,
                DeviceFingerprint: fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ReadSqlCertificateDigest(string settingsPath)
    {
        var bytes = BoundedFile.ReadBytes(settingsPath, MaximumSettingsBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!Unique(document.RootElement))
                throw new InvalidDataException("Installed appsettings has duplicate properties.");
            var value = RequireString(
                document.RootElement.GetProperty("Agent"),
                "SqlServerCertificateSha256");
            if (value.Length != 64 || value.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new InvalidDataException("Installed SQL certificate digest is invalid.");
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void ValidateBootstrapRequest(
        PioneerRxApprovalBootstrapRequest request,
        string consentPath,
        DateTimeOffset now)
    {
        if (request.SchemaVersion != PioneerRxApprovalBootstrapContract.SchemaVersion ||
            !IsSid(request.ApprovedBySid) ||
            request.ConsentReceiptSha256.Length != 64 ||
            !TryUtc(request.RequestedAtUtc, out var requestedAt) ||
            requestedAt > now.AddMinutes(5) || now - requestedAt > TimeSpan.FromDays(30))
            throw new InvalidDataException("PioneerRx bootstrap request is invalid.");
        var consentBytes = BoundedFile.ReadBytes(consentPath, 64 * 1024);
        try
        {
            var digest = SHA256.HashData(consentBytes);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        digest,
                        Convert.FromHexString(request.ConsentReceiptSha256)))
                    throw new InvalidDataException("PioneerRx bootstrap consent binding changed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(consentBytes);
        }
    }

    private static void WriteStatus(
        string path,
        string status,
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset now) => WriteProtected(
        path,
        new PioneerRxApprovalBootstrapStatus(
            PioneerRxApprovalBootstrapContract.SchemaVersion,
            status,
            receipt.ReceiptId,
            receipt.ApprovalCounter,
            Utc(now)),
        interactiveRead: true);

    private static void WriteProtected<T>(string path, T value, bool interactiveRead)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            PioneerRxApprovalMaintenanceContract.JsonOptions);
        try
        {
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidDataException("Protected state parent is missing.");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (interactiveRead)
                    PioneerRxApprovalMetadataAcl.ProtectMetadataFile(temporary);
                else
                    PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(temporary);
                File.Move(temporary, path, overwrite: true);
                if (interactiveRead)
                    PioneerRxApprovalMetadataAcl.ProtectMetadataFile(path);
                else
                    PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static T ReadStrict<T>(string path)
    {
        var bytes = BoundedFile.ReadBytes(
            path,
            PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            if (!Unique(document.RootElement))
                throw new InvalidDataException("Protected state has duplicate properties.");
            return JsonSerializer.Deserialize<T>(
                       bytes,
                       PioneerRxApprovalMaintenanceContract.JsonOptions)
                   ?? throw new InvalidDataException("Protected state is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ReadBoundedUtf8(string path, int maximumBytes)
    {
        var bytes = BoundedFile.ReadBytes(path, maximumBytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (!Unique(child)) return false;
        }
        return true;
    }

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidDataException($"{name} is empty.")
            : throw new InvalidDataException($"{name} is missing.");

    internal static void DeleteRegular(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Refusing to delete an untrusted state entry.");
        File.Delete(path);
    }

    internal static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    internal static bool TryUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    internal static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    internal static bool IsSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        var segments = value.Split('-');
        return segments.Length >= 4 && segments[0] == "S" && segments[1] == "1" &&
               segments.Skip(2).All(segment =>
                   segment.Length > 0 && segment.All(char.IsAsciiDigit));
    }

    internal static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool FixedHexEquals(string? left, string? right)
    {
        if (!LowerHex64(left) || !LowerHex64(right)) return false;
        var leftBytes = Convert.FromHexString(left!);
        var rightBytes = Convert.FromHexString(right!);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return string.Equals(identity.User?.Value, "S-1-5-18", StringComparison.Ordinal);
    }
}
