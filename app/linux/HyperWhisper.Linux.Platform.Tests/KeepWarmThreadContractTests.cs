using HyperWhisper.Linux.Platform.Audio;

// #2818: keep-warm's Configure, SuspendForRecording and ResumeAfterRecording share `_enabled`, `_suspended`
// and `_source` with no lock, because every caller runs on the Avalonia UI thread. These drive the issue's
// steps in the order that one thread runs them: the user's settings change lands between the recording's
// suspend and the finalizer's resume, never inside either call.
static class KeepWarmThreadContractTests
{
    // Keep-warm on, a recording starts, the user turns keep-warm off while it finalizes: the restore must
    // not reopen the microphone, and neither may the quit's second resume (#1038).
    public static Task TurnedOffWhileRecordingStaysOff()
    {
        var warm = new FakeStreamingAudioSource(new BlockingAudioStream());
        var spare = new FakeStreamingAudioSource(new BlockingAudioStream());
        var factory = new CyclingStreamingSourceFactory(warm, spare);
        var service = new LinuxMicrophoneKeepWarmService(factory);
        service.Configure(true, "mic"); Assert.Equal(1, factory.OpenCalls);
        service.SuspendForRecording(); Assert.Equal(1, warm.TerminateCalls);
        service.Configure(false, "mic");
        service.ResumeAfterRecording("mic"); service.ResumeAfterRecording("mic");
        Assert.Equal(1, factory.OpenCalls); Assert.Equal("mic", string.Join('|', factory.Devices));
        service.Dispose();
        Assert.Equal(1, warm.TerminateCalls); Assert.Equal(0, spare.TerminateCalls);
        return Task.CompletedTask;
    }

    // The converse: keep-warm off, a recording starts, the user turns it on mid-recording. Nothing opens
    // while the recording holds the mic; the restore opens exactly one source, on the device it resumes
    // with, and turning keep-warm off afterwards closes that source.
    public static Task TurnedOnWhileRecordingOpensOnceOnResume()
    {
        var warm = new FakeStreamingAudioSource(new BlockingAudioStream());
        var spare = new FakeStreamingAudioSource(new BlockingAudioStream());
        var factory = new CyclingStreamingSourceFactory(warm, spare);
        var service = new LinuxMicrophoneKeepWarmService(factory);
        service.Configure(false, "mic"); service.SuspendForRecording();
        service.Configure(true, "mic"); Assert.Equal(0, factory.OpenCalls);
        service.ResumeAfterRecording("mic2"); service.ResumeAfterRecording("mic2");
        Assert.Equal(1, factory.OpenCalls); Assert.Equal("mic2", string.Join('|', factory.Devices));
        service.Configure(false, "mic2");
        Assert.Equal(1, warm.TerminateCalls); Assert.Equal(1, factory.OpenCalls);
        service.Dispose();
        Assert.Equal(1, warm.TerminateCalls); Assert.Equal(0, spare.TerminateCalls);
        return Task.CompletedTask;
    }
}
