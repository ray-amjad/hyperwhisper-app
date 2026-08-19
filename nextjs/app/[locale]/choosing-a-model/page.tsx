import { notFound } from "next/navigation";

import ModelPicker from "@/components/choosing-a-model/ModelPicker";
import { getMeasuredLatency } from "@/src/content/choosing-a-model";
import { CLOUD_MODELS, DEVICE_MODELS } from "@/lib/choosing-a-model/catalog";

export const dynamic = "force-static";
export const revalidate = 3600;

const TITLE = "Choosing a speech-to-text model";
const DESCRIPTION =
  "Split 100 points across accuracy, speed, cost and privacy, and see which of the models HyperWhisper ships actually fits — cloud and on-device, ranked side by side.";

export async function generateMetadata() {
  return {
    title: TITLE,
    description: DESCRIPTION,
    alternates: {
      canonical: "https://hyperwhisper.com/en/choosing-a-model",
    },
  };
}

type Props = {
  params: Promise<{ locale: string }>;
};

/**
 * The model chooser. English only, like /latency and the blog — the page is
 * mostly numbers, and translating 40 copies of them buys nothing.
 *
 * Static with an hourly revalidate. The measured timings behind it are a 90-day
 * aggregate, so an hour-old page tells the same story as a fresh one, and the
 * one thing that really is per-visitor — which region they are closest to — is
 * fetched by the client from a small dynamic endpoint after paint.
 */
export default async function ChoosingAModelPage({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  const measured = await getMeasuredLatency();

  // Busiest regions first, so the default selection is a region we know well
  // and the dropdown opens on something useful even if geolocation is blocked.
  const regions = Object.keys(measured).sort(
    (a, b) => Object.keys(measured[b]).length - Object.keys(measured[a]).length,
  );

  return (
    <div className="w-full py-16 md:py-24">
      <header className="mx-auto max-w-3xl text-center">
        <h1 className="bg-gradient-to-r from-white to-gray-400 bg-clip-text text-4xl font-bold text-transparent md:text-5xl">
          You have 100 points. Spend them.
        </h1>
        <p className="mx-auto mt-6 text-lg text-gray-400">
          Every speech-to-text trade-off is zero-sum — the most accurate model is
          rarely the cheapest or the fastest, and the most private one runs on
          your own hardware. So instead of rating everything &ldquo;very
          important&rdquo;, split 100 points across four priorities. Push one up
          and the others give way.
        </p>
        <p className="mx-auto mt-4 text-sm text-gray-500">
          We rank the {CLOUD_MODELS.length} cloud models and{" "}
          {DEVICE_MODELS.length} on-device models HyperWhisper actually ships
          across macOS and Windows — not a general leaderboard. Pick your
          platform below and the list narrows to what you can run.
        </p>
      </header>

      <ModelPicker measured={measured} regions={regions} />

      <section className="mx-auto mt-20 max-w-3xl border-t border-gray-800 pt-10">
        <h2 className="text-xl font-semibold text-white">
          Cloud and on-device are different bargains
        </h2>
        <p className="mt-4 text-sm leading-6 text-gray-400">
          Every row on this page is one or the other, and the badge says which.
          The distinction is not a detail — it changes what you pay, what you
          wait for, and where your voice ends up.
        </p>
        <div className="mt-6 grid gap-4 md:grid-cols-2">
          <div className="rounded-lg border border-sky-800/50 bg-sky-950/20 p-5">
            <h3 className="text-sm font-semibold text-sky-300">Cloud models</h3>
            <p className="mt-2 text-sm leading-6 text-gray-400">
              Your audio is uploaded to HyperWhisper Cloud, which hands it to the
              provider and sends the text back. You get the strongest accuracy
              available and no download, and you pay per audio minute in credits.
              These are the models with published error rates, because a hosted
              model is something a third party can measure.
            </p>
          </div>
          <div className="rounded-lg border border-emerald-800/50 bg-emerald-950/20 p-5">
            <h3 className="text-sm font-semibold text-emerald-300">
              On-device models
            </h3>
            <p className="mt-2 text-sm leading-6 text-gray-400">
              The model is downloaded once and runs on your own Mac or PC. The
              audio never leaves the machine, there is nothing to pay per minute,
              and it works with no network at all. What you give up is the top of
              the accuracy table, and a few gigabytes of disk.
            </p>
          </div>
        </div>
      </section>

      <section className="mx-auto mt-16 max-w-3xl border-t border-gray-800 pt-10">
        <h2 className="text-xl font-semibold text-white">
          Where these numbers come from
        </h2>
        <dl className="mt-6 space-y-6 text-sm leading-6 text-gray-400">
          <div>
            <dt className="font-medium text-gray-200">Accuracy</dt>
            <dd className="mt-1">
              Word error rate comes from the{" "}
              <a
                className="text-purple-300 underline underline-offset-4 transition hover:text-purple-200"
                href="https://artificialanalysis.ai/speech-to-text/non-streaming"
                rel="noopener noreferrer"
                target="_blank"
              >
                Artificial Analysis speech-to-text leaderboard
              </a>{" "}
              — an independent third party, not us. We do not publish an accuracy
              claim of our own here. A model they have not measured shows a dash
              and is marked <span className="text-gray-200">not benchmarked</span>;
              it scores neutrally rather than being flattered by a number we
              invented.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">
              Why some on-device models borrow a cloud model&apos;s score
            </dt>
            <dd className="mt-1">
              Whisper and Parakeet are open weights. The leaderboard measured the
              same weight files we download, just running on someone else&apos;s
              hardware — so the error rate carries over and is marked{" "}
              <span className="text-gray-200">same weights</span>. Speed does not
              carry over, which is why those rows never borrow a speed figure.
              The remaining local models have no published benchmark at all and
              fall back to the app&apos;s own accuracy rating, marked{" "}
              <span className="text-gray-200">app rating</span>. Without that
              fallback the smallest, roughest models would score like frontier
              ones purely for being free and private.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Cost</dt>
            <dd className="mt-1">
              Credits per audio minute, read from the same catalog the desktop
              apps read, so the price here is the price the app charges you.
              1,000 credits are $1, which makes a model at 4.5 credits a minute
              $4.50 per 1,000 minutes of audio. On-device models cost nothing to
              run, so they score full marks on cost no matter how you weight it.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Speed</dt>
            <dd className="mt-1">
              Where we have measured a provider from your nearest region, the
              number is our own median from the last 90 days — the same
              measurements behind{" "}
              <a
                className="text-purple-300 underline underline-offset-4 transition hover:text-purple-200"
                href="/en/latency"
              >
                the latency page
              </a>
              , taken from short clips because that is what dictation is. Those
              rows carry a dot. Everything else falls back to the
              leaderboard&apos;s published speed factor. On-device timings are an
              estimate from the app&apos;s own speed rating and depend on your
              hardware, so read them as an ordering, not a promise.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Privacy</dt>
            <dd className="mt-1">
              Three steps, by where the audio goes. On-device scores full marks:
              nothing is transmitted. A model you can use with your own API key
              scores half: the audio still reaches the vendor, but on your
              account rather than through us. A cloud-tier-only model scores
              lowest. Turn the privacy slider up and the ranking moves to
              on-device models, which is the honest answer to that question.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Your region</dt>
            <dd className="mt-1">
              HyperWhisper Cloud runs in 17 regions and routes you to the nearest
              one. We ask a small endpoint which of those you are closest to so
              the speed numbers are the ones you would actually get; the answer
              is used once to pick a row and is never stored. You can override it
              with the dropdown. Off our edge — a local dev server, say — nothing
              is detected and the busiest region is selected instead.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">
              What this page will not tell you
            </dt>
            <dd className="mt-1">
              A ranking is not a recommendation for a job it has never seen. If
              you dictate medical or legal terms, transcribe heavy accents, or
              need speaker labels, the model that wins here on paper may still
              lose on your audio. Every model listed is switchable in Settings →
              Transcription, so the last word is a minute of your own speech, not
              a number on a website.
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}
