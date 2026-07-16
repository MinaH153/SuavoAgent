using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
    [Fact]
    public async Task SyncRxDeviceBoundAsync_RequiresExactAcceptanceBody()
    {
        var signed = SourceReceipt();
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" },
            new FixedHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        success = true,
                        data = new
                        {
                            stored = true,
                            batchDigest = signed.Receipt.BatchDigest,
                            sourceKeyId = signed.KeyId,
                            sourceCounter = signed.Receipt.Counter,
                            sourceBindingId = signed.Receipt.SourceBindingId,
                        },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            }));

        Assert.True(await client.SyncRxDeviceBoundAsync(
            new { ok = true }, signed, CancellationToken.None));
    }

    [Fact]
    public async Task SyncRxDeviceBoundAsync_RejectsEmptyOrMismatchedAcceptance()
    {
        var signed = SourceReceipt();
        using var empty = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" },
            new FixedHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            }));
        Assert.False(await empty.SyncRxDeviceBoundAsync(
            new { ok = true }, signed, CancellationToken.None));

        using var mismatch = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" },
            new FixedHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        success = true,
                        data = new
                        {
                            stored = true,
                            batchDigest = new string('f', 64),
                            sourceKeyId = signed.KeyId,
                            sourceCounter = signed.Receipt.Counter,
                            sourceBindingId = signed.Receipt.SourceBindingId,
                        },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            }));
        Assert.False(await mismatch.SyncRxDeviceBoundAsync(
            new { ok = true }, signed, CancellationToken.None));
    }

    [Fact]
    public async Task PostSignedAsync_IncludesSanitizedCloudReasonOnAuthFailure()
    {
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            new FixedHandler(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"success":false,"error":"Agent not found"}""",
                        Encoding.UTF8,
                        "application/json"),
                }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostSignedAsync("/api/agent/heartbeat", new { ok = true }, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("reason=Agent not found", ex.Message);
    }

    [Fact]
    public async Task PostSignedAsync_RejectsPhiShapedPayloadToNonPhiEndpointBeforeSending()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/heartbeat",
                new { status = "ok", detail = "Patient: Jane Rivera DOB 04/12/1980 phone 555-123-4567" },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_RejectsLegacyRxDeliveryQueueOnSyncByDefault()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/sync",
                new
                {
                    snapshotType = "rx_delivery_queue",
                    data = new
                    {
                        rxDeliveryQueue = new[]
                        {
                            new
                            {
                                rxNumber = new string('a', 64),
                                patientFirstName = "Jane",
                                patientPhone = "555-123-4567",
                                deliveryAddress1 = "123 Main Street"
                            }
                        }
                    }
                },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_AllowsHashOnlyCandidateSyncByDefault()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        await client.PostSignedAsync(
            "/api/agent/sync",
            new
            {
                snapshotType = "rx_delivery_queue",
                data = new
                {
                    rxOrderCandidates = new[]
                    {
                        new
                        {
                            rxHash = new string('a', 64),
                            patientDelivery = new
                            {
                                phoneHash = new string('b', 64),
                                zip5 = "92101"
                            },
                            provenance = new { evidenceId = "rxh-aaaaaaaaaaaaaaaa-1770000000" }
                        }
                    }
                }
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_LegacyFlagCannotReenableRxDeliveryQueueEgress()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                EnableLegacyPhiDeliveryQueueSync = true,
            },
            handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostSignedAsync(
                "/api/agent/sync",
                new
                {
                    snapshotType = "rx_delivery_queue",
                    data = new
                    {
                        rxDeliveryQueue = new[]
                        {
                            new
                            {
                                rxNumber = new string('a', 64),
                                patientFirstName = "Jane",
                                patientPhone = "555-123-4567",
                                deliveryAddress1 = "123 Main Street"
                            }
                        }
                    }
                },
                CancellationToken.None));

        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_GenericPatientDetailsCallCannotBypassTypedPhiContract()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostSignedAsync(
                "/api/agent/patient-details",
                new
                {
                    rxNumberHash = new string('a', 64),
                    details = new
                    {
                        firstName = "Jane",
                        lastInitial = "R",
                        phone = "555-123-4567",
                        address1 = "123 Main Street",
                        city = "San Diego",
                        state = "CA",
                        zip = "92101"
                    },
                    commandId = "cmd_1"
                },
                CancellationToken.None));

        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SendApprovedPatientDetailsAsync_FailsClosedByDefault_DoesNotShipPhi()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                // EnableAuditedPatientDetailsEgress defaults to false
            },
            handler);

        var receipt = await client.SendApprovedPatientDetailsAsync(
            PatientCommand(),
            new PatientDetailsPayload(
                "Jane", "R", "555-123-4567", "123 Main Street", null, "San Diego", "CA", "92101"),
            CancellationToken.None);

        // Precedence-1: no patient PHI may leave the box until the audited
        // /api/agent/patient-details route + phi_egress_audit exist (Stage A1).
        Assert.Null(receipt);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SendApprovedPatientDetailsAsync_RequiresSignedBoundReceiptBeforeSuccess()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var command = PatientCommand();
        var responseBody = CallbackReceiptJson(command, idempotent: false);
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add(
            "X-Response-Signature",
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(responseBody),
                HashAlgorithmName.SHA256)));
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                EnableAuditedPatientDetailsEgress = true,
            },
            handler,
            publicKey);

        var receipt = await client.SendApprovedPatientDetailsAsync(
            command,
            new PatientDetailsPayload(
                "Jane", "R", "555-123-4567", "123 Main Street", null, "San Diego", "CA", "92101"),
            CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(command.CommandId, receipt.CommandId);
        Assert.Equal(1, handler.SendCount);
        Assert.NotNull(handler.LastBody);
        Assert.DoesNotContain("RX123456", handler.LastBody, StringComparison.Ordinal);
        using var callback = JsonDocument.Parse(handler.LastBody!);
        var root = callback.RootElement;
        Assert.Equal(command.RxHash, root.GetProperty("rxHash").GetString());
        Assert.Equal(command.EvidenceId, root.GetProperty("evidenceId").GetString());
        Assert.Equal(command.CommandId, root.GetProperty("commandId").GetString());
        Assert.Equal("Jane", root.GetProperty("details").GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task SendApprovedPatientDetailsAsync_UnsignedSuccessReceiptFailsClosed()
    {
        var command = PatientCommand();
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallbackReceiptJson(command, idempotent: true), Encoding.UTF8, "application/json"),
        });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                EnableAuditedPatientDetailsEgress = true,
            },
            handler);

        var receipt = await client.SendApprovedPatientDetailsAsync(
            command,
            new PatientDetailsPayload("Jane", "R", null, "123 Main", null, "San Diego", "CA", "92101"),
            CancellationToken.None);

        Assert.Null(receipt);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public void PatientDetailsReceipt_RejectsMismatchedOrExpiredBindings()
    {
        var command = PatientCommand();
        var now = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        var mismatched = JsonSerializer.Deserialize<JsonElement>(CallbackReceiptJson(
            command with { CandidateId = "00000000-0000-4000-8000-000000000099" },
            idempotent: false,
            expiresAt: now.AddMinutes(30)));
        Assert.False(SuavoCloudClient.TryParsePatientDetailsReceipt(
            mismatched,
            command,
            out _,
            now));

        var expired = JsonSerializer.Deserialize<JsonElement>(CallbackReceiptJson(
            command,
            idempotent: true,
            expiresAt: now.AddMinutes(-1)));
        Assert.False(SuavoCloudClient.TryParsePatientDetailsReceipt(
            expired,
            command,
            out _,
            now));
    }

    [Fact]
    public async Task SendDeliveryWritebackAsync_UsesPatchAndRequiresExactP1363SignedReceipt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var command = DeliveryCommand();
        var responseBody = DeliveryReceiptJson(
            command,
            DeliveryWritebackResultCode.Success,
            idempotent: false);
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add(
            "X-Response-Signature",
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(responseBody),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler,
            publicKey);

        var receipt = await client.SendDeliveryWritebackAsync(
            command,
            DeliveryWritebackResultCode.Success,
            CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(command.CommandId, receipt!.CommandId);
        Assert.True(receipt.Proof.Verify(
            receipt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RemoteCommandTrust.CommandV1KeyId] = publicKey,
            }));
        Assert.Equal(HttpMethod.Patch, handler.LastMethod);
        Assert.Equal("/api/agent/delivery-writeback", handler.LastPath);
        Assert.NotNull(handler.LastBody);
        Assert.Equal("agent-secret", handler.LastApiKey);
        Assert.Equal("2", handler.LastAuthVersion);
        Assert.NotNull(handler.LastTimestamp);
        Assert.NotNull(handler.LastNonce);
        Assert.NotNull(handler.LastContentSha256);
        Assert.Equal(
            new HmacSigner("agent-secret").Sign(
                "PATCH",
                "/api/agent/delivery-writeback",
                handler.LastTimestamp!,
                handler.LastNonce!,
                handler.LastContentSha256!),
            handler.LastSignature);
        Assert.DoesNotContain("rxNumber", handler.LastBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt", handler.LastBody!, StringComparison.OrdinalIgnoreCase);
        using var callback = JsonDocument.Parse(handler.LastBody!);
        var names = callback.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "schemaVersion", "writebackId", "commandId", "candidateId", "rxHash",
                "evidenceId", "pharmacyId", "orderId", "inboxItemId", "pmsReferenceId",
                "proofRecordId", "proofDigest", "transition", "transitionAt", "resultCode",
            },
            names);
    }

    [Fact]
    public async Task SendDeliveryWritebackAsync_RejectsUnsignedOrByteMismatchedReceipt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var command = DeliveryCommand();
        var signedBody = DeliveryReceiptJson(
            command,
            DeliveryWritebackResultCode.Success,
            idempotent: true);
        var returnedBody = signedBody + " ";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(returnedBody, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add(
            "X-Response-Signature",
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(signedBody),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" },
            handler,
            publicKey);

        Assert.Null(await client.SendDeliveryWritebackAsync(
            command,
            DeliveryWritebackResultCode.Success,
            CancellationToken.None));
    }

    [Fact]
    public void DeliveryWritebackReceipt_RejectsExtraFieldsAndIdentityOrResultMismatch()
    {
        var command = DeliveryCommand();
        var wrongOrder = JsonSerializer.Deserialize<JsonElement>(DeliveryReceiptJson(
            command with { OrderId = "00000000-0000-4000-8000-000000000099" },
            DeliveryWritebackResultCode.Success,
            idempotent: false));
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            wrongOrder,
            command,
            DeliveryWritebackResultCode.Success,
            out _));

        var wrongResult = JsonSerializer.Deserialize<JsonElement>(DeliveryReceiptJson(
            command,
            DeliveryWritebackResultCode.ManualReview,
            idempotent: false));
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            wrongResult,
            command,
            DeliveryWritebackResultCode.Success,
            out _));

        var withExtra = JsonSerializer.SerializeToElement(new
        {
            success = true,
            data = new
            {
                schemaVersion = 1,
                writebackId = command.WritebackId,
                commandId = command.CommandId,
                pharmacyId = command.PharmacyId,
                orderId = command.OrderId,
                candidateId = command.CandidateId,
                transition = command.Transition,
                status = "succeeded",
                resultCode = "success",
                completedAt = "2026-07-10T12:16:00.000Z",
                idempotent = false,
                unexpected = true,
            },
        });
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            withExtra,
            command,
            DeliveryWritebackResultCode.Success,
            out _));
    }

    [Fact]
    public async Task PostSignedAsync_ShadowMode_AllowsUnrecognizedToken_PreservingAvailability()
    {
        // Default (shadow) mode must NOT change behavior — a packed identifier that passes the
        // legacy <=96-char charset but is not a known token shape is STILL allowed, so the guard
        // never silently takes the agent offline. (It logs a would-block; we assert it sends.)
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" }, // StrictOutboundTokenAllowlist defaults false
            handler);

        await client.PostSignedAsync(
            "/api/agent/heartbeat",
            new { status = "ok", marker = "DOE-JOHN-1990" }, // charset-clean, NOT a known token
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_BlocksUnrecognizedTokenBeforeSending()
    {
        // Strict mode closes the <=96-char escape hatch: a packed identifier that matches no
        // operational token shape is BLOCKED before the POST.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/heartbeat",
                new { status = "ok", marker = "DOE-JOHN-1990" },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_BlocksGeographicField()
    {
        // city/state/zip5 are Safe-Harbor identifiers — strict mode blocks them on non-PHI paths.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/sync",
                new { snapshotType = "x", data = new { zip5 = "92101" } },
                CancellationToken.None));

        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_AllowsKnownTokenShapes()
    {
        // Machine token shapes (uuid, hex hash, semver) stay safe even under strict mode and even
        // under non-operational field names.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        await client.PostSignedAsync(
            "/api/agent/heartbeat",
            new
            {
                status = "ok",
                runToken = "3f2504e0-4f89-41d3-9a0c-0305e82c3301", // uuid
                build = "v3.19.4",                                  // semver
                fingerprint = new string('a', 32),                 // hex hash
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task AckCommand_StrictMode_AllowsPhiFreeLearnedRuleReceipt()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                StrictOutboundTokenAllowlist = true,
            },
            handler);

        await client.AckCommandAsync(
            "33333333-3333-4333-8333-333333333333",
            success: true,
            result: new
            {
                approval_id = "11111111-1111-4111-8111-111111111111",
                rule_id = "auto.learned.aaaaaaaaaaaa",
                template_id = new string('a', 64),
                run_id = "22222222-2222-4222-8222-222222222222",
                outcome = "Completed",
                steps_completed = 3,
                failed_ordinal = (int?)null,
            },
            error: null,
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

}
