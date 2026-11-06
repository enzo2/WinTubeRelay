# Playback lifecycle fixes

Validated on the isolated Windows tree `C:\lucas\WinTubeRelay-validation` with SDK 10.0.204.
The harness uses unique `wintuberelay-test-*` pipes and a fake mpv process. It does not touch the
production pipe, live `C:\lucas\WinTubeRelay`, or real settings.

## Results

Debug harness, after `dotnet build tests/PlaybackRecoveryTests/PlaybackRecoveryTests.csproj -c Debug -v q`
and `dotnet run --project tests/PlaybackRecoveryTests/PlaybackRecoveryTests.csproj -c Debug --no-build`:

```text
PLAYBACK_RECOVERY_RESULT ran=48 fail=0
```

Release app build:

```text
WinTubeRelay -> C:\lucas\WinTubeRelay-validation\bin\Release\net10.0-windows\win-x64\WinTubeRelay.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Release publish, isolated to the validation tree:

```text
WinTubeRelay -> C:\lucas\WinTubeRelay-validation\publish\win-x64\
PUBLISH_EXE C:\lucas\WinTubeRelay-validation\publish\win-x64\WinTubeRelay.exe bytes=63841204
```

`git diff --check` on the source tree: clean.

Release test-project build is not possible with the existing project settings. `WinTubeRelay.csproj`
sets `SelfContained` for Release, and the SDK then refuses the test reference (`NETSDK1151`).
The harness gate is the Debug run above. That gap is pre-existing and was not changed.

## Review dispositions

1. Deadlock. `InvokeExclusive` no longer takes the mpv `_gate`. Prepare can send to the UI while a
   distinct operation gate still serializes prepare plus the command. Tray player actions, quit, and
   audio reapply run off the UI thread, so the UI thread does not wait on either gate.
   Regression: `exclusive_prepare_does_not_deadlock_ui_command` failed on the old lock (UI send timed out)
   and now passes. `exclusive_actions_do_not_interleave` keeps the prepare/command order.

2. Settings. `SettingsStore.Update` mutates, snapshots, and persists under one lock. API favorite/recent
   handlers and tray favorite, recent, selected-device, and settings-form writes go through that lock.
   List reads used by the API and tray menus copy under the same lock. Atomic `.bak` replace remains.
   Regression: overlapping `POST /favorites` and `POST /recent/clear` cannot clear recent while the
   favorite save holds the lock, and the reloaded file keeps the favorite. No secrets in the fixture.

3. Monitor. A single failed reconnect no longer stops the loop. Transient failures retry with bounded
   backoff until cancelled or the tracked process exits. Adopting a live player, or returning to an
   already tracked process, restarts a completed monitor. Regression drops the monitor, fails one
   reconnect, accepts the next, and observes `end-file` on a unique fake pipe.

4. IPC. A command is marked written before `WriteLine`, and a retry happens only when connect itself
   failed and nothing was written. Reads use `ReadLineAsync` plus `WaitAsync` with the existing 1s
   deadline instead of a thread per read. Event, foreign `request_id`, error, and deadline tests still pass.
   Regression: a write that throws after connect is attempted once.

5. `ClassifyState`. `idle-active=true` wins over a stale nonempty path. Regression expects idle, not playing.

6. Toggle. A known paused player resumes; a known playing player pauses from the current status.
   Toggle-to-pause sets the intentional pause and suppresses failsafe. Unknown state does not cycle.

7. Failsafe resume position is cleared after the load is sent and after the load fails.
   A null `playlist-count` suppresses `loadfile replace`; replace runs only when the count is known 0.

8. Single instance. `AbandonedMutexException` counts as acquired. Disposal releases the owned mutex.
   A same-thread second acquire still fails. Regression covers crash/abandon and a later acquire after release.

9. Wake coalescer. A wake that arrives between consume and complete is kept and returned so the caller
   wakes again. Regression covers that gap. The earlier queued-wake test still passes.

10. Web UI API key. `getHeaders` is executed with node (`C:\Program Files\nodejs\node.exe`) against the
    production `static/index.html`. A fixture key is sent as `x-api-key`; a blank key is omitted.
    The string presence check remains.

11. Shutdown and restart. Tray exit and web-server restart no longer block the UI thread on
    `DisposeAsync` or `RestartAsync`. `ExitThreadCore` is async, ignores a repeated call, leaves the
    message pump running until stop finishes, and disposes the icon, timers, audio, and player only
    after that drain. Dispose and restart errors are logged and do not escape the async void path.
    Icon and audio callbacks no-op once the shell is disposed. Host start, stop, and dispose are
    invoked with the UI synchronization context cleared, then awaited with `ConfigureAwait(false)`.
    `ConfigureAwait(false)` alone did not stop Kestrel from capturing that context: blocking
    `DisposeAsync` on the caller context still timed out until the context was cleared for the host
    call. Constructor `StartAsync` still waits synchronously; on SDK 10.0.204 that start completed
    even before the clear, and the clear keeps a yielding start from posting back to the UI.
    Regressions, all red before the fix except the constructor start case: blocking stop on a caller
    synchronization context timed out; idle shutdown and shutdown/restart with an in-flight `/play`
    that had to marshal to that context did not return to the pump; a second shutdown disposed again;
    a throwing dispose callback escaped and was not logged. They now pass. Restart does not bind the
    new port until the in-flight request finishes, then `/health` answers on the new port and the old
    port refuses. The harness calls `TrayWebLifecycle` and the real `WebServerService`, not the tray
    icon and not a source-string check.

## Not separately regression-tested

- Two fresh launches racing before any pipe exists are still serialized by `EnsureMpv`'s lock.
  The harness proves adoption of an already-open pipe, not a timed first-launch pileup.
- Tray icon code is not instantiated in the harness. Doing so would touch the live session.
  Shutdown and restart are covered through `TrayWebLifecycle` and the real `WebServerService`.
  The earlier player-gate deadlock regression still exercises the gate split the tray uses.
- Release configuration of the test project cannot be built, as noted above.
