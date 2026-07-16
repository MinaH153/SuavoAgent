# OTA Crash-Loop Guard — Design

Status: superseded by the SYSTEM-owned signed cohort transaction · 2026-07-10.

## Problem

The former LocalService-owned in-place swap could leave mixed binaries and had no trustworthy
post-restart commitment gate. It has been removed. Core can now only stage signed bytes in
ProgramData; LocalSystem Maintenance owns quiesce, same-volume cohort swap, health proof, rollback,
and durable completion.

## Current decision

The activation request and 11/13-field binary manifest are independently signature-verified by
Watchdog and Maintenance. Maintenance copies them into an Administrator/SYSTEM-only immutable claim,
reserves an authoritative replay identity, and runs the complete install directory through the durable
`InstallCohortTransaction` journal.

Commit requires all of these:

1. exact signed cohort and release receipts;
2. hardened install/data/maintenance ACLs;
3. Core, Broker, and Watchdog running from the fixed install directory;
4. interactive Helper/IPC health;
5. a fresh one-time target-version milestone written only after a successful cloud heartbeat.

Any activation or health failure restores the intact prior directory and prior manifest as one rollback
unit. Watchdog monitors the SYSTEM claim heartbeat and relaunches the trusted native coordinator if it
dies before writing terminal completion. No PowerShell, command shell, or LocalService install mutation
exists in this path.

## Test plan (follow-up)

Automated coverage includes signed-envelope rejection, replay leases, request/payload TOCTOU, durable
claim recovery, every transaction crash phase, and target heartbeat challenge binding. The remaining
release gate is a Windows sandbox/pilot test that stages a deliberately crashing target and proves the
prior cohort returns online without remote shell intervention.
