using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Audio;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HyperWhisper.Linux;

internal sealed record SileroInferenceResult(float SpeechProbability, float[] State);

internal interface ISileroInferenceSession : IDisposable
{
    PlatformResult<SileroInferenceResult> Run(float[] frame, float[] state);
}

internal sealed class OnnxSileroInferenceSession : ISileroInferenceSession
{
    private readonly InferenceSession _session;

    public OnnxSileroInferenceSession(string modelPath)
    {
        if (!Path.IsPathFullyQualified(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("The packaged Silero VAD model is unavailable.");
        var options = new SessionOptions
        {
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
        };
        _session = new InferenceSession(modelPath, options);
        if (!_session.InputMetadata.Keys.Order().SequenceEqual(new[] { "c", "h", "x" })
            || !_session.OutputMetadata.Keys.Order().SequenceEqual(new[] { "new_c", "new_h", "prob" }))
            throw new InvalidDataException("The packaged Silero VAD model has an unsupported tensor contract.");
    }

    public PlatformResult<SileroInferenceResult> Run(float[] frame, float[] state)
    {
        try
        {
            var hidden = state[..128];
            var cell = state[128..];
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(frame, [1, 512])),
                NamedOnnxValue.CreateFromTensor("h", new DenseTensor<float>(hidden, [2, 1, 64])),
                NamedOnnxValue.CreateFromTensor("c", new DenseTensor<float>(cell, [2, 1, 64])),
            };
            using var outputs = _session.Run(inputs);
            var probability = outputs.First(item => item.Name == "prob").AsEnumerable<float>().First();
            var nextHidden = outputs.First(item => item.Name == "new_h").AsEnumerable<float>();
            var nextCell = outputs.First(item => item.Name == "new_c").AsEnumerable<float>();
            var nextState = nextHidden.Concat(nextCell).ToArray();
            return nextState.Length == 256 && float.IsFinite(probability)
                ? PlatformResult<SileroInferenceResult>.Success(new(probability, nextState))
                : PlatformResult<SileroInferenceResult>.Failure("vad.silero_output_invalid", "Silero VAD returned an invalid tensor.");
        }
        catch (OnnxRuntimeException)
        {
            return PlatformResult<SileroInferenceResult>.Failure("vad.silero_inference_failed", "Silero VAD inference failed.");
        }
    }

    public void Dispose() => _session.Dispose();
}

internal sealed class SileroVoiceActivityDetector(
    ISileroInferenceSession session,
    float threshold = 0.5f) : IVoiceActivityDetector, IDisposable
{
    private readonly ISileroInferenceSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly float _threshold = threshold is > 0 and < 1 ? threshold : throw new ArgumentOutOfRangeException(nameof(threshold));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private float[] _state = new float[256];
    private bool _disposed;

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Array.Clear(_state); }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<bool>> ContainsSpeechAsync(
        ReadOnlyMemory<float> mono16KhzPcm,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mono16KhzPcm.Length is 0 or > 512)
            return PlatformResult<bool>.Failure("vad.window_invalid", "Silero VAD requires a bounded 512-sample frame.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = new float[512];
            mono16KhzPcm.CopyTo(frame);
            var inferred = _session.Run(frame, _state);
            cancellationToken.ThrowIfCancellationRequested();
            if (inferred.IsFailure)
                return PlatformResult<bool>.Failure(inferred.Error!.Code, inferred.Error.Message);
            _state = inferred.Value!.State;
            return PlatformResult<bool>.Success(inferred.Value.SpeechProbability >= _threshold);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _gate.Dispose();
    }
}

internal sealed class FallbackVoiceActivityDetector(
    IVoiceActivityDetector primary,
    IVoiceActivityDetector fallback) : IVoiceActivityDetector
{
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        await primary.ResetAsync(cancellationToken);
        await fallback.ResetAsync(cancellationToken);
    }

    public async ValueTask<PlatformResult<bool>> ContainsSpeechAsync(
        ReadOnlyMemory<float> mono16KhzPcm,
        CancellationToken cancellationToken = default)
    {
        var result = await primary.ContainsSpeechAsync(mono16KhzPcm, cancellationToken);
        return result.IsSuccess ? result : await fallback.ContainsSpeechAsync(mono16KhzPcm, cancellationToken);
    }
}
