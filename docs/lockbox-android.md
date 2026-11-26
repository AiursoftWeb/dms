# Lockbox for Android

A native .NET for Android app for reviewing DMS Lockboxes. The UI uses Material 3 and AndroidX AppCompat: outlined connection fields, request cards with risk chips, and a confirmation dialog for approval decisions. The Android project is built separately so server and CLI builds do not need the Android workload.

## Build and install a development APK

Use .NET 10 with the Android workload and its supported Android SDK and JDK. If the SDK and JDK are not available, install them into user-owned directories. Review the Android SDK licenses before choosing to accept them.

```bash
dotnet workload install android
project=src/Aiursoft.DMS.Lockbox.Android/Aiursoft.DMS.Lockbox.Android.csproj
tools="$HOME/.local/share/dms-android"
mkdir -p "$tools/sdk" "$tools/jdk"
dotnet build "$project" -t:InstallAndroidDependencies \
  -p:AndroidSdkDirectory="$tools/sdk" -p:JavaSdkDirectory="$tools/jdk" \
  -p:AcceptAndroidSDKLicenses=true
dotnet build "$project" -c Debug -p:AndroidPackageFormats=apk \
  -p:AndroidSdkDirectory="$tools/sdk" -p:JavaSdkDirectory="$tools/jdk"
```

If workload installation reports success but the build still reports `NETSDK1147`, try matching the workload set to the SDK (on this development host: `dotnet workload install android --version 10.0.110`).

Install the `*-Signed.apk` from the project's `bin/Debug/net10.0-android/` directory on the test phone over USB, for example with `adb install -r <apk-path>`. Google Play is not required.

## Connect and review

1. Deploy a Controller that includes `GET /api/v1/dms/lockboxes/review`. Give the phone network access to its HTTPS origin with a trusted certificate; the app rejects HTTP and redirects.
2. In **连接设置**, enter the Controller origin and a **DMS personal API key** (`dmsu_`), not a Kanban, agent, or join key. The key is verified against DMS and saved encrypted with a key owned by Android Keystore. Clearing the connection removes the device copy; revoke on the Controller if the key should no longer work.
3. In **待我审批**, refresh the list. Before paging, the Controller filters by approval permissions and confirms each frozen workflow name, version and risk still match an active public definition. An obsolete request is omitted from the review queue and reminders; if its details are already open, refresh to see that it needs to be resubmitted. The request is not deleted. For current requests, check the parameters and frozen target list before deciding. If the request has changed or the submission result is uncertain, the app refreshes the status without automatically resubmitting.

## My approval history

From **待我审批**, open **我的审批记录** to see the requests you personally approved or denied, newest decision first. Open a record to read the original request, decision, reviewer, decision time and any recorded reason. Pending, withdrawn, automatic and other reviewers' decisions are excluded. The Controller must expose `GET /api/v1/dms/lockboxes/review/history`.

Validation (2026-11-26): all 64 focused regression tests passed, and the restarted USB lab returned five existing decisions for the test reviewer (three approved, two denied). The in-place update preserved the phone's connection and showed the history entry point. Automated tapping was blocked by the phone's `INJECT_EVENTS` restriction; opening the history and details on the device is a manual check.

## On-call notifications without Google

On **待我审批**, tap **值班提醒** and allow Android notifications. The visible on-call service opens an authenticated long-poll connection directly to the same Controller; a newly stored, eligible pending request wakes the wait. The Controller uses the same permission filter as the review queue and returns only a timestamp cursor, not an applicant, request ID, command, or target. The phone notification opens the queue and cannot approve anything. Stop the session from its ongoing notification or the queue. Changing or clearing the saved connection stops the session; restart it explicitly when needed.

- Deploy `GET /api/v1/dms/lockboxes/review/updates` on the same Controller as the queue. Configure the HTTPS reverse proxy to allow at least 35 seconds for the response; the Controller normally returns after up to 20 seconds of idle wait. Sessions reconnect with backoff on connection failures and stop on authorization failure.
- The APK needs neither Firebase nor Google Play Services. This is a user-started foreground notification service, not a platform push guarantee. Force-stop, reboot, Doze, manufacturer restrictions, network loss and Android notification settings can prevent or delay delivery. Keep the session running during on-call, and use manual refresh when it stops.

## Device acceptance checks

- Verify a personal key, restart the app, and check that the connection is restored. Clear it and verify it no longer restores. Invalid keys, an untrusted certificate, and HTTP should fail.
- With accounts that have different approval grants, check risk filtering and self-approval restrictions, including restrictions on privileged requests. Verify request parameters and all targets are visible before acting.
- Have another approver act or the requester withdraw after opening the details. Confirm that the app shows the updated state and does not resubmit. Also check status after a connection loss during submission.
- On an actual on-call phone using a test Controller, enable **值班提醒**, background the app and lock the screen. Submit an eligible request; use 30 seconds from submission to phone alert as the initial acceptance target on a healthy network. Confirm the alert contains no request data and tapping it refreshes the queue. An ineligible or obsolete request must not alert. While a details load or decision is in flight, tap the notification and confirm the app opens the queue after that operation finishes; multiple taps during the same operation must not cause multiple navigations.
- While the session is active, disconnect the phone from the network, submit an eligible request and leave it pending, then restore connectivity. Verify that the phone alerts without opening the app or restarting the session. Stop the session and verify that later requests no longer alert.
- If direct notifications are unreliable on the on-call phones, prioritize a server-side escalation for requests still pending after an agreed interval through the existing on-call channel. This fallback is not implemented; do not switch to a separate push relay before verifying the direct path.

## #2423 validation record

- The Android debug APK built without warnings and its signature verified with `apksigner`. The manifest declares the non-exported on-call service, disables cleartext and requests Android notification permission.
- The focused DMS regression run passed 64 tests, including the review queue, approval history, approval conflicts, authorization and authenticated notification delivery from the Controller. The repository's `bash lint.sh` also passed after documenting the public JSON contracts that are consumed outside the server solution.
- Emulator UI walkthrough (2026-11-26): T3 discovered and booted `DMS_Lockbox_API_36` after the SDK exposed `cmdline-tools/latest` and the device hub could find `java` on its PATH. The standard APK installed and opened the connection screen; Android masked its screenshots as intended. A separate demo application ID with synthetic responses and capture enabled was used to take actual emulator screenshots of connection settings, the review queue and review details. The standard APK was unchanged.
- Material 3 UI (2026-11-26): rebuilt the signed APK and walked through the redesigned settings, review queue, and details on the Android 36 emulator with synthetic records. Verified that toolbar navigation works after page changes with system-bar insets applied. The standard app keeps capture protection.
- Direct notification follow-up (2026-11-26): both targeted review-queue and notification authorization tests passed.
- USB device smoke test (2026-11-26): installed the signed debug APK on a Xiaomi 25067PYE3C running Android 16. The first installation was canceled by the device installer; the retry succeeded. T3 confirmed the app opened its connection settings with the HTTPS address and personal-key fields.
- Isolated USB lab (2026-11-26): started a local Controller at `https://127.0.0.1:18443` with a separate SQLite database, local state and file storage, no registered Agent, and a six-hour process timeout. Verified TLS, test-key authentication, Lockbox creation, review filtering, and notification API response using lab-only accounts. The USB reverse rule maps port `18443` on the Xiaomi phone to that host port. A separate test APK trusts the lab CA only for the loopback endpoint; the standard APK and phone-wide trust settings were not changed. After the phone was unlocked, the test APK installed and opened its connection screen. T3 input was denied with `INJECT_EVENTS permission` on both the initial and diagnostic attempt. No key was entered on the phone. Next: authorize USB input on the Xiaomi device or enter the lab connection manually; then verify connectivity, lock-screen delivery, and recovery. No production Controller was changed.
- Xiaomi notification exercise (2026-11-26): restored the USB forwarding rule. Submitted `lb/f61825d1-4628-495c-809c-76855eaf47d0` at 13:43 (UTC+08:00) against the lab-only target; the reviewer updates endpoint saw it. Scheduled three more requests two minutes apart. Phone notification receipt and lock-screen behavior remain unverified. At the user's request the scheduled sender was stopped before its first timed submission, the outstanding exercise request was withdrawn, and the test APK was installed afresh and opened to the connection form. After the user confirmed the reminder was enabled again, submitted `lb/eccc1e89-4206-45e3-b799-78d0f0fa9eca` at 13:47 (UTC+08:00) and verified the reviewer updates endpoint saw it. Submitted a second request for the lock-screen check, `lb/b3d42954-a33c-4232-99c6-60daf72460f2`, at 13:48:13 (UTC+08:00); the reviewer updates endpoint confirmed it. For a third request (`lb/b6dc7537-6c4b-4a81-9ba1-fe81a2f04543`, 13:49:27 UTC+08:00), verified the server update and observed an active Android `lockbox-review` notification and the running foreground listener. The user had reported no lock-screen alert on the prior attempt; visibility on the Xiaomi lock screen remains unverified. After the user adjusted lock-screen settings, sent `lb/0c98b619-33ed-440e-ae83-9b703b4dabdf` at 13:53:02 (UTC+08:00). The reviewer updates endpoint responded and Android had an active review notification. The user confirmed the notification appeared on the Xiaomi lock screen after adjusting the lock-screen settings. Offline recovery and other phone models remain unverified. No timed sender is running.
- USB lab shutdown (2026-11-26): stopped the isolated Controller at the user's request. Verified that port `18443` is no longer listening, the phone's USB forwarding rule is absent, and no timed test sender remains. The test database and APK were retained.
- [MR !150](https://gitlab.aiursoft.com/aiursoft/dms/-/merge_requests/150) review fixes (2026-11-26): 67 focused tests passed, including stale-workflow filtering and deferred notification navigation. `bash lint.sh` passed and the Android Release APK built without warnings. Inspected the `10.37.0` Contracts package and confirmed all five review, history and update DTOs are in the packaged assembly. The busy-state notification tap still needs an on-device check; the package has not been published locally, and the USB lab was not restarted.
- Full-suite follow-up (2026-11-26): corrected the queue pagination test so older pending requests left by other integration tests do not invalidate its assertions. Ran the solution with CI coverage and JUnit settings, an isolated home without saved DMS credentials, and the local Playwright Chromium executable: all 860 tests passed.
