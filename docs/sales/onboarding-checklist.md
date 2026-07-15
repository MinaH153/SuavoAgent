# New Pharmacy Onboarding Checklist

## Pre-Visit

- [ ] Confirm pharmacy name, address, NPI
- [ ] Identify PMS system (PioneerRx, QS/1, Liberty, etc.)
- [ ] Print employee notice template (docs/compliance/employee-notice-template.md)
- [ ] Print one-pager (docs/sales/pharmacy-one-pager.md)
- [ ] Prepare BAA for signature
- [ ] Verify state compliance requirements (docs/compliance/state-compliance-matrix.md)
- [ ] Confirm the pharmacy and authorized installer entitlement appear in the Suavo dashboard
- [ ] Confirm the installing owner/PIC can complete MFA and approve device pairing
- [ ] Download a fresh signed `SuavoAgent-Setup.exe` only from that authenticated dashboard

## During Visit

### Phase 1: Paperwork (5 min)
- [ ] Business owner signs MSA
- [ ] Business owner signs BAA
- [ ] Distribute employee notice to all staff at monitored workstations
- [ ] Collect employee acknowledgment signatures
- [ ] Note which computer(s) to install on

### Phase 2: Install (5 min)
- [ ] Download the signed SuavoAgent installer from the pharmacy dashboard
- [ ] Open the signed native installer, approve the verified MKM Technologies LLC publisher, and follow the pairing wizard
- [ ] Confirm PMS auto-detected
- [ ] Confirm SQL/PioneerRx canary is green without copying or recording credentials
- [ ] Confirm the observed SQL Server certificate digest is included in the signed probation proof and promoted to the workstation identity
- [ ] Verify services running (Core + Broker + Watchdog)
- [ ] Verify Helper attaches to PMS
- [ ] Confirm heartbeat appears on cloud dashboard
- [ ] Confirm any PioneerRx process approval is bound to that same promoted SQL certificate identity

### Phase 3: Verify (2 min)
- [ ] Run **Diagnostics** in the dashboard and confirm its PHI-safe health receipt is green
- [ ] Confirm system tray indicator is visible
- [ ] Show pharmacy owner the disclosure indicator
- [ ] Verify learning mode is active on dashboard
- [ ] Confirm **Windows Settings → Apps → SuavoAgent** offers native Repair and Uninstall
- [ ] Demonstrate the pharmacist panda's exact **Pause Autopilot**, **Resume**, and **Stop Autopilot** acknowledgements

## Post-Visit (Same Day)

- [ ] Verify heartbeat continues on dashboard
- [ ] Check first Rx detection batch arrives
- [ ] Send follow-up email/text to pharmacy owner
- [ ] Log visit in CRM/tracker
- [ ] Schedule 1-week check-in call

## 1-Week Check-in

- [ ] Verify agent has been running 7 days continuously
- [ ] Check learning progress on dashboard
- [ ] Any errors or offline periods?
- [ ] Ask pharmacy owner if staff has questions
- [ ] Confirm delivery service scheduling

## 30-Day Activation

- [ ] Review POM (Pharmacy Operating Model) on dashboard
- [ ] Approve model if learning looks correct
- [ ] Transition to active mode
- [ ] Schedule first delivery batch
- [ ] For pricing pilots, obtain PIC approval of the exact cost basis and pass the supervised 10 → 100 → 500 reconciliation gates before unattended execution
