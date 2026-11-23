# Remaining work

The bench prototype now supports fingerprint or PIN entry, matrix feedback,
solenoid control, and remote HTTPS policy polling from the Azure-hosted portal.
See the [project overview](../README.md) for the implemented architecture and
[remote validation record](../hardware/arduino/E2E_remote/validation.md) for
recorded results and outstanding hardware checks.

## Device identity and policy delivery

- Register individual devices and authenticate policy requests. The current
  read-only API is anonymous and returns policy metadata and PIN verifiers.
- Replace the shared bench PIN key with a device-specific provisioning and
  recovery design.
- Bind reader template slots to access identities and handle deletion,
  re-enrolment and revocation explicitly.
- Track policy versions delivered to each device, acknowledgements, delivery
  status and audit events.
  The portal currently has no live device telemetry.

## Hardware and failure testing

- Complete the remote sketch's fault-injection and endurance checklist,
  including network loss, stale clocks, stale policies and recovery.
- Add the omitted MOSFET gate pull-down and assess an independent actuator
  timeout. A blocked controller must not leave a solenoid energised.
- Record repeatable wiring, power-on behaviour and hardware dependency versions.
- Test supervised enrolment/deletion and recovery from interrupted USB updates.

## Hosting and operations

- Close the Keycloak origin-isolation gap: Front Door backend network ranges
  alone do not restrict traffic to this project's Front Door profile.
- Review administrative access restrictions and demonstrate database restore
  and image upgrade recovery using the existing infrastructure scripts.
- Review portal session-key persistence before scaling or relying on sessions
  surviving replacement, and add operational monitoring and alerting.
- Record cloud operating costs and retention policies for future audit data.

These are follow-up items, not completed controls or delivery commitments.
Installation on an occupied door needs a separate physical-security, electrical
and emergency-egress assessment. This repository remains a bench prototype.
