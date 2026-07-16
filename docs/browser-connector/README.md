# Suavo browser observation connector

Status: implementation-ready protocol and MV3 extension source. It is **not
deployed**. The templates intentionally contain invalid placeholders until the
Chrome Web Store and Microsoft Edge Add-ons listings issue immutable extension
IDs and the release process signs an authority document.

## Privacy boundary

The connector observes one value: the normalized hostname of the active
HTTP/HTTPS tab. The extension reads the tab URL locally because Chromium does
not expose the hostname separately, immediately discards the URL, and sends
only `hostname`. It never reads or sends:

- page titles or tab titles;
- URL scheme, path, query, fragment, or credentials;
- page content, DOM text, form values, cookies, or browsing history;
- inactive-tab URLs.

The native host normalizes the hostname again. Known hostnames become a coarse
adapter category. Unknown hostnames become a keyed HMAC-SHA-256 value. The raw
hostname is not representable in `BrowserDomainObservation` and does not enter
the behavioral event buffer or logs.

## Trust chain

1. Chrome or Edge launches the registered native host and supplies the caller's
   exact `chrome-extension://<id>/` origin.
2. `BrowserConnectorAuthorityVerifier` accepts that origin only from a
   non-expired schema-v2 P-256-signed allowlist. Every extension entry binds
   the origin and browser family to the exact canonical browser executable
   path authorized for that device. It rejects schema v1, duplicate identities,
   malformed IDs, wildcard origins, UNC/environment/relative paths, wrong
   browser filenames, and authority lifetimes over 31 days.
3. `WindowsBrowserNativeChannelVerifier` reads the host process's actual
   standard-input and standard-output handles with `GetStdHandle`. Both must be
   named-pipe client handles whose kernel-reported server PID is the same
   non-zero process. Its canonical image path must equal the device-specific
   signed path and sit below the OS-derived machine Program Files Chrome/Edge
   vendor directory. Per-user LocalAppData installs are never eligible. Every
   path ancestor must be non-reparse, and the Program Files root, vendor
   directories, and executable ACLs must grant no write/create/modify/delete/
   ownership capability to the current user or any non-admin principal. The
   executable must also have the exact expected filename/original filename,
   valid Authenticode signature from the exact Google or Microsoft organization,
   and the same Windows user/session as the host. Missing evidence fails closed
   before the relay connects or any lease-derived key is requested.
4. `WindowsBrowserParentVerifier` corroborates that channel authority by
   proving the direct parent is the expected signed browser. A non-zero
   `--parent-window` owner must independently match the same browser family.
   A zero handle is allowed for an MV3 service worker. PPID is corroboration
   only; it can never substitute for the kernel-reported standard-pipe peers.
5. The host sends a random 256-bit in-memory session key and random 256-bit
   challenge over the browser-created native-messaging channel. The key is
   never persisted. Each message must contain the exact session ID, exact next
   counter, current challenge, and HMAC. An accepted message rotates the
   challenge. Replay, gaps, stale challenges, bad MACs, and expired sessions
   terminate the connection.
6. Host acknowledgements and fatal status messages carry a host HMAC so the
   extension never advances its counter on an unauthenticated response.

The signed authority `revision` must be persisted with a monotonic high-water
mark by the installer/maintenance authority before production activation.
Registry provisioning and that high-water store are intentionally outside this
component; a caller must pass a `VerifiedBrowserConnectorAuthority`, never a
raw JSON allowlist.

## Installer provisioning contract

The privileged native installer or maintenance cohort must provision browser
authority atomically. It must:

1. discover Chrome and Edge only beneath the OS-derived machine `Program Files`
   or `Program Files (x86)` vendor directories;
2. open each executable and record the final canonical DOS path from its file
   handle, rejecting UNC, environment-expanded, relative, per-user, reparse,
   or writable-ACL candidates;
3. obtain a device-bound schema-v2 signed authority whose canonical signature
   bytes include each exact executable path;
4. validate the signature, revision, exact paths, Authenticode publishers, and
   ACL/reparse evidence again immediately before atomically writing
   `authority.json` and its monotonic revision receipt to the protected
   `browser-connector-trust` ProgramData directory; and
5. register native-host manifests only after that write succeeds. Uninstall
   must remove both browser registrations and the authority/receipt together.

A schema-v1 document, a cloud-wide generic browser path, or a path supplied by
an unprivileged user is never a production provisioning input.

## Protocol

Native messages use Chromium's four-byte little-endian length prefix and a
strict JSON object. The host hard-caps frames at 4,096 bytes before allocation.
Unknown or duplicate JSON properties are rejected.

The post-handshake client HMAC canonical form is:

```text
suavo-native-messaging-v1\n
<sessionId>\n
<counter>\n
<challenge>\n
<hostname>
```

The accepted-host response canonical form is:

```text
suavo-native-messaging-v1\n
<sessionId>\n
accepted\n
<counter>\n
<nextChallenge>\n
ready
```

All encodings are UTF-8. HMAC values, challenges, keys, and session IDs use
unpadded base64url. Counter `1` is the first client message.

## Extension permissions

`browser-extension/manifest.json` requests only:

- `nativeMessaging`, to reach the local Suavo host;
- `tabs`, because continuous active-tab hostname observation requires access to
  the active tab URL without a user click.

There are no host permissions, content scripts, web-accessible resources,
remote code, DOM access, or network requests. `activeTab` is insufficient for
the always-on employee-observation mode because it requires a user gesture for
each grant.

## Release activation gates

Do not replace the placeholders or register the native host during a normal
developer build. Production activation requires all of the following evidence:

- published Chrome and Edge extension IDs;
- reviewed store packages built from this exact source;
- signed, non-expired schema-v2 connector authority with a monotonic revision
  and exact protected machine browser paths for this device;
- native host path pinned to the Authenticode-signed Suavo release cohort;
- machine-level Chrome and Edge native-host registration with an exact
  uninstall receipt (installer-owned work);
- enterprise browser policy allowlisting the exact native host where managed
  browser policy blocks native messaging;
- a wired native-host execution entry point and a Helper sink using the current
  observation-key lease;
- real Windows Chrome and Edge launches proving both inherited standard handles
  report the browser process as their named-pipe server before relay access;
- real Windows tests for browser restart, MV3 worker suspension, parent-window
  zero/non-zero, invalid extension origin, replay, authority expiry/revocation,
  lock/logoff, and uninstall.

Until those gates pass, browser observation health must remain unavailable or
degraded. It must never be inferred from a browser window title.

## Files

- `src/SuavoAgent.Helper/SystemObservers/BrowserConnector/` — signed authority,
  parent-browser verification, framing, session authentication, hashing, and
  privacy-safe status contracts.
- `browser-extension/service-worker.js` — Chrome/Edge MV3 client.
- `browser-extension/native-host.chrome.json.template` — Chrome host manifest.
- `browser-extension/native-host.edge.json.template` — Edge host manifest.
- `browser-extension/connector-authority.json.template` — deliberately
  non-runnable signed-authority shape.
