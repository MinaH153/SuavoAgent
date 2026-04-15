# SuavoAgent State Compliance Matrix

## Mandatory Written Notice States

These states REQUIRE written notice to employees before electronic monitoring.
SuavoAgent MUST NOT be deployed without distributing the employee notice template.

| State | Statute | Requirement | Penalty |
|-------|---------|-------------|---------|
| **Connecticut** | Conn. Gen. Stat. § 31-48d | Prior written notice + conspicuous posting | $500 first offense, $1,000-$3,000 subsequent |
| **Delaware** | Del. Code tit. 19, § 705 | Prior written notice of monitoring types | Civil penalties |
| **New York** | NY Civil Rights Law § 52-c | Written notice upon hiring + conspicuous posting + employee acknowledgment | Civil penalties |

## High-Risk States (Strong Privacy Protections)

These states have broad privacy protections that affect desktop monitoring.

| State | Risk | Key Statute | Notes |
|-------|------|-------------|-------|
| **California** | CRITICAL | CCPA/CPRA + Cal. Const. Art I § 1 | Employee data fully in CCPA scope since Jan 2023. Notice at collection required. All-party consent wiretap state. |
| **Illinois** | CRITICAL | BIPA (740 ILCS 14) | If ANY biometric capture: written consent required. $1K-$5K per violation. Private right of action. Do NOT capture biometric data. |
| **Massachusetts** | HIGH | Mass. Gen. Laws ch. 272 § 99 | All-party consent wiretap. Broadest in the nation. |
| **Maryland** | HIGH | Md. Code Ann., Cts. & Jud. Proc. § 10-402 | All-party consent for communication interception. |
| **Colorado** | MEDIUM | CPA (Colo. Rev. Stat. § 6-1-1301) | Data protection assessment required for "profiling" activities. |
| **Montana** | MEDIUM | Mont. Code Ann. § 45-8-213 | Criminal surreptitious surveillance statute. |

## Recommended Practice (All States)

Even in states without explicit monitoring statutes, provide:
1. Written employee notice (use template)
2. Employee acknowledgment signature
3. System tray disclosure indicator (built into SuavoAgent v3.7+)

## Verticals to AVOID

| Vertical | Risk | Reason |
|----------|------|--------|
| **Law Firms** | CRITICAL | Attorney-client privilege. Even hashed file names can violate privilege. |
| **Healthcare (non-pharmacy)** | HIGH | EHR screen observation expands HIPAA scope beyond BAA. |
| **Financial Services** | HIGH | SEC/FINRA record retention + SOX audit trail implications. |

## Deployment Checklist

Before deploying SuavoAgent at any business:

- [ ] Verify state does not prohibit undisclosed monitoring
- [ ] Business owner signs MSA with monitoring disclosure clause
- [ ] Employee notice template distributed to all affected employees
- [ ] Employee acknowledgments collected and retained by business
- [ ] System tray indicator enabled on all monitored workstations
- [ ] BAA executed (if healthcare/pharmacy)
- [ ] State-specific requirements met (see table above)
