using LLama;
using LLama.Abstractions;

namespace HyperWhisper.Services;

/// <summary>
/// An <see cref="InteractiveExecutor"/> that cannot end its stream while it is
/// still reading the prompt (issue #2559).
/// </summary>
/// <remarks>
/// LLamaSharp 0.27 feeds the prompt in batches of <c>BatchSize</c> (512) tokens
/// and, after queueing each batch, ends generation when the batch's LAST token
/// is an end-of-generation token. Gemma 4's turn close <c>&lt;turn|&gt;</c> is
/// one, and the prompt holds two of them, so whenever one lands on a batch
/// boundary the stream ends with no output after the prefill. The default
/// hyper Mode with a short dictation did exactly that on the Surface: the user
/// turn's <c>&lt;turn|&gt;</c> was token 2559, the last of the fifth batch.
/// Until the first token is sampled (<c>ReturnValue</c> is still false) there is
/// nothing to end, so the check waits; from then on the base logic runs as is,
/// including the end of generation it relies on after a real EOS.
/// The Linux head keeps the same class in
/// <c>app/shared-dotnet/HyperWhisper.LocalPostProcessing/PromptSafeInteractiveExecutor.cs</c>; change both.
/// </remarks>
internal sealed class PromptSafeInteractiveExecutor(LLamaContext context) : InteractiveExecutor(context)
{
    private static readonly IReadOnlyList<string> NoExtraOutputs = [];

    protected override Task<(bool, IReadOnlyList<string>)> PostProcess(
        IInferenceParams inferenceParams, InferStateArgs args, CancellationToken cancellationToken = default)
    {
        return args.ReturnValue
            ? base.PostProcess(inferenceParams, args, cancellationToken)
            : Task.FromResult((false, NoExtraOutputs));
    }
}
