# TODO

## Usage statistics (idea, not started)

Goal: know roughly how many devices used the recorder in the last month, split
by platform (Windows desktop / Linux x64 / Linux arm64).

Sketch:

- Random install ID (UUID) generated on first run and stored in `config.json`.
  No hardware fingerprinting.
- At most one small background ping per day: install ID, app version, OS, arch,
  flavor (`desktop` / `cli`). Short timeout, errors ignored — no network must
  never affect recording.
- Server side: one row per ping, monthly unique devices =
  `COUNT(DISTINCT id) GROUP BY os` over the last 30 days. Do not store IPs.
- Candidates instead of a custom endpoint: Aptabase (desktop-focused, .NET SDK,
  self-hostable), PostHog. Cheapest variant may be piggybacking on an update
  check if the recorder ever gets one.

Before shipping:

- Say so in the README, and add `"telemetry": false` to switch it off
  (consider opt-in instead of opt-out).
- GDPR: a persistent install ID can count as personal data — keep the payload
  minimal and document it.
- Agree with the RLT author, since it would carry the RLT name.
