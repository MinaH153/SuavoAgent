# New Pharmacy Onboarding Checklist

## Pre-Visit

- [ ] Confirm pharmacy name, address, NPI
- [ ] Identify PMS system (PioneerRx, QS/1, Liberty, etc.)
- [ ] Print employee notice template (docs/compliance/employee-notice-template.md)
- [ ] Print one-pager (docs/sales/pharmacy-one-pager.md)
- [ ] Prepare BAA for signature
- [ ] Verify state compliance requirements (docs/compliance/state-compliance-matrix.md)
- [ ] Generate pharmacy ID and API key in Suavo cloud dashboard
- [ ] Create setup.json with pharmacy credentials

## During Visit

### Phase 1: Paperwork (5 min)
- [ ] Business owner signs MSA
- [ ] Business owner signs BAA
- [ ] Distribute employee notice to all staff at monitored workstations
- [ ] Collect employee acknowledgment signatures
- [ ] Note which computer(s) to install on

### Phase 2: Install (5 min)
- [ ] Open admin PowerShell on pharmacy computer
- [ ] Run bootstrap.ps1 one-liner
- [ ] Confirm PMS auto-detected
- [ ] Confirm SQL credentials discovered
- [ ] Verify services running (Core + Broker)
- [ ] Verify Helper attaches to PMS
- [ ] Confirm heartbeat appears on cloud dashboard

### Phase 3: Verify (2 min)
- [ ] Check logs: no errors in first 60 seconds
- [ ] Confirm system tray indicator is visible
- [ ] Show pharmacy owner the disclosure indicator
- [ ] Verify learning mode is active on dashboard

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
- [ ] Celebrate with the pharmacy owner
