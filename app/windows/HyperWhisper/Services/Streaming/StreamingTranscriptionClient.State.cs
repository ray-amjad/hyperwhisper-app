using System;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services.Streaming;

// The state guard every State write goes through, and the event raise every event goes through.
public sealed partial class StreamingTranscriptionClient
{
    // internal (not private): test seam for HyperWhisper.SmokeTests via InternalsVisibleTo (see
    // HyperWhisper.csproj). A freshly constructed client's State defaults to Idle, which
    // HandleCloseResult's own shutdown guard treats as "already torn down" and no-ops on - so
    // exercising HandleCloseResult in isolation needs a way to put the client into a realistic
    // pre-close state (e.g. Streaming) first without going through a real WebSocket connect.
    internal void SetStateForTesting(StreamingConnectionState state) => ChangeState(state);

    // Single chokepoint for every State mutation in this class - the one discipline every call
    // site (ChangeState/TryChangeState/TryChangeStateUnless, and HandleCloseResult's inline guard)
    // routes through instead of each hand-rolling its own lock+check+set+raise body. `guard`
    // decides whether the transition from the current state to `next` is allowed; it is not
    // consulted (and the transition trivially "succeeds") when State already equals `next`, so
    // repeated/racing attempts to reach the same target state are idempotent instead of spuriously
    // failing - this is what lets StartAsync's own Connecting -> Streaming attempt and
    // HandleProviderEvent's SessionStarted-driven attempt for a SessionStartsOnWebSocketOpen
    // provider (e.g. Deepgram's Metadata frame) race safely: whichever lands first wins, and the
    // other observes success rather than a false "conflict".
    //
    // `onSuccess`, if given, runs exactly once when the transition succeeds (whether by actually
    // writing State or via the "already there" no-op) and always BEFORE StateChanged is raised for
    // an actual write - so a caller like HandleCloseResult can use it to finish bookkeeping
    // (failure message, _receivedTerminalClose, the startup-failure exception) that a subscriber
    // reacting to StateChanged, or a caller that just observed State == next, depends on already
    // being populated. It does not run when `guard` rejects the transition.
    private bool TryChangeStateCore(Func<StreamingConnectionState, bool> guard, StreamingConnectionState next, Action? onSuccess = null)
    {
        bool alreadyThere;

        lock (_stateLock)
        {
            alreadyThere = State == next;
            if (!alreadyThere)
            {
                if (!guard(State))
                    return false;

                State = next;
            }
        }

        onSuccess?.Invoke();

        if (!alreadyThere)
            Raise(StateChanged, next);

        return true;
    }

    // Unconditional transition. Safe to call from a single call site that only ever runs on one
    // logical thread of execution at a time (e.g. StopAsync's own sequential shutdown). Anywhere
    // a background thread (the receive loop) can concurrently observe/mutate State while the
    // caller is mid-await, use TryChangeState/TryChangeStateUnless instead so the check-then-act
    // is atomic.
    private void ChangeState(StreamingConnectionState state) => TryChangeStateCore(_ => true, state);

    // Atomically transitions State from `expected` to `next` (or treats State already being `next`
    // as success - see TryChangeStateCore). Returns whether the transition landed - callers that
    // only expected to move State forward from a specific prior state (e.g. StartAsync's
    // Connecting -> Streaming, TryReconnectAsync's Reconnecting -> Streaming/Ready) use the return
    // value to detect that something else (typically HandleCloseResult moving to Error off the
    // receive-loop thread) already moved State elsewhere in the gap since it was last read,
    // instead of blindly clobbering that concurrent transition.
    private bool TryChangeState(StreamingConnectionState expected, StreamingConnectionState next) =>
        TryChangeStateCore(current => current == expected, next);

    // Atomically transitions State to `next` unless State currently equals one of
    // `excludedStates` (or is already `next` - see TryChangeStateCore). Used where the guard is
    // "don't overwrite these specific states" rather than "only proceed from this one expected
    // state" - e.g. HandleCloseResult and the in-band provider-error path moving to Error unless
    // StopAsync has already moved State to Disconnecting/Idle out from under them. Returns whether
    // the transition landed - including the case where State was already `next` (e.g. a terminal
    // close arriving after an in-band provider error already set Error), so bookkeeping that only
    // makes sense once is still driven by the return value, not by "did we actually write".
    private bool TryChangeStateUnless(StreamingConnectionState next, params StreamingConnectionState[] excludedStates) =>
        TryChangeStateCore(current => Array.IndexOf(excludedStates, current) < 0, next);

    // Single chokepoint for every event raise. Drops all callbacks when _suppressDispatch is set
    // (synchronous Dispose() teardown) so no subscriber can marshal back to a blocked caller, and
    // invokes each subscriber independently so one throwing handler cannot starve the rest.
    private void Raise<T>(Action<T>? handler, T arg)
    {
        if (_suppressDispatch || handler == null)
            return;

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((Action<T>)target)(arg);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: event handler threw - {ex.Message}");
            }
        }
    }

    private void Raise(Action<double, double>? handler, double arg1, double arg2)
    {
        if (_suppressDispatch || handler == null)
            return;

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((Action<double, double>)target)(arg1, arg2);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: event handler threw - {ex.Message}");
            }
        }
    }
}
